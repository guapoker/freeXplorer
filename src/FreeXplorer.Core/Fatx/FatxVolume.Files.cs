namespace FreeXplorer.Fatx;

public sealed partial class FatxVolume
{
    const int MaxIoChunk = 8 << 20;

    public int Read(FatxNode file, long offset, Span<byte> destination)
    {
        lock (sync)
        {
            RequireFile(file);
            RequireLive(file);
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (offset >= file.Size || destination.Length == 0)
                return 0;

            long remaining = Math.Min(destination.Length, file.Size - offset);
            var map = EnsureMap(file);
            int cs = ClusterSize;
            int done = 0;
            while (remaining > 0)
            {
                long pos = offset + done;
                if (!map.Find(pos / cs, out uint disk, out long runClusters))
                    throw Fail(FatxError.Corrupt, $"{file.Path}: the cluster chain is shorter than the file size says");
                int within = (int)(pos % cs);
                int n = (int)Math.Min(Math.Min(remaining, runClusters * cs - within), MaxIoChunk);
                device.Read(Geometry.ClusterOffset(disk) + within, destination.Slice(done, n));
                done += n;
                remaining -= n;
            }
            return done;
        }
    }

    // Writing past the end grows the file. Anything between the old end and the offset
    // is zeroed, same as every other file system would hand it back.
    public void Write(FatxNode file, long offset, ReadOnlySpan<byte> data)
    {
        lock (sync)
        {
            RequireWritable();
            RequireFile(file);
            RequireLive(file);
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (data.Length == 0)
                return;

            long end = offset + data.Length;
            if (end > MaxFileSize)
                throw Fail(FatxError.FileTooLarge, "FATX files can't be bigger than 4 GB");

            var map = EnsureMap(file);
            int cs = ClusterSize;
            long oldSize = file.Size;
            long needed = (end + cs - 1) / cs;
            bool grew = false;

            if (needed > map.Count)
            {
                Extend(file, map, needed - map.Count);
                grew = true;
            }
            if (offset > oldSize)
                ZeroRange(map, oldSize, offset - oldSize);

            WriteRange(map, offset, data);

            if (end > oldSize)
            {
                file.Size = end;
                grew = true;
            }
            file.ModifiedRaw = file.AccessedRaw = NowRaw();

            if (grew)
            {
                fat.Flush();
                WriteEntry(file);
                dirty.Remove(file);
            }
            else
            {
                dirty.Add(file);
            }
        }
    }

    public void SetLength(FatxNode file, long length)
    {
        lock (sync)
        {
            RequireWritable();
            RequireFile(file);
            RequireLive(file);
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));
            if (length > MaxFileSize)
                throw Fail(FatxError.FileTooLarge, "FATX files can't be bigger than 4 GB");
            if (length == file.Size)
                return;

            var map = EnsureMap(file);
            int cs = ClusterSize;
            long old = file.Size;
            long needed = (length + cs - 1) / cs;

