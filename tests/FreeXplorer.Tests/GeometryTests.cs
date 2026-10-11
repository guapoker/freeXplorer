using FreeXplorer.Fatx;

namespace FreeXplorer.Tests;

public class GeometryTests
{
    // numbers taken from the example output of a public XTAF reader run on a real drive:
    // 16K clusters, 4 byte FAT entries, FAT of 59801600 bytes
    [Fact]
    public void Matches_a_real_data_partition()
    {
        var g = FatxGeometry.Compute(244943675392, 0x20);
        Assert.Equal(16384, g.ClusterSize);
        Assert.Equal(4, g.FatEntrySize);
        Assert.Equal(59801600, g.FatSize);
        Assert.Equal(0x1000 + 59801600, g.DataOffset);
    }

    // same source: a 128 MiB sysext partition has its first cluster at 0x6000
    [Fact]
    public void Matches_a_real_sysext_partition()
    {
        var g = FatxGeometry.Compute(0x8000000, 0x20);
        Assert.Equal(2, g.FatEntrySize);
        Assert.Equal(0x5000, g.FatSize);
        Assert.Equal(0x6000, g.DataOffset);
        Assert.Equal(0x6000, g.ClusterOffset(1));
        Assert.Equal(0xA000, g.ClusterOffset(2));
    }

    [Fact]
    public void Switches_to_32_bit_entries_at_0xFFF0_clusters()
    {
        // max clusters = sectors / spc + 1
        long justBelow = (0xFFEFL - 1) * 0x20 * 512;
        long atLimit = (0xFFF0L - 1) * 0x20 * 512;
        Assert.Equal(2, FatxGeometry.Compute(justBelow, 0x20).FatEntrySize);
        Assert.Equal(4, FatxGeometry.Compute(atLimit, 0x20).FatEntrySize);
    }

    [Fact]
    public void Fat_size_is_a_multiple_of_4096()
    {
        foreach (long mb in new long[] { 17, 33, 100, 1000, 5000 })
        {
            var g = FatxGeometry.Compute(mb * 1024 * 1024 + 512, 0x20);
            Assert.Equal(0, g.FatSize % 4096);
            Assert.Equal(0, g.DataOffset % 4096);
        }
    }

    [Fact]
    public void Last_cluster_fits_inside_the_partition()
    {
        foreach (long length in new long[] { 20_000_000, 134_217_728, 5_000_000_000 })
        {
            var g = FatxGeometry.Compute(length, 0x20);
            long end = g.ClusterOffset(g.ClusterCount) + g.ClusterSize;
            Assert.True(end <= g.PartitionLength, $"{length}: last cluster ends at {end}");
        }
    }

    [Fact]
    public void Picks_16K_clusters_until_the_console_limit()
    {
        Assert.Equal(0x20, FatxGeometry.PickSectorsPerCluster(100L << 20));
        Assert.Equal(0x20, FatxGeometry.PickSectorsPerCluster(2L << 40));
        Assert.Equal(0x40, FatxGeometry.PickSectorsPerCluster(6L << 40));
        Assert.Equal(0x80, FatxGeometry.PickSectorsPerCluster(12L << 40));
    }

    [Fact]
    public void Rejects_nonsense_cluster_sizes()
    {
        Assert.Throws<FatxException>(() => FatxGeometry.Compute(1 << 30, 0));
        Assert.Throws<FatxException>(() => FatxGeometry.Compute(1 << 30, 3));
        Assert.Throws<FatxException>(() => FatxGeometry.Compute(1 << 30, 256));
    }

    [Fact]
    public void Tiny_partitions_are_refused()
    {
        Assert.Throws<FatxException>(() => FatxGeometry.Compute(0x2000, 0x20));
    }
}

public class TimeTests
{
    [Fact]
    public void Roundtrips_on_the_360_epoch()
    {
        var t = new DateTime(2011, 6, 15, 18, 42, 30);
        uint raw = FatxTime.Encode(t, FatxFlavor.Xbox360);
        Assert.Equal(t, FatxTime.Decode(raw, FatxFlavor.Xbox360));
    }

    [Fact]
    public void Counts_years_from_1980_on_the_360()
    {
        uint raw = FatxTime.Encode(new DateTime(1980, 1, 1), FatxFlavor.Xbox360);
        Assert.Equal(0u, raw >> 25);
        Assert.Equal(25u, FatxTime.Encode(new DateTime(2005, 1, 1), FatxFlavor.Xbox360) >> 25);
    }

    [Fact]
    public void Only_keeps_two_second_precision()
    {
        var t = new DateTime(2020, 1, 1, 10, 0, 7);
        Assert.Equal(new DateTime(2020, 1, 1, 10, 0, 6), FatxTime.Decode(FatxTime.Encode(t, FatxFlavor.Xbox360), FatxFlavor.Xbox360));
    }

    [Fact]
    public void Garbage_values_dont_throw()
    {
        foreach (uint raw in new uint[] { 0, 0xFFFFFFFF, 0x12345678, 0xDEADBEEF })
            FatxTime.Decode(raw, FatxFlavor.Xbox360);
    }

    [Fact]
    public void Years_outside_the_range_are_clamped()
    {
        var early = FatxTime.Decode(FatxTime.Encode(new DateTime(1970, 5, 5), FatxFlavor.Xbox360), FatxFlavor.Xbox360);
        Assert.Equal(1980, early.Year);
    }
}

public class NameTests
{
    [Theory]
    [InlineData("Content")]
    [InlineData("0000000000000000")]
    [InlineData("Halo 3 (disc 1)")]
    [InlineData("name.txt")]
    [InlineData("a_b-c$d")]
    public void Accepts(string name) => Assert.True(FatxName.IsValid(name));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("what?")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("colon:name")]
    [InlineData("star*")]
    [InlineData("quote\"")]
    [InlineData("pipe|")]
    [InlineData("café")]
    public void Rejects(string name) => Assert.False(FatxName.IsValid(name));

    [Fact]
    public void Limit_is_42_characters()
    {
        Assert.True(FatxName.IsValid(new string('a', 42)));
        Assert.False(FatxName.IsValid(new string('a', 43)));
    }
}
