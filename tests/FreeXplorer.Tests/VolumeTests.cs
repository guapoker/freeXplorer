using FreeXplorer.Fatx;
using FreeXplorer.Storage;

namespace FreeXplorer.Tests;

public class FormatTests
{
    [Fact]
    public void Writes_the_header_the_console_expects()
    {
        var dev = new MemoryBlockDevice(64L << 20);
        FatxFormatter.Format(dev, 0, dev.Length, new FatxFormatOptions { VolumeId = 0x11223344 });

        var head = dev.ReadBytes(0, 16);
        Assert.Equal("XTAF", System.Text.Encoding.ASCII.GetString(head, 0, 4));
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, head[4..8]);
        Assert.Equal(new byte[] { 0, 0, 0, 0x20 }, head[8..12]);   // sectors per cluster, big endian
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, head[12..16]);     // root directory cluster

        // the rest of the page looks like a console-formatted drive: two zero bytes, then 0xFF
        var page = dev.ReadBytes(0, 0x1000);
        Assert.Equal(new byte[] { 0, 0 }, page[0x10..0x12]);
        Assert.All(page[0x12..], b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void Fat32_starts_with_media_entry_and_a_closed_root()
    {
        var dev = new MemoryBlockDevice(2L << 30);
        FatxFormatter.Format(dev, 0, dev.Length);
        var fat = dev.ReadBytes(0x1000, 12);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xF8 }, fat[0..4]);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, fat[4..8]);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, fat[8..12]);
    }

    [Fact]
    public void Fat16_uses_16_bit_entries()
    {
        var dev = new MemoryBlockDevice(32L << 20);
        FatxFormatter.Format(dev, 0, dev.Length);
        var fat = dev.ReadBytes(0x1000, 6);
        Assert.Equal(new byte[] { 0xFF, 0xF8, 0xFF, 0xFF, 0, 0 }, fat);
    }

    [Fact]
    public void Root_directory_cluster_is_empty()
    {
        var dev = new MemoryBlockDevice(64L << 20);
        var geo = FatxFormatter.Format(dev, 0, dev.Length);
        var root = dev.ReadBytes(geo.ClusterOffset(1), geo.ClusterSize);
        Assert.All(root, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void Stays_inside_the_requested_range()
    {
        var inner = new MemoryBlockDevice(256L << 20);
        var dev = new RecordingDevice(inner);
        FatxFormatter.Format(dev, 64L << 20, 128L << 20);
        Assert.True(dev.LowestWrite >= 64L << 20);
        Assert.True(dev.HighestWriteEnd <= 192L << 20);
    }

    [Fact]
    public void Can_format_over_an_old_volume()
    {
        var (dev, vol) = TestVolumes.Create();
        var f = vol.CreateFile(vol.Root, "old.bin");
        vol.Write(f, 0, TestVolumes.RandomBytes(100000, 1));
        vol.Dispose();

        FatxFormatter.Format(dev, 0, dev.Length);
        using var fresh = FatxVolume.Open(dev);
        Assert.Empty(fresh.GetChildren(fresh.Root));
    }

    [Fact]
    public void Label_is_stored_in_name_txt()
    {
        var dev = new MemoryBlockDevice(64L << 20);
        FatxFormatter.Format(dev, 0, dev.Length, new FatxFormatOptions { Label = "My Drive" });
        using var vol = FatxVolume.Open(dev);
        Assert.Equal("My Drive", vol.Label);
        var node = vol.Find("name.txt");
        Assert.NotNull(node);
        var raw = TestVolumes.ReadAll(vol, node!);
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0, (byte)'M' }, raw[..4]);
    }

    [Fact]
    public void Refuses_a_read_only_device()
    {
        var dev = new MemoryBlockDevice(64L << 20) { CanWrite = false };
        Assert.Throws<FatxException>(() => FatxFormatter.Format(dev, 0, dev.Length));
    }

    [Fact]
    public void Little_endian_flavor_writes_FATX()
    {
        var dev = new MemoryBlockDevice(64L << 20);
        FatxFormatter.Format(dev, 0, dev.Length, new FatxFormatOptions { Flavor = FatxFlavor.Xbox, VolumeId = 0x01020304 });
        var head = dev.ReadBytes(0, 16);
        Assert.Equal("FATX", System.Text.Encoding.ASCII.GetString(head, 0, 4));
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, head[4..8]);
        using var vol = FatxVolume.Open(dev);
        Assert.Equal(FatxFlavor.Xbox, vol.Flavor);
    }

    [Fact]
    public void Open_rejects_things_that_arent_fatx()
    {
        var dev = new MemoryBlockDevice(64L << 20);
        var ex = Assert.Throws<FatxException>(() => FatxVolume.Open(dev));
        Assert.Equal(FatxError.NotFatx, ex.Error);
    }
}

