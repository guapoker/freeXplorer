using System.Text;

namespace FreeXplorer.Fatx;

public sealed partial class FatxVolume
{
    // ---- lookups ----

    public IReadOnlyList<FatxNode> GetChildren(FatxNode directory)
    {
        lock (sync)
        {
            RequireDirectory(directory);
            RequireLive(directory);
            return GetListing(directory).Slots.Where(n => n is not null).Select(n => n!).ToList();
        }
    }

    public FatxNode? FindChild(FatxNode directory, string name)
    {
        lock (sync)
        {
            RequireDirectory(directory);
            RequireLive(directory);
            return GetListing(directory).ByName.GetValueOrDefault(name);
        }
    }

    // Accepts either slash. Returns null when something along the way is missing.
    public FatxNode? Find(string path)
    {
        lock (sync)
        {
            var node = Root;
            foreach (var part in FatxName.SplitPath(path))
            {
                if (!node.IsDirectory)
                    return null;
                var next = GetListing(node).ByName.GetValueOrDefault(part);
                if (next is null)
                    return null;
                node = next;
            }
            return node;
        }
    }

    public FatxNode Resolve(string path) =>
        Find(path) ?? throw Fail(FatxError.NotFound, $"{path} doesn't exist");

    // ---- changes ----

    // the console doesn't set the archive bit on files it creates, so neither do we
    public FatxNode CreateFile(FatxNode parent, string name) => Create(parent, name, FatxAttributes.None);

    public FatxNode CreateDirectory(FatxNode parent, string name) => Create(parent, name, FatxAttributes.Directory);

    // makes every missing directory along the path, returns the last one
    public FatxNode CreateDirectories(string path)
    {
        lock (sync)
        {
            var node = Root;
            foreach (var part in FatxName.SplitPath(path))
            {
                var next = GetListing(node).ByName.GetValueOrDefault(part);
                if (next is null)
                    next = CreateDirectory(node, part);
                else if (!next.IsDirectory)
                    throw Fail(FatxError.NotADirectory, $"{next.Path} is a file");
                node = next;
            }
            return node;
        }
    }

    FatxNode Create(FatxNode parent, string name, FatxAttributes attributes)
    {
        lock (sync)
        {
            RequireWritable();
            RequireDirectory(parent);
            RequireLive(parent);
            FatxName.Require(name);
            if (parent.Path.TrimEnd('\\').Length + 1 + name.Length > FatxName.MaxPathLength)
                throw Fail(FatxError.PathTooLong, "paths on FATX can't be longer than 240 characters");

            var listing = GetListing(parent);
            if (listing.ByName.ContainsKey(name))
                throw Fail(FatxError.AlreadyExists, $"{name} already exists");

            // find the slot first so a full directory fails before we allocate anything
            int slot = ReserveSlot(parent, listing);

            var node = new FatxNode(Flavor, name, attributes);
            uint now = NowRaw();
            node.CreatedRaw = node.ModifiedRaw = node.AccessedRaw = now;

            if (node.IsDirectory)
            {
                node.Map = new ClusterMap();
                Extend(node, node.Map, 1);
                var blank = new byte[ClusterSize];
                Array.Fill(blank, (byte)0xFF);
                device.Write(Geometry.ClusterOffset(node.FirstCluster), blank);
                fat.Flush();
            }

            Attach(parent, listing, node, slot);
            return node;
        }
    }

    public void Delete(FatxNode node)
    {
        lock (sync)
        {
            RequireWritable();
            RequireLive(node);
            if (node.Parent is null)
                throw Fail(FatxError.InvalidOperation, "can't delete the root directory");
            if (node.IsDirectory && GetListing(node).ByName.Count > 0)
                throw Fail(FatxError.DirectoryNotEmpty, $"{node.Path} isn't empty");

            Detach(node);
            node.IsDeleted = true;
            dirty.Remove(node);
            FreeChain(node.FirstCluster);
            node.FirstCluster = 0;
            node.Map = null;
            fat.Flush();
        }
    }

