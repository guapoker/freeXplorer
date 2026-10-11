namespace FreeXplorer.Storage;

// Anything we can read and write by byte offset: an image file, a raw disk, a slice of either.
public interface IBlockDevice : IDisposable
{
    string Name { get; }
    long Length { get; }
    int SectorSize { get; }
    bool CanWrite { get; }

    // both calls handle the whole span or throw, there are no short reads
    void Read(long offset, Span<byte> buffer);
    void Write(long offset, ReadOnlySpan<byte> buffer);
    void Flush();
}

public static class BlockDeviceExtensions
{
    public static byte[] ReadBytes(this IBlockDevice device, long offset, int count)
    {
        var data = new byte[count];
        device.Read(offset, data);
        return data;
    }

    internal static void CheckRange(IBlockDevice device, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > device.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"range {offset}+{count} is outside {device.Name} ({device.Length} bytes)");
    }
}
