using FreeXplorer.Fatx;
using FreeXplorer.Storage;

namespace FreeXplorer.Tests;

static class TestVolumes
{
    public static (MemoryBlockDevice Device, FatxVolume Volume) Create(
        long length = 64L << 20, int sectorsPerCluster = 0x20, FatxFlavor flavor = FatxFlavor.Xbox360)
    {
        var device = new MemoryBlockDevice(length);
        FatxFormatter.Format(device, 0, length, new FatxFormatOptions
        {
            Flavor = flavor,
            SectorsPerCluster = sectorsPerCluster,
            VolumeId = 0x1234ABCD,
        });
        var volume = FatxVolume.Open(device, writable: true);
        volume.ScanFreeSpace();
        return (device, volume);
    }

    public static FatxVolume Reopen(MemoryBlockDevice device, FatxVolume old)
    {
        old.Dispose();
        var volume = FatxVolume.Open(device, writable: true);
        volume.ScanFreeSpace();
        return volume;
    }

    public static byte[] RandomBytes(int count, int seed)
    {
        var data = new byte[count];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static void WriteAll(FatxVolume volume, FatxNode file, byte[] data)
    {
        volume.SetLength(file, 0);
        volume.Write(file, 0, data);
    }

    public static byte[] ReadAll(FatxVolume volume, FatxNode file)
    {
        var data = new byte[file.Size];
        int n = volume.Read(file, 0, data);
        Assert.Equal(data.Length, n);
        return data;
    }

    public static void AssertClean(FatxVolume volume)
    {
        var report = volume.Check();
        Assert.True(report.IsClean, string.Join(Environment.NewLine, report.Issues.Select(i => $"{i.Severity}: {i.Message}")));
    }
}

// counts what gets written, so a test can prove something stayed untouched
sealed class RecordingDevice : IBlockDevice
{
    readonly IBlockDevice inner;
    public RecordingDevice(IBlockDevice inner) => this.inner = inner;

    public long LowestWrite { get; private set; } = long.MaxValue;
    public long HighestWriteEnd { get; private set; }
    public long BytesWritten { get; private set; }

    public string Name => inner.Name;
    public long Length => inner.Length;
    public int SectorSize => inner.SectorSize;
    public bool CanWrite => inner.CanWrite;

    public void Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        LowestWrite = Math.Min(LowestWrite, offset);
        HighestWriteEnd = Math.Max(HighestWriteEnd, offset + buffer.Length);
        BytesWritten += buffer.Length;
        inner.Write(offset, buffer);
    }

    public void Flush() => inner.Flush();
    public void Dispose() { }
}