    // Moves and/or renames. replaceExisting only ever replaces a plain file.
    public void Rename(FatxNode node, FatxNode newParent, string newName, bool replaceExisting = false)
    {
        lock (sync)
        {
            RequireWritable();
            RequireLive(node);
            RequireDirectory(newParent);
            RequireLive(newParent);
            if (node.Parent is null)
                throw Fail(FatxError.InvalidOperation, "can't rename the root directory");
            FatxName.Require(newName);
            int below = node.IsDirectory ? DeepestBelow(node) : 0;
            if (newParent.Path.TrimEnd('\\').Length + 1 + newName.Length + below > FatxName.MaxPathLength)
                throw Fail(FatxError.PathTooLong, "paths on FATX can't be longer than 240 characters");

            if (node.IsDirectory)
            {
                for (var p = newParent; p is not null; p = p.Parent)
                    if (p == node)
                        throw Fail(FatxError.InvalidOperation, "can't move a directory into itself");
            }

            var targetListing = GetListing(newParent);
            bool sameDir = newParent == node.Parent;

            if (targetListing.ByName.TryGetValue(newName, out var existing) && existing != node)
            {
                if (!replaceExisting)
                    throw Fail(FatxError.AlreadyExists, $"{newName} already exists");
                if (existing.IsDirectory || node.IsDirectory)
                    throw Fail(FatxError.InvalidOperation, "a directory can't replace or be replaced");
                Delete(existing);
            }

            if (sameDir)
            {
                var listing = targetListing;
                listing.ByName.Remove(node.Name);
                node.Name = newName;
                listing.ByName[newName] = node;
                WriteEntry(node);
                return;
            }

            int slot = ReserveSlot(newParent, targetListing);
            var oldListing = GetListing(node.Parent!);
            int oldSlot = node.Slot;
            string oldName = node.Name;

            node.Name = newName;
            Attach(newParent, targetListing, node, slot);

            // the new entry is on disk by now, so retire the old one
            device.Write(SlotOffset(oldListing, oldSlot), new byte[] { 0xE5 });
            oldListing.Slots[oldSlot] = null;
            oldListing.ByName.Remove(oldName);
        }
    }

    public void SetAttributes(FatxNode node, FatxAttributes attributes)
    {
        lock (sync)
        {
            RequireWritable();
            RequireLive(node);
            if (node.Parent is null)
                return;
            var keep = node.Attributes & (FatxAttributes.Directory | FatxAttributes.Volume);
            node.Attributes = keep | (attributes & ~(FatxAttributes.Directory | FatxAttributes.Volume));
            WriteEntry(node);
        }
    }

    public void SetTimes(FatxNode node, DateTime? created, DateTime? modified, DateTime? accessed)
    {
        lock (sync)
        {
            RequireWritable();
            RequireLive(node);
            if (node.Parent is null)
                return;
            if (created is { } c) node.CreatedRaw = FatxTime.Encode(c, Flavor);
            if (modified is { } m) node.ModifiedRaw = FatxTime.Encode(m, Flavor);
            if (accessed is { } a) node.AccessedRaw = FatxTime.Encode(a, Flavor);
            WriteEntry(node);
        }
    }

    // ---- directory plumbing ----

    // length of the longest path under a directory, counted from the directory itself
    int DeepestBelow(FatxNode directory)
    {
        int deepest = 0;
        var pending = new Stack<(FatxNode Dir, int Length)>();
        pending.Push((directory, 0));
        while (pending.Count > 0)
        {
            var (dir, length) = pending.Pop();
            foreach (var child in GetListing(dir).Slots)
            {
                if (child is null)
                    continue;
                int childLength = length + 1 + child.Name.Length;
                deepest = Math.Max(deepest, childLength);
                if (child.IsDirectory)
                    pending.Push((child, childLength));
            }
        }
        return deepest;
    }

    Listing GetListing(FatxNode directory)
    {
        directory.Children ??= LoadListing(directory);
        return directory.Children;
    }

    Listing LoadListing(FatxNode directory)
    {
        var map = EnsureMap(directory);
        var listing = new Listing();
        foreach (var e in map.Extents)
            for (long i = 0; i < e.Length; i++)
                listing.Clusters.Add((uint)(e.Disk + i));

        var buffer = new byte[ClusterSize];
        bool finished = false;
        foreach (uint cluster in listing.Clusters)
        {
            device.Read(Geometry.ClusterOffset(cluster), buffer);
            for (int i = 0; i < entriesPerCluster; i++)
            {
                var raw = buffer.AsSpan(i * EntrySize, EntrySize);
                byte len = raw[0];
                if (len == 0x00 || len == 0xFF)
                {
                    finished = true;
                    break;
                }

                int slot = listing.Slots.Count;
                if (len == 0xE5 || len > FatxName.MaxLength)
                {
                    // deleted, or junk we can't use. leave the slot free
                    listing.Slots.Add(null);
                    continue;
                }

                var node = ParseEntry(raw, len);
                node.Parent = directory;
                node.Slot = slot;
                listing.Slots.Add(node);
                // two entries with the same name shouldn't happen. keep the first.
                listing.ByName.TryAdd(node.Name, node);
            }
            if (finished)
                break;
        }
        return listing;
    }