public class VolumeTests
{
    [Fact]
    public void Fresh_volume_is_empty_and_clean()
    {
        var (_, vol) = TestVolumes.Create();
        Assert.Empty(vol.GetChildren(vol.Root));
        Assert.Equal(vol.TotalBytes - vol.ClusterSize, vol.FreeBytes);
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void New_files_have_no_attribute_bits_like_the_consoles_own()
    {
        var (_, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "plain.bin");
        var dir = vol.CreateDirectory(vol.Root, "folder");
        Assert.Equal(FatxAttributes.None, file.Attributes);
        Assert.Equal(FatxAttributes.Directory, dir.Attributes);
    }

    [Fact]
    public void Write_then_read_back()
    {
        var (_, vol) = TestVolumes.Create();
        var dir = vol.CreateDirectories("Content\\0000000000000000");
        var file = vol.CreateFile(dir, "data.bin");
        var data = TestVolumes.RandomBytes(100_000, 7);
        vol.Write(file, 0, data);

        Assert.Equal(100_000, file.Size);
        Assert.Equal(data, TestVolumes.ReadAll(vol, file));
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Survives_a_reopen()
    {
        var (dev, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.CreateDirectory(vol.Root, "Games"), "a.bin");
        var data = TestVolumes.RandomBytes(333_333, 9);
        vol.Write(file, 0, data);

        vol = TestVolumes.Reopen(dev, vol);
        var again = vol.Resolve("Games\\a.bin");
        Assert.Equal(data, TestVolumes.ReadAll(vol, again));
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Partial_reads_at_odd_offsets()
    {
        var (_, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "x");
        var data = TestVolumes.RandomBytes(70_000, 3);
        vol.Write(file, 0, data);

        var buf = new byte[5000];
        int n = vol.Read(file, 16_380, buf);
        Assert.Equal(5000, n);
        Assert.Equal(data[16_380..21_380], buf);

        n = vol.Read(file, 69_000, buf);
        Assert.Equal(1000, n);
        Assert.Equal(0, vol.Read(file, 70_000, buf));
        Assert.Equal(0, vol.Read(file, 99_999, buf));
    }

    [Fact]
    public void Overwrite_in_the_middle_keeps_the_rest()
    {
        var (_, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "x");
        var data = TestVolumes.RandomBytes(60_000, 4);
        vol.Write(file, 0, data);

        var patch = TestVolumes.RandomBytes(9_000, 5);
        vol.Write(file, 20_000, patch);
        patch.CopyTo(data, 20_000);

        Assert.Equal(60_000, file.Size);
        Assert.Equal(data, TestVolumes.ReadAll(vol, file));
    }

    [Fact]
    public void Append_in_small_pieces()
    {
        var (_, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "log.txt");
        var all = new List<byte>();
        for (int i = 0; i < 300; i++)
        {
            var piece = TestVolumes.RandomBytes(1 + i * 7 % 900, i);
            vol.Write(file, file.Size, piece);
            all.AddRange(piece);
        }
        Assert.Equal(all.ToArray(), TestVolumes.ReadAll(vol, file));
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Writing_past_the_end_zero_fills_the_gap()
    {
        var (_, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "sparse");
        vol.Write(file, 50_000, new byte[] { 1, 2, 3 });

        var all = TestVolumes.ReadAll(vol, file);
        Assert.Equal(50_003, all.Length);
        Assert.All(all[..50_000], b => Assert.Equal(0, b));
        Assert.Equal(new byte[] { 1, 2, 3 }, all[50_000..]);
    }

    [Fact]
    public void Growing_a_file_never_exposes_old_data()
    {
        var (_, vol) = TestVolumes.Create(8L << 20);
        var junk = vol.CreateFile(vol.Root, "junk");
        vol.Write(junk, 0, Enumerable.Repeat((byte)0xAB, 1_000_000).ToArray());
        vol.Delete(junk);

        var fresh = vol.CreateFile(vol.Root, "fresh");
        vol.SetLength(fresh, 600_000);
        Assert.All(TestVolumes.ReadAll(vol, fresh), b => Assert.Equal(0, b));

        // and the same through a write that skips ahead
        var other = vol.CreateFile(vol.Root, "other");
        vol.Write(other, 300_000, new byte[] { 9 });
        var read = TestVolumes.ReadAll(vol, other);
        Assert.All(read[..300_000], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Truncate_down_frees_clusters_and_up_gives_zeros()
    {
        var (_, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "t");
        vol.Write(file, 0, TestVolumes.RandomBytes(200_000, 1));
        long usedBefore = vol.TotalBytes - vol.FreeBytes;

        vol.SetLength(file, 20_000);
        Assert.Equal(20_000, file.Size);
        Assert.True(vol.TotalBytes - vol.FreeBytes < usedBefore);
        TestVolumes.AssertClean(vol);

        vol.SetLength(file, 40_000);
        var all = TestVolumes.ReadAll(vol, file);
        Assert.All(all[20_000..], b => Assert.Equal(0, b));

        vol.SetLength(file, 0);
        Assert.Equal(0u, file.FirstCluster);
        Assert.Equal(vol.TotalBytes - vol.ClusterSize, vol.FreeBytes);
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Delete_gives_the_space_back()
    {
        var (_, vol) = TestVolumes.Create();
        long free = vol.FreeBytes;
        var file = vol.CreateFile(vol.Root, "big");
        vol.Write(file, 0, new byte[3_000_000]);
        Assert.True(vol.FreeBytes < free - 2_900_000);

        vol.Delete(file);
        Assert.Equal(free, vol.FreeBytes);
        Assert.Null(vol.Find("big"));
        Assert.True(file.IsDeleted);
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Deleting_a_directory_needs_it_empty()
    {
        var (_, vol) = TestVolumes.Create();
        var dir = vol.CreateDirectory(vol.Root, "d");
        var file = vol.CreateFile(dir, "f");

        var ex = Assert.Throws<FatxException>(() => vol.Delete(dir));
        Assert.Equal(FatxError.DirectoryNotEmpty, ex.Error);

        vol.Delete(file);
        vol.Delete(dir);
        Assert.Empty(vol.GetChildren(vol.Root));
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Names_ignore_case_but_keep_it()
    {
        var (_, vol) = TestVolumes.Create();
        vol.CreateFile(vol.Root, "Hello.TXT");
        Assert.NotNull(vol.Find("hello.txt"));
        Assert.Equal("Hello.TXT", vol.Find("HELLO.txt")!.Name);
        var ex = Assert.Throws<FatxException>(() => vol.CreateFile(vol.Root, "HELLO.TXT"));
        Assert.Equal(FatxError.AlreadyExists, ex.Error);
    }

    [Fact]
    public void Bad_names_are_rejected()
    {
        var (_, vol) = TestVolumes.Create();
        foreach (var bad in new[] { "a?b", "", new string('x', 43), "tab\there", "ü" })
        {
            var ex = Assert.Throws<FatxException>(() => vol.CreateFile(vol.Root, bad));
            Assert.Equal(FatxError.InvalidName, ex.Error);
        }
    }

    [Fact]
    public void Long_paths_are_refused()
    {
        var (_, vol) = TestVolumes.Create();
        var dir = vol.Root;
        FatxException? failure = null;
        for (int i = 0; i < 20 && failure is null; i++)
        {
            try { dir = vol.CreateDirectory(dir, new string('d', 30)); }
            catch (FatxException ex) { failure = ex; }
        }
        Assert.NotNull(failure);
        Assert.Equal(FatxError.PathTooLong, failure!.Error);
    }

    [Fact]
    public void Directories_grow_past_one_cluster()
    {
        var (dev, vol) = TestVolumes.Create();
        var dir = vol.CreateDirectory(vol.Root, "many");
        for (int i = 0; i < 700; i++)
            vol.CreateFile(dir, $"file{i:D4}.dat");

        Assert.Equal(700, vol.GetChildren(dir).Count);
        TestVolumes.AssertClean(vol);

        vol = TestVolumes.Reopen(dev, vol);
        dir = vol.Resolve("many");
        Assert.Equal(700, vol.GetChildren(dir).Count);
        Assert.NotNull(vol.Find("many\\file0699.dat"));
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Deleted_slots_get_reused()
    {
        var (_, vol) = TestVolumes.Create();
        var dir = vol.CreateDirectory(vol.Root, "d");
        var nodes = Enumerable.Range(0, 50).Select(i => vol.CreateFile(dir, $"f{i}")).ToList();
        long clusters = vol.Resolve("d").Map!.Count;

        foreach (var n in nodes.Where((_, i) => i % 2 == 0))
            vol.Delete(n);
        for (int i = 0; i < 25; i++)
            vol.CreateFile(dir, $"new{i}");

        Assert.Equal(50, vol.GetChildren(dir).Count);
        Assert.Equal(clusters, dir.Map!.Count);
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void A_directory_stops_at_4096_entries()
    {
        var (_, vol) = TestVolumes.Create(64L << 20, 8);
        var dir = vol.CreateDirectory(vol.Root, "full");
        for (int i = 0; i < FatxVolume.MaxEntriesPerDirectory; i++)
            vol.CreateFile(dir, $"{i}");

        var ex = Assert.Throws<FatxException>(() => vol.CreateFile(dir, "one-too-many"));
        Assert.Equal(FatxError.DirectoryFull, ex.Error);

        vol.Delete(vol.Find("full\\17")!);
        vol.CreateFile(dir, "fits-now");
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Rename_in_place()
    {
        var (dev, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "before.bin");
        vol.Write(file, 0, TestVolumes.RandomBytes(5000, 1));

        vol.Rename(file, vol.Root, "after.bin");
        Assert.Null(vol.Find("before.bin"));
        Assert.Same(file, vol.Find("after.bin"));

        vol = TestVolumes.Reopen(dev, vol);
        Assert.Equal(5000, vol.Resolve("after.bin").Size);
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Rename_can_change_only_the_case()
    {
        var (_, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "readme.txt");
        vol.Rename(file, vol.Root, "README.TXT");
        Assert.Equal("README.TXT", vol.Find("readme.txt")!.Name);
    }

    [Fact]
    public void Moving_a_directory_takes_its_children_along()
    {
        var (dev, vol) = TestVolumes.Create();
        var a = vol.CreateDirectory(vol.Root, "a");
        var b = vol.CreateDirectory(vol.Root, "b");
        var inner = vol.CreateDirectory(a, "inner");
        var file = vol.CreateFile(inner, "deep.bin");
        var data = TestVolumes.RandomBytes(40_000, 11);
        vol.Write(file, 0, data);

        vol.Rename(a, b, "moved");
        Assert.Null(vol.Find("a"));
        Assert.Equal("\\b\\moved\\inner\\deep.bin", file.Path);

        vol = TestVolumes.Reopen(dev, vol);
        Assert.Equal(data, TestVolumes.ReadAll(vol, vol.Resolve("b\\moved\\inner\\deep.bin")));
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Cant_move_a_directory_into_itself()
    {
        var (_, vol) = TestVolumes.Create();
        var a = vol.CreateDirectory(vol.Root, "a");
        var b = vol.CreateDirectory(a, "b");
        Assert.Throws<FatxException>(() => vol.Rename(a, b, "a"));
        Assert.Throws<FatxException>(() => vol.Rename(a, a, "a2"));
    }

    [Fact]
    public void Rename_over_an_existing_file_only_when_asked()
    {
        var (_, vol) = TestVolumes.Create();
        var one = vol.CreateFile(vol.Root, "one");
        var two = vol.CreateFile(vol.Root, "two");
        vol.Write(one, 0, new byte[] { 1 });
        vol.Write(two, 0, new byte[] { 2 });

        var ex = Assert.Throws<FatxException>(() => vol.Rename(one, vol.Root, "two"));
        Assert.Equal(FatxError.AlreadyExists, ex.Error);

        vol.Rename(one, vol.Root, "two", replaceExisting: true);
        Assert.Equal(new byte[] { 1 }, TestVolumes.ReadAll(vol, vol.Find("two")!));
        Assert.True(two.IsDeleted);
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Disk_full_leaves_everything_consistent()
    {
        var (_, vol) = TestVolumes.Create(6L << 20);
        var kept = new List<(FatxNode, byte[])>();
        FatxException? full = null;
        for (int i = 0; i < 100 && full is null; i++)
        {
            var file = vol.CreateFile(vol.Root, $"f{i}");
            var data = TestVolumes.RandomBytes(500_000 + i, i);
            try
            {
                vol.Write(file, 0, data);
                kept.Add((file, data));
            }
            catch (FatxException ex)
            {
                full = ex;
            }
        }
        Assert.NotNull(full);
        Assert.Equal(FatxError.DiskFull, full!.Error);
        foreach (var (file, data) in kept)
            Assert.Equal(data, TestVolumes.ReadAll(vol, file));
        TestVolumes.AssertClean(vol);

        // and space freed up is usable again
        vol.Delete(kept[0].Item1);
        var again = vol.CreateFile(vol.Root, "again");
        vol.Write(again, 0, TestVolumes.RandomBytes(400_000, 1));
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Failed_extend_changes_nothing()
    {
        var (_, vol) = TestVolumes.Create(4L << 20);
        var file = vol.CreateFile(vol.Root, "f");
        vol.Write(file, 0, new byte[1000]);
        long free = vol.FreeBytes;

        Assert.Throws<FatxException>(() => vol.SetLength(file, 3_900_000_000));
        Assert.Throws<FatxException>(() => vol.Write(file, 0, new byte[8_000_000]));
        Assert.Equal(1000, file.Size);
        Assert.Equal(free, vol.FreeBytes);
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Four_gigabytes_is_the_ceiling()
    {
        var (_, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "huge");
        var ex = Assert.Throws<FatxException>(() => vol.SetLength(file, 0x1_0000_0000));
        Assert.Equal(FatxError.FileTooLarge, ex.Error);
        ex = Assert.Throws<FatxException>(() => vol.Write(file, 0xFFFF_FFFF, new byte[2]));
        Assert.Equal(FatxError.FileTooLarge, ex.Error);
    }

    [Fact]
    public void A_fragmented_file_reads_back_right()
    {
        var (dev, vol) = TestVolumes.Create(32L << 20, 8);
        var fillers = Enumerable.Range(0, 200).Select(i =>
        {
            var f = vol.CreateFile(vol.Root, $"filler{i}");
            vol.Write(f, 0, new byte[4096]);
            return f;
        }).ToList();
        foreach (var f in fillers.Where((_, i) => i % 2 == 0))
            vol.Delete(f);

        var big = vol.CreateFile(vol.Root, "big");
        var data = TestVolumes.RandomBytes(600_000, 21);
        vol.Write(big, 0, data);
        Assert.True(big.Map!.Extents.Count > 20, "test needs the file to be spread out");

        vol = TestVolumes.Reopen(dev, vol);
        Assert.Equal(data, TestVolumes.ReadAll(vol, vol.Resolve("big")));
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void A_fresh_file_is_one_contiguous_run()
    {
        var (_, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "seq");
        for (int i = 0; i < 40; i++)
            vol.Write(file, file.Size, new byte[50_000]);
        Assert.Single(file.Map!.Extents);
    }

    [Fact]
    public void Read_only_volumes_refuse_changes()
    {
        var (dev, vol) = TestVolumes.Create();
        vol.CreateFile(vol.Root, "a");
        vol.Dispose();

        using var ro = FatxVolume.Open(dev, writable: false);
        Assert.True(ro.IsReadOnly);
        var a = ro.Resolve("a");
        Assert.Throws<FatxException>(() => ro.CreateFile(ro.Root, "b"));
        Assert.Throws<FatxException>(() => ro.Write(a, 0, new byte[1]));
        Assert.Throws<FatxException>(() => ro.Delete(a));
        Assert.Throws<FatxException>(() => ro.Rename(a, ro.Root, "c"));
    }

    [Fact]
    public void Timestamps_are_set_and_kept()
    {
        var (dev, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "t");
        Assert.True((DateTime.Now - file.Created).TotalMinutes < 2);

        var stamp = new DateTime(2012, 3, 4, 5, 6, 8);
        vol.SetTimes(file, stamp, stamp.AddHours(1), stamp.AddHours(2));
        vol = TestVolumes.Reopen(dev, vol);
        var back = vol.Resolve("t");
        Assert.Equal(stamp, back.Created);
        Assert.Equal(stamp.AddHours(1), back.Modified);
        Assert.Equal(stamp.AddHours(2), back.Accessed);
    }

    [Fact]
    public void Attributes_keep_the_directory_bit()
    {
        var (_, vol) = TestVolumes.Create();
        var dir = vol.CreateDirectory(vol.Root, "d");
        vol.SetAttributes(dir, FatxAttributes.Hidden);
        Assert.True(dir.IsDirectory);
        Assert.True(dir.Attributes.HasFlag(FatxAttributes.Hidden));
    }

    [Fact]
    public void Works_with_32_bit_fat_and_small_clusters()
    {
        var (dev, vol) = TestVolumes.Create(2L << 30, 8);
        Assert.Equal(4, vol.Geometry.FatEntrySize);
        var file = vol.CreateFile(vol.CreateDirectory(vol.Root, "x"), "y");
        var data = TestVolumes.RandomBytes(2_500_000, 5);
        vol.Write(file, 0, data);

        vol = TestVolumes.Reopen(dev, vol);
        Assert.Equal(data, TestVolumes.ReadAll(vol, vol.Resolve("x\\y")));
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Works_with_16_bit_fat()
    {
        var (dev, vol) = TestVolumes.Create(40L << 20);
        Assert.Equal(2, vol.Geometry.FatEntrySize);
        var file = vol.CreateFile(vol.Root, "y");
        var data = TestVolumes.RandomBytes(900_000, 6);
        vol.Write(file, 0, data);
        vol = TestVolumes.Reopen(dev, vol);
        Assert.Equal(data, TestVolumes.ReadAll(vol, vol.Resolve("y")));
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Little_endian_volumes_work_too()
    {
        var (dev, vol) = TestVolumes.Create(64L << 20, 0x20, FatxFlavor.Xbox);
        var dir = vol.CreateDirectory(vol.Root, "Dir");
        var file = vol.CreateFile(dir, "file.bin");
        var data = TestVolumes.RandomBytes(123_456, 2);
        vol.Write(file, 0, data);
        var when = new DateTime(2004, 7, 8, 9, 10, 12);
        vol.SetTimes(file, when, when, when);

        vol = TestVolumes.Reopen(dev, vol);
        var back = vol.Resolve("Dir\\file.bin");
        Assert.Equal(FatxFlavor.Xbox, vol.Flavor);
        Assert.Equal(data, TestVolumes.ReadAll(vol, back));
        Assert.Equal(when, back.Modified);
        TestVolumes.AssertClean(vol);
    }

    [Fact]
    public void Stream_wrapper_copies()
    {
        var (_, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "s");
        var data = TestVolumes.RandomBytes(250_000, 8);
        using (var w = new FatxStream(vol, file, writable: true))
            new MemoryStream(data).CopyTo(w);

        using var r = new FatxStream(vol, file);
        var ms = new MemoryStream();
        r.CopyTo(ms);
        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public void Entries_not_flushed_yet_are_written_by_flush()
    {
        var (dev, vol) = TestVolumes.Create();
        var file = vol.CreateFile(vol.Root, "f");
        vol.Write(file, 0, new byte[100]);
        vol.Write(file, 10, new byte[5]);   // overwrite only, entry stays dirty
        var modified = file.Modified;
        vol.Flush();

        var other = FatxVolume.Open(dev);
        Assert.Equal(modified, other.Resolve("f").Modified);
    }
}