            if (length > old)
            {
                if (needed > map.Count)
                    Extend(file, map, needed - map.Count);
                ZeroRange(map, old, length - old);
                file.Size = length;
                file.ModifiedRaw = file.AccessedRaw = NowRaw();
                fat.Flush();
                WriteEntry(file);
            }
            else
            {
                file.Size = length;
                file.ModifiedRaw = file.AccessedRaw = NowRaw();
                if (needed < map.Count)
                {
                    var tail = map.RunsFrom(needed).ToList();
                    if (needed == 0)
                    {
                        file.FirstCluster = 0;
                    }
                    else
                    {
                        SetFat(map.ClusterAt(needed - 1), Geometry.EndOfChain);
                    }
                    // entry first: if we die before the FAT is flushed the chain is just longer than the size
                    WriteEntry(file);
                    foreach (var (disk, count) in tail)
                        for (long i = 0; i < count; i++)
                        {
                            uint c = (uint)(disk + i);
                            SetFat(c, 0);
                            if (c < hint)
                                hint = c;
                        }
                    map.Truncate(needed);
                    fat.Flush();
                }
                else
                {
                    WriteEntry(file);
                }
            }
            dirty.Remove(file);
        }
    }

    // ---- allocation ----

    ClusterMap EnsureMap(FatxNode node)
    {
        node.Map ??= BuildMap(node);
        return node.Map;
    }

    ClusterMap BuildMap(FatxNode node)
    {
        var map = new ClusterMap();
        uint c = node.FirstCluster;
        if (c == 0)
            return map;

        long guard = 0;
        while (true)
        {
            if (c < 1 || c > Geometry.ClusterCount)
                throw Fail(FatxError.Corrupt, $"{node.Path}: chain points at cluster {c}, outside the volume");
            if (++guard > Geometry.ClusterCount)
                throw Fail(FatxError.Corrupt, $"{node.Path}: chain loops back on itself");
            map.Append(c);

            uint next = fat.Get(c);
            if (next >= Geometry.EndThreshold)
                break;
            if (next == 0 || next == Geometry.BadCluster)
                throw Fail(FatxError.Corrupt, $"{node.Path}: chain runs into a free or bad cluster at {c}");
            c = next;
        }
        return map;
    }

    void SetFat(uint index, uint value)
    {
        if (freeKnown)
        {
            uint old = fat.Get(index);
            if (old == 0 && value != 0) freeClusters--;
            else if (old != 0 && value == 0) freeClusters++;
        }
        fat.Set(index, value);
    }

    // next-fit search, so repeated allocations tend to land one after the other
    List<(uint Start, uint Length)>? FindFree(long count, uint startAt)
    {
        uint limit = Geometry.ClusterCount;
        if (startAt < 1 || startAt > limit)
            startAt = 1;

        var runs = new List<(uint Start, uint Length)>();
        long need = count;
        uint i = startAt;
        for (long scanned = 0; scanned < limit && need > 0; scanned++)
        {
            if (fat.Get(i) == 0)
            {
                if (runs.Count > 0 && (long)runs[^1].Start + runs[^1].Length == i)
                    runs[^1] = (runs[^1].Start, runs[^1].Length + 1);
                else
                    runs.Add((i, 1));
                need--;
            }
            i = i == limit ? 1 : i + 1;
        }
        return need > 0 ? null : runs;
    }

    // Adds clusters to the end of a node's chain. All or nothing.
    void Extend(FatxNode node, ClusterMap map, long count)
    {
        if (count <= 0)
            return;
        if (freeKnown && count > freeClusters)
            throw Fail(FatxError.DiskFull, "there isn't enough free space on the volume");

        uint startAt = map.Count > 0 ? map.LastCluster + 1 : hint;
        var runs = FindFree(count, startAt) ?? throw Fail(FatxError.DiskFull, "there isn't enough free space on the volume");

        uint? tail = map.Count > 0 ? map.LastCluster : null;
        for (int r = 0; r < runs.Count; r++)
        {
            var (start, length) = runs[r];
            for (uint c = start; c < start + length - 1; c++)
                SetFat(c, c + 1);
            SetFat(start + length - 1, r + 1 < runs.Count ? runs[r + 1].Start : Geometry.EndOfChain);
        }
        if (tail is uint t)
            SetFat(t, runs[0].Start);
        else
            node.FirstCluster = runs[0].Start;

        foreach (var (start, length) in runs)
            map.AppendRun(start, length);

        var last = runs[^1];
        hint = last.Start + last.Length;
        if (hint > Geometry.ClusterCount)
            hint = 1;
    }

    // Frees a whole chain by following the FAT. Tolerates broken chains, it just stops.
    void FreeChain(uint first)
    {
        uint c = first;
        long guard = 0;
        while (c >= 1 && c <= Geometry.ClusterCount && guard++ <= Geometry.ClusterCount)
        {
            uint next = fat.Get(c);
            SetFat(c, 0);
            if (c < hint)
                hint = c;
            if (next >= Geometry.EndThreshold || next == 0)
                break;
            c = next;
        }
    }

    // ---- raw data movement inside an already allocated chain ----

    void WriteRange(ClusterMap map, long offset, ReadOnlySpan<byte> data)
    {
        int cs = ClusterSize;
        int done = 0;
        while (done < data.Length)
        {
            long pos = offset + done;
            if (!map.Find(pos / cs, out uint disk, out long runClusters))
                throw Fail(FatxError.Corrupt, "tried to write outside the allocated clusters");
            int within = (int)(pos % cs);
            int n = (int)Math.Min(Math.Min(data.Length - done, runClusters * cs - within), MaxIoChunk);
            device.Write(Geometry.ClusterOffset(disk) + within, data.Slice(done, n));
            done += n;
        }
    }

    void ZeroRange(ClusterMap map, long start, long length)
    {
        if (length <= 0)
            return;
        int cs = ClusterSize;
        var zeros = new byte[(int)Math.Min(length, 1 << 20)];
        long pos = start;
        long left = length;
        while (left > 0)
        {
            if (!map.Find(pos / cs, out uint disk, out long runClusters))
                throw Fail(FatxError.Corrupt, "tried to zero outside the allocated clusters");
            int within = (int)(pos % cs);
            int n = (int)Math.Min(Math.Min(left, runClusters * cs - within), zeros.Length);
            device.Write(Geometry.ClusterOffset(disk) + within, zeros.AsSpan(0, n));
            pos += n;
            left -= n;
        }
    }
}
