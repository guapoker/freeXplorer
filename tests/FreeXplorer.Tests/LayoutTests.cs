using System.Buffers.Binary;
using FreeXplorer.Fatx;
using FreeXplorer.Storage;
using FreeXplorer.Xbox360;

namespace FreeXplorer.Tests;

public class LayoutTests
{
    const long Gib = 1L << 30;

    [Fact]
    public void Partitions_sit_end_to_end()
    {
        var parts = HddLayout.Partitions(500 * Gib);
        Assert.Equal(6, parts.Count);
        for (int i = 1; i < parts.Count; i++)
            Assert.Equal(parts[i - 1].End, parts[i].Offset);
        Assert.Equal(0x80000, parts[0].Offset);
        Assert.Equal(500 * Gib, parts[^1].End);
    }

    [Fact]
    public void Offsets_match_the_published_table()
    {
        var parts = HddLayout.Partitions(500 * Gib).ToDictionary(p => p.Id);
        Assert.Equal(0x80000, parts["syscache"].Offset);
        Assert.Equal(0x80000000, parts["syscache"].Length);
        Assert.Equal(0x80080000, parts["gamecache"].Offset);
        Assert.Equal(0x10C080000, parts["sysext"].Offset);
        Assert.Equal(0xCE30000, parts["sysext"].Length);
        Assert.Equal(0x118EB0000, parts["sysext2"].Offset);
        Assert.Equal(0x8000000, parts["sysext2"].Length);
        Assert.Equal(0x120EB0000, parts["compat"].Offset);
        Assert.Equal(0x10000000, parts["compat"].Length);
        Assert.Equal(0x130EB0000, parts["content"].Offset);
        Assert.Equal(500 * Gib - 0x130EB0000, parts["content"].Length);
    }

    [Fact]
    public void Only_the_caches_are_not_fatx()
    {
        var parts = HddLayout.Partitions(100 * Gib);
        Assert.Equal(new[] { "syscache", "gamecache" }, parts.Where(p => !p.IsFatx).Select(p => p.Id));
    }

    [Theory]
    [InlineData("Content", "content")]
    [InlineData("data", "content")]
    [InlineData("Hdd1", "content")]
    [InlineData("Xbox 1", "compat")]
    [InlineData("backwards-compatibility", "compat")]
    [InlineData("HDDX", "compat")]
    [InlineData("SysExt", "sysext")]
    [InlineData("System Extended 2", "sysext2")]
    public void Partition_aliases(string input, string expected) =>
        Assert.Equal(expected, HddLayout.Normalize(input));

    [Fact]
    public void Visible_length_defaults_to_the_device()
    {
        var dev = new MemoryBlockDevice(20 * Gib);
        Assert.Equal(20 * Gib, HddLayout.VisibleLength(dev));
    }

