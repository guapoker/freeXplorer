using FreeXplorer.Storage;

namespace FreeXplorer.Tests;

// refuses anything that isn't whole sectors, like a raw disk handle does
sealed class StrictSectorIo : IAlignedIo
{
    readonly MemoryBlockDevice data;
    readonly int sector;

    public StrictSectorIo(long length, int sector)
    {
        data = new MemoryBlockDevice(length);
        this.sector = sector;
    }

    public MemoryBlockDevice Backing => data;
    public string Name => "strict";
    public long Length => data.Length;
    public int SectorSize => sector;
    public bool CanWrite => true;

    public void ReadAligned(long offset, Span<byte> buffer)
    {
        Check(offset, buffer.Length);
        data.Read(offset, buffer);
    }

    public void WriteAligned(long offset, ReadOnlySpan<byte> buffer)
    {
        Check(offset, buffer.Length);
        data.Write(offset, buffer);
    }

    void Check(long offset, int length)
    {
        if (offset % sector != 0 || length % sector != 0 || length == 0)
            throw new IOException($"unaligned io: offset {offset}, length {length}");
    }

    public void Flush() { }
    public void Dispose() { }
}

public class DeviceTests
{
    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void Aligned_wrapper_behaves_like_plain_memory(int sector)
    {
        const long length = 3 << 20;
        var io = new StrictSectorIo(length, sector);
        var wrapped = new SectorAlignedDevice(io);
        var reference = new MemoryBlockDevice(length);
        var rng = new Random(sector);

        for (int i = 0; i < 600; i++)
        {
            int count = rng.Next(1, 40_000);
            long offset = rng.Next(0, (int)(length - count));
            var data = new byte[count];
            rng.NextBytes(data);
            wrapped.Write(offset, data);
            reference.Write(offset, data);

            int readCount = rng.Next(1, 30_000);
            long readAt = rng.Next(0, (int)(length - readCount));
            var a = new byte[readCount];
            var b = new byte[readCount];
            wrapped.Read(readAt, a);
            reference.Read(readAt, b);
            Assert.True(a.AsSpan().SequenceEqual(b), $"mismatch after {i} rounds");
        }

        var whole1 = new byte[length];
        var whole2 = new byte[length];
        io.Backing.Read(0, whole1);
        reference.Read(0, whole2);
        Assert.True(whole1.AsSpan().SequenceEqual(whole2));
    }

    [Fact]
    public void Tiny_reads_and_writes_inside_one_sector()
    {
        var io = new StrictSectorIo(1 << 20, 512);
        var dev = new SectorAlignedDevice(io);
        dev.Write(700, new byte[] { 1, 2, 3 });
        dev.Write(5, new byte[] { 9 });

        var back = new byte[3];
        dev.Read(700, back);
        Assert.Equal(new byte[] { 1, 2, 3 }, back);
        Assert.Equal(9, dev.ReadBytes(5, 1)[0]);
        Assert.Equal(0, dev.ReadBytes(6, 1)[0]);
    }

