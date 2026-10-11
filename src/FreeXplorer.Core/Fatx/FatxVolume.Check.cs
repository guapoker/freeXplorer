namespace FreeXplorer.Fatx;

public sealed partial class FatxVolume
{
    // A quick look at the first root directory cluster. If the geometry is off, that read
    // lands somewhere else entirely and you get zeros or file data instead of entries.
    internal bool RootLooksSane()
    {
        lock (sync)
        {
            try
            {
                var buffer = new byte[ClusterSize];
                device.Read(Geometry.ClusterOffset(Root.FirstCluster), buffer);
                for (int i = 0; i < entriesPerCluster; i++)
                {
                    var e = buffer.AsSpan(i * EntrySize, EntrySize);
                    byte len = e[0];
                    if (len == 0xFF)
                        return true;                    // blank, or we hit the end of the entries
                    if (len == 0x00)
                        return i > 0;                   // zeros right at the start is what a misplaced read looks like
                    if (len == 0xE5)
                        continue;
                    if (len > FatxName.MaxLength)
                        return false;
                    for (int k = 0; k < len; k++)
                        if (e[2 + k] < 0x20 || e[2 + k] > 0x7E)
                            return false;
                    if (ReadU32(e[0x2C..]) > Geometry.ClusterCount)
                        return false;
                }
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    // Walks the whole tree and the whole FAT and reports what doesn't add up. With
    // freeOrphans set, clusters that are marked used but that no file owns get released.
    // It re-reads every directory from disk, so nodes handed out earlier are stale afterwards.
    public FsckReport Check(bool freeOrphans = false, Action<string>? status = null)
    {
        lock (sync)
        {
            var report = new FsckReport();
            uint count = Geometry.ClusterCount;
            var owned = new ulong[(count + 64) / 64];
            bool Test(uint c) => (owned[c >> 6] & (1UL << (int)(c & 63))) != 0;
            void Mark(uint c) => owned[c >> 6] |= 1UL << (int)(c & 63);

            long ownedCount = 0;

            // returns how many clusters the chain has, or -1 if it's broken
            long Walk(string path, uint first)
            {
                long length = 0;
                uint c = first;
                while (true)
                {
                    if (c < 1 || c > count)
                    {
                        report.Add(FsckSeverity.Error, $"{path}: chain points at cluster {c}, which is outside the volume");
                        return -1;
                    }
                    if (Test(c))
                    {
                        report.Add(FsckSeverity.Error, $"{path}: cluster {c} is already used by another file or loops back");
                        return -1;
                    }
                    Mark(c);
                    ownedCount++;
                    length++;
                    uint next = fat.Get(c);
                    if (next >= Geometry.EndThreshold)
                        return length;
                    if (next == 0 || next == Geometry.BadCluster)
                    {
                        report.Add(FsckSeverity.Error, $"{path}: chain runs into a free or bad cluster after {c}");
                        return -1;
                    }
                    c = next;
                }
            }

            var pending = new Stack<FatxNode>();
            pending.Push(Root);
            long rootChain = Walk("\\", Root.FirstCluster);
            if (rootChain < 0)
                pending.Clear();

            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                status?.Invoke(dir.Path);
                report.Directories++;

                Listing listing;
                try
                {
                    dir.Map = null;
                    dir.Children = null;
                    listing = GetListing(dir);
                }
                catch (FatxException ex)
                {
                    report.Add(FsckSeverity.Error, $"{dir.Path}: can't read the directory ({ex.Message})");
                    continue;
                }

                if (listing.Slots.Count > MaxEntriesPerDirectory)
                    report.Add(FsckSeverity.Warning, $"{dir.Path}: has {listing.Slots.Count} entries, the console stops at {MaxEntriesPerDirectory}");

                foreach (var child in listing.Slots)
                {
                    if (child is null)
                        continue;
                    string path = child.Path;

                    if (!FatxName.IsValid(child.Name))
                        report.Add(FsckSeverity.Warning, $"{path}: the name has characters the console may not like");

                    if (child.IsDirectory)
                    {
                        if (child.FirstCluster == 0)
                        {
                            report.Add(FsckSeverity.Error, $"{path}: directory has no cluster");
                            continue;
                        }
                        if (Walk(path, child.FirstCluster) >= 0)
                            pending.Push(child);
                        continue;
                    }

                    report.Files++;
                    report.FileBytes += child.Size;
                    long needed = (child.Size + ClusterSize - 1) / ClusterSize;

                    if (child.FirstCluster == 0)
                    {
                        if (child.Size > 0)
                            report.Add(FsckSeverity.Error, $"{path}: has {child.Size} bytes but no clusters");
                        continue;
                    }

                    long have = Walk(path, child.FirstCluster);
                    if (have < 0)
                        continue;
                    if (have < needed)
                        report.Add(FsckSeverity.Error, $"{path}: size needs {needed} clusters but the chain has {have}");
                    else if (have > needed)
                        report.Add(FsckSeverity.Warning, $"{path}: chain has {have - needed} more clusters than the size needs");
                }
            }

            report.UsedClusters = ownedCount;

            // anything the FAT calls used that nobody owns
            long orphans = 0;
            for (uint c = 1; c <= count; c++)
            {
                if (Test(c) || fat.Get(c) == 0)
                    continue;
                orphans++;
                if (freeOrphans && !IsReadOnly)
                {
                    SetFat(c, 0);
                    report.FixedClusters++;
                }
            }
            report.OrphanClusters = orphans;
            if (orphans > 0)
            {
                string tail = freeOrphans && !IsReadOnly ? " (released)" : "";
                report.Add(FsckSeverity.Warning, $"{orphans} clusters are marked used but belong to nothing{tail}");
            }
            if (freeOrphans && !IsReadOnly)
                fat.Flush();

            return report;
        }
    }
}