    [Fact]
    public void Visible_length_follows_the_security_sector()
    {
        var dev = new MemoryBlockDevice(20 * Gib);
        var sector = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sector, (uint)(12 * Gib / 512));
        dev.Write(HddLayout.SecuritySectorOffset + 0x58, sector);
        Assert.Equal(12 * Gib, HddLayout.VisibleLength(dev));
    }

    [Fact]
    public void A_security_sector_claiming_too_much_is_ignored()
    {
        var dev = new MemoryBlockDevice(20 * Gib);
        var sector = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sector, (uint)(60 * Gib / 512));
        dev.Write(HddLayout.SecuritySectorOffset + 0x58, sector);
        Assert.Equal(20 * Gib, HddLayout.VisibleLength(dev));
    }

    [Fact]
    public void The_32_bit_sector_count_caps_big_drives_at_2_TiB()
    {
        var dev = new MemoryBlockDevice(4L << 40);
        Assert.Equal(0xFFFFFFFFL * 512, HddLayout.VisibleLength(dev));
    }

    [Fact]
    public void Format_everything_then_scan()
    {
        var dev = new MemoryBlockDevice(8 * Gib);
        HddFormatter.Format(dev, new[] { "sysext", "sysext2", "compat", "content" });

        var scan = DriveScanner.Scan(dev);
        Assert.Equal(DriveKind.Xbox360Drive, scan.Kind);
        var mountable = scan.Partitions.Where(p => p.CanMount).Select(p => p.Id).ToList();
        Assert.Equal(new[] { "sysext", "sysext2", "compat", "content" }, mountable);

        var content = scan.Partitions.Single(p => p.Id == "content");
        Assert.Equal(8 * Gib - HddLayout.ContentOffset, content.Length);
        Assert.All(scan.Partitions.Where(p => p.CanMount), p => Assert.Equal(FatxFlavor.Xbox360, p.Flavor));
    }

    [Fact]
    public void Partitions_dont_trample_each_other()
    {
        var dev = new MemoryBlockDevice(8 * Gib);
        HddFormatter.Format(dev, new[] { "sysext", "sysext2", "compat", "content" });
        var scan = DriveScanner.Scan(dev);

        // fill each one with a recognisable file
        foreach (var p in scan.Partitions.Where(p => p.CanMount))
        {
            using var vol = DriveScanner.OpenPartition(dev, p, writable: true);
            var file = vol.CreateFile(vol.Root, "marker.bin");
            vol.Write(file, 0, Enumerable.Repeat((byte)p.Id[^1], 3_000_000).ToArray());
        }

        foreach (var p in scan.Partitions.Where(p => p.CanMount))
        {
            using var vol = DriveScanner.OpenPartition(dev, p, writable: false);
            var data = TestVolumes.ReadAll(vol, vol.Resolve("marker.bin"));
            Assert.All(data, b => Assert.Equal((byte)p.Id[^1], b));
            TestVolumes.AssertClean(vol);
        }
    }

    [Fact]
    public void Formatting_leaves_the_drive_header_alone()
    {
        var inner = new MemoryBlockDevice(8 * Gib);
        var dev = new RecordingDevice(inner);
        HddFormatter.Format(dev, new[] { "sysext", "sysext2", "compat", "content" });
        Assert.True(dev.LowestWrite >= HddLayout.SystemExtOffset, $"wrote at 0x{dev.LowestWrite:X}");
    }

    [Fact]
    public void Formatting_one_partition_leaves_the_others_alone()
    {
        var dev = new MemoryBlockDevice(8 * Gib);
        HddFormatter.Format(dev, new[] { "sysext", "sysext2", "compat", "content" });
        using (var vol = DriveScanner.OpenPartition(dev, DriveScanner.Scan(dev).Partitions.Single(p => p.Id == "content"), true))
            vol.Write(vol.CreateFile(vol.Root, "keep.me"), 0, new byte[1000]);

        HddFormatter.Format(dev, new[] { "compat" });

        var content = DriveScanner.Scan(dev).Partitions.Single(p => p.Id == "content");
        using var check = DriveScanner.OpenPartition(dev, content, false);
        Assert.NotNull(check.Find("keep.me"));
    }

    [Fact]
    public void The_cluster_size_of_each_partition_is_16K()
    {
        var dev = new MemoryBlockDevice(8 * Gib);
        var plan = HddFormatter.Plan(dev, new[] { "sysext", "sysext2", "compat", "content" });
        Assert.All(plan, s => Assert.Equal(16384, s.Geometry.ClusterSize));
    }

    [Fact]
    public void Small_partitions_use_16_bit_fat_and_content_uses_32()
    {
        var dev = new MemoryBlockDevice(8 * Gib);
        var plan = HddFormatter.Plan(dev, new[] { "sysext", "sysext2", "compat", "content" }).ToDictionary(s => s.Partition.Id);
        Assert.Equal(2, plan["sysext"].Geometry.FatEntrySize);
        Assert.Equal(2, plan["sysext2"].Geometry.FatEntrySize);
        Assert.Equal(2, plan["compat"].Geometry.FatEntrySize);
        Assert.Equal(4, plan["content"].Geometry.FatEntrySize);
    }

    [Fact]
    public void A_drive_thats_too_small_is_rejected()
    {
        var dev = new MemoryBlockDevice(3 * Gib);
        Assert.Throws<ArgumentException>(() => HddFormatter.Plan(dev, new[] { "content" }));
        Assert.Throws<ArgumentException>(() => HddFormatter.Plan(dev, new[] { "compat" }));
    }

    [Fact]
    public void Cache_partitions_cant_be_formatted()
    {
        var dev = new MemoryBlockDevice(8 * Gib);
        Assert.Throws<ArgumentException>(() => HddFormatter.Plan(dev, new[] { "syscache" }));
        Assert.Throws<ArgumentException>(() => HddFormatter.Plan(dev, new[] { "nonsense" }));
    }

    [Fact]
    public void Content_can_be_made_smaller_than_the_drive()
    {
        var dev = new MemoryBlockDevice(8 * Gib);
        var plan = HddFormatter.Plan(dev, new[] { "content" }, new HddFormatOptions { ContentLength = 2 * Gib });
        Assert.Equal(2 * Gib, plan[0].Partition.Length);
    }

    [Fact]
    public void Content_label_is_written()
    {
        var dev = new MemoryBlockDevice(8 * Gib);
        HddFormatter.Format(dev, new[] { "content" }, new HddFormatOptions { ContentLabel = "Games" });
        var content = DriveScanner.Scan(dev).Partitions.Single(p => p.Id == "content");
        using var vol = DriveScanner.OpenPartition(dev, content, false);
        Assert.Equal("Games", vol.Label);
    }

    [Fact]
    public void A_partition_dump_is_recognised()
    {
        var dev = new MemoryBlockDevice(64L << 20);
        FatxFormatter.Format(dev, 0, dev.Length);
        var scan = DriveScanner.Scan(dev);
        Assert.Equal(DriveKind.SinglePartition, scan.Kind);
        Assert.Single(scan.Partitions);
        Assert.True(scan.Partitions[0].CanMount);
        using var vol = DriveScanner.OpenPartition(dev, scan.Partitions[0], false);
        Assert.Empty(vol.GetChildren(vol.Root));
    }

    [Fact]
    public void An_empty_disk_is_unknown_but_still_offers_the_layout()
    {
        var dev = new MemoryBlockDevice(10 * Gib);
        var scan = DriveScanner.Scan(dev);
        Assert.Equal(DriveKind.Unknown, scan.Kind);
        Assert.Contains(scan.Partitions, p => p.Id == "content" && !p.HasSignature);
    }

    // Content's FAT size depends on how long the partition is, and the console works that out
    // from the security sector. If our idea of the length is wrong the volume looks broken.
    [Fact]
    public void Content_opens_with_the_length_the_console_would_use()
    {
        var dev = new MemoryBlockDevice(10 * Gib);
        var claimed = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(claimed, (uint)(8 * Gib / 512));
        dev.Write(HddLayout.SecuritySectorOffset + 0x58, claimed);

        HddFormatter.Format(dev, new[] { "content" });
        var scan = DriveScanner.Scan(dev);
        var content = scan.Partitions.Single(p => p.Id == "content");
        Assert.Equal(8 * Gib - HddLayout.ContentOffset, content.Length);
        using var vol = DriveScanner.OpenPartition(dev, content, false);
        Assert.Equal(FatxGeometry.Compute(content.Length, 0x20).DataOffset, vol.Geometry.DataOffset);
    }

    [Fact]
    public void Content_falls_back_to_the_device_length()
    {
        // formatted on a 10 GiB drive, but then a security sector shows up claiming 8 GiB
        var dev = new MemoryBlockDevice(10 * Gib);
        HddFormatter.Format(dev, new[] { "content" });
        var claimed = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(claimed, (uint)(8 * Gib / 512));
        dev.Write(HddLayout.SecuritySectorOffset + 0x58, claimed);

        var content = DriveScanner.Scan(dev).Partitions.Single(p => p.Id == "content");
        using var vol = DriveScanner.OpenPartition(dev, content, false);
        Assert.Equal(FatxGeometry.Compute(10 * Gib - HddLayout.ContentOffset, 0x20).DataOffset, vol.Geometry.DataOffset);
    }
}