    [Fact]
    public void A_write_to_the_last_bytes_of_the_disk_works()
    {
        var dev = new SectorAlignedDevice(new StrictSectorIo(1 << 20, 4096));
        dev.Write((1 << 20) - 3, new byte[] { 7, 8, 9 });
        Assert.Equal(new byte[] { 7, 8, 9 }, dev.ReadBytes((1 << 20) - 3, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => dev.Write((1 << 20) - 2, new byte[3]));
    }

    [Fact]
    public void A_volume_works_on_top_of_the_aligned_wrapper()
    {
        var dev = new SectorAlignedDevice(new StrictSectorIo(64L << 20, 4096));
        Fatx.FatxFormatter.Format(dev, 0, dev.Length);
        using var vol = Fatx.FatxVolume.Open(dev, writable: true);
        var dir = vol.CreateDirectory(vol.Root, "Content");
        for (int i = 0; i < 40; i++)
        {
            var file = vol.CreateFile(dir, $"f{i}.bin");
            vol.Write(file, 0, TestVolumes.RandomBytes(10_000 + i * 977, i));
        }
        vol.Flush();
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void File_devices_read_what_was_written()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fx-test-{Guid.NewGuid():N}.img");
        try
        {
            using (var dev = FileBlockDevice.CreateImage(path, 8 << 20))
            {
                Assert.Equal(8 << 20, dev.Length);
                dev.Write(4_000_000, new byte[] { 5, 6, 7 });
                dev.Flush();
            }
            using var again = new FileBlockDevice(path, writable: false);
            Assert.Equal(new byte[] { 5, 6, 7 }, again.ReadBytes(4_000_000, 3));
            Assert.Throws<NotSupportedException>(() => again.Write(0, new byte[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => again.ReadBytes(8 << 20, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Slices_translate_offsets_and_stay_in_bounds()
    {
        var mem = new MemoryBlockDevice(1000);
        mem.Write(500, new byte[] { 42 });
        var slice = new SliceDevice(mem, 400, 200, writable: true);
        Assert.Equal(42, slice.ReadBytes(100, 1)[0]);
        slice.Write(0, new byte[] { 1 });
        Assert.Equal(1, mem.ReadBytes(400, 1)[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => slice.ReadBytes(199, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SliceDevice(mem, 900, 200, true));
    }

    [Fact]
    public void Memory_device_only_uses_memory_for_what_was_written()
    {
        var dev = new MemoryBlockDevice(500L << 30);
        dev.Write(300L << 30, new byte[] { 1 });
        Assert.True(dev.AllocatedBytes <= 2 << 20);
        Assert.Equal(0, dev.ReadBytes(100L << 30, 1)[0]);
        Assert.Equal(1, dev.ReadBytes(300L << 30, 1)[0]);
    }
}

public class DiskSafetyTests
{
    static PhysicalDiskInfo Disk(int n = 3, bool volumes = false, bool system = false) =>
        new(n, 500L << 30, 512, "Test Disk", "123", "USB", true, volumes, system);

    [Fact]
    public void The_system_disk_is_never_writable()
    {
        Assert.False(DiskSafety.CanWrite(Disk(0, system: true)).Allowed);
        Assert.False(DiskSafety.CanWrite(Disk(0, system: true), allowPcDisk: true).Allowed);
    }

    [Fact]
    public void Disks_with_drive_letters_need_an_explicit_override()
    {
        var disk = Disk(volumes: true);
        Assert.False(DiskSafety.CanWrite(disk).Allowed);
        Assert.True(DiskSafety.CanWrite(disk, allowPcDisk: true).Allowed);
    }

    [Fact]
    public void A_plain_disk_is_allowed()
    {
        Assert.True(DiskSafety.CanWrite(Disk()).Allowed);
    }

    [Fact]
    public void Confirmation_has_to_name_the_disk()
    {
        var disk = Disk(5);
        Assert.True(DiskSafety.IsConfirmed(disk, "disk 5"));
        Assert.True(DiskSafety.IsConfirmed(disk, "  Disk 5 "));
        Assert.False(DiskSafety.IsConfirmed(disk, "disk 4"));
        Assert.False(DiskSafety.IsConfirmed(disk, "yes"));
        Assert.False(DiskSafety.IsConfirmed(disk, null));
    }

    [Fact]
    public void Sizes_read_nicely()
    {
        Assert.Equal("0 B", Size.Format(0));
        Assert.Equal("1.5 KB", Size.Format(1536));
        Assert.Equal("465.8 GB", Size.Format(500_107_862_016));
    }

    // only looks at the disks, never reads or writes them
    [Fact]
    public void Listing_disks_works_without_elevation()
    {
        var disks = PhysicalDisks.List();
        Assert.All(disks, d => Assert.True(d.Length > 0 && d.SectorSize > 0));
        if (disks.Count > 0)
            Assert.Contains(disks, d => d.IsSystem);
    }
}