    FatxNode ParseEntry(ReadOnlySpan<byte> raw, int nameLength)
    {
        var name = Encoding.Latin1.GetString(raw.Slice(2, nameLength));
        var node = new FatxNode(Flavor, name, (FatxAttributes)raw[1])
        {
            FirstCluster = ReadU32(raw[0x2C..]),
            Size = ReadU32(raw[0x30..]),
        };
        if (Flavor == FatxFlavor.Xbox360)
        {
            node.CreatedRaw = ReadU32(raw[0x34..]);
            node.ModifiedRaw = ReadU32(raw[0x38..]);
        }
        else
        {
            node.ModifiedRaw = ReadU32(raw[0x34..]);
            node.CreatedRaw = ReadU32(raw[0x38..]);
        }
        node.AccessedRaw = ReadU32(raw[0x3C..]);
        if (node.IsDirectory)
            node.Size = 0;
        return node;
    }

    void BuildEntry(FatxNode node, Span<byte> raw)
    {
        raw.Fill(0xFF);
        var name = Encoding.Latin1.GetBytes(node.Name);
        raw[0] = (byte)name.Length;
        raw[1] = (byte)node.Attributes;
        name.CopyTo(raw[2..]);
        WriteU32(raw[0x2C..], node.FirstCluster);
        WriteU32(raw[0x30..], node.IsDirectory ? 0 : (uint)node.Size);
        if (Flavor == FatxFlavor.Xbox360)
        {
            WriteU32(raw[0x34..], node.CreatedRaw);
            WriteU32(raw[0x38..], node.ModifiedRaw);
        }
        else
        {
            WriteU32(raw[0x34..], node.ModifiedRaw);
            WriteU32(raw[0x38..], node.CreatedRaw);
        }
        WriteU32(raw[0x3C..], node.AccessedRaw);
    }

    long SlotOffset(Listing listing, int slot) =>
        Geometry.ClusterOffset(listing.Clusters[slot / entriesPerCluster]) + (long)(slot % entriesPerCluster) * EntrySize;

    void WriteEntry(FatxNode node)
    {
        if (node.Parent is null || node.IsDeleted)
            return;
        var listing = GetListing(node.Parent);
        Span<byte> raw = stackalloc byte[EntrySize];
        BuildEntry(node, raw);
        device.Write(SlotOffset(listing, node.Slot), raw);
    }

    // Picks the slot a new entry will go in: the first deleted one, or the end of the
    // directory. Adds a cluster to the directory if it's out of room.
    int ReserveSlot(FatxNode directory, Listing listing)
    {
        int free = listing.Slots.IndexOf(null);
        if (free >= 0)
            return free;

        int slot = listing.Slots.Count;
        if (slot >= MaxEntriesPerDirectory)
            throw Fail(FatxError.DirectoryFull, $"{directory.Path} already has the maximum {MaxEntriesPerDirectory} entries");

        if (slot >= listing.Clusters.Count * entriesPerCluster)
        {
            var map = EnsureMap(directory);
            Extend(directory, map, 1);
            uint added = map.LastCluster;
            var blank = new byte[ClusterSize];
            Array.Fill(blank, (byte)0xFF);
            device.Write(Geometry.ClusterOffset(added), blank);
            fat.Flush();
            listing.Clusters.Add(added);
        }
        return slot;
    }

    void Attach(FatxNode parent, Listing listing, FatxNode node, int slot)
    {
        node.Parent = parent;
        node.Slot = slot;
        if (slot == listing.Slots.Count)
            listing.Slots.Add(node);
        else
            listing.Slots[slot] = node;
        listing.ByName[node.Name] = node;
        WriteEntry(node);
    }

    // Takes a node out of its parent's listing and marks the slot deleted on disk
    void Detach(FatxNode node)
    {
        var listing = GetListing(node.Parent!);
        device.Write(SlotOffset(listing, node.Slot), new byte[] { 0xE5 });
        listing.Slots[node.Slot] = null;
        listing.ByName.Remove(node.Name);
    }
}
