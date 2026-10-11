namespace FreeXplorer.Storage;

// Lives in RAM, but only the blocks that were written actually use memory, so a
// "400 GB drive" costs nothing until you fill it. Mostly for tests and dry runs.
public sealed class MemoryBlockDevice : IBlockDevice
{
    const int BlockSize = 1 << 20;

    readonly Dictionary<long, byte[]> blocks = new();
    readonly object gate = new();

    public MemoryBlockDevice(long length, string name = "memory")
    {
        Length = length;
        Name = name;
    }

    public string Name { get; }
    public long Length { get; }
    public int SectorSize => 512;
    public bool CanWrite { get; set; } = true;

    public long AllocatedBytes
    {
        get { lock (gate) return blocks.Count * (long)BlockSize; }
    }

    public void Read(long offset, Span<byte> buffer)
    {
        BlockDeviceExtensions.CheckRange(this, offset, buffer.Length);
        lock (gate)
        {
            int done = 0;
            while (done < buffer.Length)
            {
                long pos = offset + done;
                long index = pos / BlockSize;
                int inBlock = (int)(pos % BlockSize);
                int n = Math.Min(buffer.Length - done, BlockSize - inBlock);
                if (blocks.TryGetValue(index, out var block))
                    block.AsSpan(inBlock, n).CopyTo(buffer[done..]);
                else
                    buffer.Slice(done, n).Clear();
                done += n;
            }
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite)
            throw new NotSupportedException("device is read-only");
        BlockDeviceExtensions.CheckRange(this, offset, buffer.Length);
        lock (gate)
        {
            int done = 0;
            while (done < buffer.Length)
            {
                long pos = offset + done;
                long index = pos / BlockSize;
                int inBlock = (int)(pos % BlockSize);
                int n = Math.Min(buffer.Length - done, BlockSize - inBlock);
                if (!blocks.TryGetValue(index, out var block))
                {
                    block = new byte[BlockSize];
                    blocks[index] = block;
                }
                buffer.Slice(done, n).CopyTo(block.AsSpan(inBlock));
                done += n;
            }
        }
    }

    public void Flush() { }
    public void Dispose() { }
}
