namespace FreeXplorer.Storage;

// A window into another device, so a partition can be handled with offsets starting at zero.
public sealed class SliceDevice : IBlockDevice
{
    readonly IBlockDevice inner;
    readonly long start;
    readonly bool writable;
    readonly bool ownsInner;

    public SliceDevice(IBlockDevice inner, long start, long length, bool writable, string? name = null, bool ownsInner = false)
    {
        if (start < 0 || length < 0 || start > inner.Length - length)
            throw new ArgumentOutOfRangeException(nameof(length), "slice does not fit inside the device");
        this.inner = inner;
        this.start = start;
        this.writable = writable && inner.CanWrite;
        this.ownsInner = ownsInner;
        Length = length;
        Name = name ?? $"{inner.Name}@0x{start:X}";
    }

    public string Name { get; }
    public long Length { get; }
    public int SectorSize => inner.SectorSize;
    public bool CanWrite => writable;
    public long StartOffset => start;

    public void Read(long offset, Span<byte> buffer)
    {
        BlockDeviceExtensions.CheckRange(this, offset, buffer.Length);
        inner.Read(start + offset, buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!writable)
            throw new NotSupportedException("slice is read-only");
        BlockDeviceExtensions.CheckRange(this, offset, buffer.Length);
        inner.Write(start + offset, buffer);
    }

    public void Flush() => inner.Flush();

    public void Dispose()
    {
        if (ownsInner)
            inner.Dispose();
    }
}
