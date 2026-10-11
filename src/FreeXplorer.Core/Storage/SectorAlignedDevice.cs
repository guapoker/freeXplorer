namespace FreeXplorer.Storage;

// What a raw disk handle gives you: offsets and lengths must be whole sectors
interface IAlignedIo : IDisposable
{
    string Name { get; }
    long Length { get; }
    int SectorSize { get; }
    bool CanWrite { get; }
    void ReadAligned(long offset, Span<byte> buffer);
    void WriteAligned(long offset, ReadOnlySpan<byte> buffer);
    void Flush();
}

// Lets the rest of the code read and write any byte range on something that only does
// whole sectors. Partial sectors at either end are done with read-modify-write.
sealed class SectorAlignedDevice : IBlockDevice
{
    const int MaxChunk = 1 << 20;

    readonly IAlignedIo io;
    readonly object writeGate = new();

    public SectorAlignedDevice(IAlignedIo io) => this.io = io;

    public string Name => io.Name;
    public long Length => io.Length;
    public int SectorSize => io.SectorSize;
    public bool CanWrite => io.CanWrite;

    public void Read(long offset, Span<byte> buffer)
    {
        BlockDeviceExtensions.CheckRange(this, offset, buffer.Length);
        int ss = io.SectorSize;
        int done = 0;

        // ragged start
        int skip = (int)(offset % ss);
        if (skip != 0 || buffer.Length < ss)
        {
            long start = offset - skip;
            int span = (int)Math.Min(ss, Length - start);
            var sector = new byte[ss];
            io.ReadAligned(start, sector.AsSpan(0, span));
            int n = Math.Min(buffer.Length, ss - skip);
            sector.AsSpan(skip, n).CopyTo(buffer);
            done = n;
        }

        // whole sectors straight into the caller's buffer
        long pos = offset + done;
        int whole = (buffer.Length - done) / ss * ss;
        int end = done + whole;
        while (done < end)
        {
            int n = Math.Min(end - done, MaxChunk / ss * ss);
            io.ReadAligned(pos, buffer.Slice(done, n));
            done += n;
            pos += n;
        }

        // ragged end
        int rest = buffer.Length - done;
        if (rest > 0)
        {
            var sector = new byte[ss];
            io.ReadAligned(pos, sector);
            sector.AsSpan(0, rest).CopyTo(buffer[done..]);
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (!CanWrite)
            throw new NotSupportedException("disk was opened read-only");
        BlockDeviceExtensions.CheckRange(this, offset, data.Length);
        int ss = io.SectorSize;

        lock (writeGate)
        {
            int done = 0;
            long pos = offset;

            int skip = (int)(pos % ss);
            if (skip != 0 || data.Length < ss)
            {
                long start = pos - skip;
                var sector = new byte[ss];
                io.ReadAligned(start, sector);
                int n = Math.Min(data.Length, ss - skip);
                data[..n].CopyTo(sector.AsSpan(skip));
                io.WriteAligned(start, sector);
                done = n;
                pos += n;
            }

            int whole = (data.Length - done) / ss * ss;
            int end = done + whole;
            while (done < end)
            {
                int n = Math.Min(end - done, MaxChunk / ss * ss);
                io.WriteAligned(pos, data.Slice(done, n));
                done += n;
                pos += n;
            }

            int rest = data.Length - done;
            if (rest > 0)
            {
                var sector = new byte[ss];
                io.ReadAligned(pos, sector);
                data[done..].CopyTo(sector);
                io.WriteAligned(pos, sector);
            }
        }
    }

    public void Flush() => io.Flush();
    public void Dispose() => io.Dispose();
}
