namespace FreeXplorer.Fatx;

// A file's cluster chain squashed into runs of consecutive clusters. Most files on a
// freshly written drive are one or two runs, so this beats following the FAT per cluster.
sealed class ClusterMap
{
    public struct Extent
    {
        public long Start;   // index of the first cluster inside the file
        public uint Disk;    // cluster number on the volume
        public long Length;
    }

    readonly List<Extent> extents = new();

    public long Count { get; private set; }
    public uint LastCluster { get; private set; }
    public IReadOnlyList<Extent> Extents => extents;

    public void AppendRun(uint disk, long length)
    {
        if (length <= 0)
            return;
        if (extents.Count > 0)
        {
            var last = extents[^1];
            if ((long)last.Disk + last.Length == disk)
            {
                last.Length += length;
                extents[^1] = last;
                Count += length;
                LastCluster = (uint)(disk + length - 1);
                return;
            }
        }
        extents.Add(new Extent { Start = Count, Disk = disk, Length = length });
        Count += length;
        LastCluster = (uint)(disk + length - 1);
    }

    public void Append(uint disk) => AppendRun(disk, 1);

    // runRemaining is how many clusters, counting this one, stay contiguous on disk
    public bool Find(long index, out uint disk, out long runRemaining)
    {
        int lo = 0, hi = extents.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var e = extents[mid];
            if (index < e.Start)
                hi = mid - 1;
            else if (index >= e.Start + e.Length)
                lo = mid + 1;
            else
            {
                disk = (uint)(e.Disk + (index - e.Start));
                runRemaining = e.Length - (index - e.Start);
                return true;
            }
        }
        disk = 0;
        runRemaining = 0;
        return false;
    }

    public uint ClusterAt(long index)
    {
        if (!Find(index, out uint disk, out _))
            throw new ArgumentOutOfRangeException(nameof(index));
        return disk;
    }

    // every (disk, length) run starting at a file cluster index
    public IEnumerable<(uint Disk, long Length)> RunsFrom(long index)
    {
        foreach (var e in extents)
        {
            if (e.Start + e.Length <= index)
                continue;
            long skip = Math.Max(0, index - e.Start);
            yield return ((uint)(e.Disk + skip), e.Length - skip);
        }
    }

    public void Truncate(long newCount)
    {
        while (extents.Count > 0 && extents[^1].Start >= newCount)
            extents.RemoveAt(extents.Count - 1);
        if (extents.Count > 0)
        {
            var e = extents[^1];
            if (e.Start + e.Length > newCount)
            {
                e.Length = newCount - e.Start;
                extents[^1] = e;
            }
        }
        Count = newCount;
        LastCluster = extents.Count == 0 ? 0 : (uint)(extents[^1].Disk + extents[^1].Length - 1);
    }
}
