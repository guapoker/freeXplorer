using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FreeXplorer.Fatx;
using FreeXplorer.Storage;

namespace FreeXplorer.Tests;

// a device that can be told to misbehave, and to call back so a test can step in mid-copy
sealed class MischiefDevice : IBlockDevice
{
    readonly IBlockDevice inner;
    public MischiefDevice(IBlockDevice inner) => this.inner = inner;

    // flips one bit of whatever gets written over this offset
    public long? FlipOnWriteAt { get; set; }
    public Action<long>? AfterRead { get; set; }
    public Action<long>? AfterWrite { get; set; }
    public int Writes { get; private set; }
    public long BytesWritten { get; private set; }

    public string Name => inner.Name;
    public long Length => inner.Length;
    public int SectorSize => inner.SectorSize;
    public bool CanWrite => inner.CanWrite;

    public void Read(long offset, Span<byte> buffer)
    {
        inner.Read(offset, buffer);
        AfterRead?.Invoke(offset);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        Writes++;
        BytesWritten += buffer.Length;
        if (FlipOnWriteAt is { } at && at >= offset && at < offset + buffer.Length)
        {
            var copy = buffer.ToArray();
            copy[at - offset] ^= 0x01;
            inner.Write(offset, copy);
        }
        else
        {
            inner.Write(offset, buffer);
        }
        AfterWrite?.Invoke(offset);
    }

    public void Flush() => inner.Flush();
    public void Dispose() { }
}

public class CopyTests
{
    const int Mib = 1 << 20;

    static MemoryBlockDevice Patterned(int length, int seed)
    {
        var device = new MemoryBlockDevice(length);
        device.Write(0, TestVolumes.RandomBytes(length, seed));
        return device;
    }

    static byte[] Everything(IBlockDevice device) => device.ReadBytes(0, (int)device.Length);

    static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public void A_copy_is_exact_and_hands_back_the_checksum()
    {
        using var from = Patterned(9 * Mib + 12345 / 512 * 512, 1);
        using var to = new MemoryBlockDevice(from.Length);

        string hash = DriveCopy.Copy(from, to, from.Length, new CopyOptions(), null, default);

        Assert.Equal(Everything(from), Everything(to));
        Assert.Equal(Sha(Everything(from)), hash);
    }

    [Fact]
    public void Only_the_asked_for_length_is_copied()
    {
        using var from = Patterned(4 * Mib, 2);
        using var to = new MemoryBlockDevice(4 * Mib);
        DriveCopy.Copy(from, to, 1 * Mib, new CopyOptions(), null, default);

        Assert.Equal(from.ReadBytes(0, Mib), to.ReadBytes(0, Mib));
        Assert.All(to.ReadBytes(Mib, Mib), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Empty_blocks_can_be_skipped()
    {
        using var from = new MemoryBlockDevice(10 * Mib);
        from.Write(3 * Mib + 100, TestVolumes.RandomBytes(5000, 3));
        from.Write(9 * Mib + Mib / 2, TestVolumes.RandomBytes(70_000, 4));
        using var to = new RecordingDevice(new MemoryBlockDevice(from.Length));

        DriveCopy.Copy(from, to, from.Length, new CopyOptions { SkipZeroBlocks = true }, null, default);

        Assert.Equal(Everything(from), to.ReadBytes(0, (int)from.Length));
        Assert.True(to.BytesWritten < 400_000, $"wrote {to.BytesWritten} bytes for two small islands of data");
    }

    [Fact]
    public void Empty_blocks_are_written_over_old_data()
    {
        using var from = new MemoryBlockDevice(4 * Mib);
        from.Write(Mib, TestVolumes.RandomBytes(1000, 5));
        using var to = new MemoryBlockDevice(4 * Mib);
        to.Write(0, Enumerable.Repeat((byte)0xAA, 4 * Mib).ToArray());

        DriveCopy.Copy(from, to, from.Length, new CopyOptions(), null, default);

        Assert.Equal(Everything(from), Everything(to));
    }

    [Fact]
    public void A_drive_that_ends_in_the_middle_of_a_block_is_handled()
    {
        long length = 3 * Mib + 3 * 512;
        using var from = new MemoryBlockDevice(length);
        from.Write(length - 700, TestVolumes.RandomBytes(700, 6));
        using var to = new MemoryBlockDevice(length);

        DriveCopy.Copy(from, to, length, new CopyOptions { SkipZeroBlocks = true }, null, default);
        Assert.Equal(Everything(from), Everything(to));
    }

    [Fact]
    public void Progress_climbs_to_the_total_and_names_its_phase()
    {
        using var from = Patterned(20 * Mib, 7);
        using var to = new MemoryBlockDevice(from.Length);
        var seen = new List<CopyProgress>();

        DriveCopy.Copy(from, to, from.Length, new CopyOptions(), seen.Add, default, "Writing the drive");

        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal("Writing the drive", p.Phase));
        Assert.Equal(0, seen[0].Done);
        Assert.Equal(from.Length, seen[^1].Done);
        Assert.Equal(1.0, seen[^1].Fraction);
        Assert.Equal(seen.Select(p => p.Done).OrderBy(d => d), seen.Select(p => p.Done));
    }

    [Fact]
    public void Time_left_needs_a_speed_first()
    {
        Assert.Null(new CopyProgress("x", 0, 1000, 0).Remaining);
        Assert.Equal(TimeSpan.FromSeconds(8), new CopyProgress("x", 200, 1000, 100).Remaining);
        Assert.Null(new CopyProgress("x", 1000, 1000, 100).Remaining);
        Assert.Equal(0.25, new CopyProgress("x", 250, 1000, 0).Fraction);
        Assert.Equal(1.0, new CopyProgress("x", 0, 0, 0).Fraction);
    }

    [Fact]
    public void Cancelling_stops_the_copy_part_way()
    {
        using var from = new MischiefDevice(Patterned(40 * Mib, 8));
        using var to = new MemoryBlockDevice(from.Length);
        using var cts = new CancellationTokenSource();
        from.AfterRead = offset =>
        {
            if (offset >= 8 * Mib)
                cts.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() =>
            DriveCopy.Copy(from, to, from.Length, new CopyOptions(), null, cts.Token));
        // it stopped soon after the cancel, it didn't plough on to the end
        Assert.All(to.ReadBytes(20 * Mib, Mib), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Hash_matches_a_normal_sha256_and_can_be_cancelled()
    {
        using var device = Patterned(6 * Mib, 9);
        Assert.Equal(Sha(Everything(device)), DriveCopy.Hash(device, device.Length, null, default));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => DriveCopy.Hash(device, device.Length, null, cts.Token));
    }

    [Fact]
    public void Copy_can_start_part_way_in()
    {
        using var from = Patterned(8 * Mib, 20);
        using var to = new MemoryBlockDevice(8 * Mib);
        to.Write(0, Enumerable.Repeat((byte)0x33, 8 * Mib).ToArray());

        string hash = DriveCopy.Copy(from, to, from.Length, new CopyOptions(), null, default, "Copying", start: Mib);

        Assert.All(to.ReadBytes(0, Mib), b => Assert.Equal(0x33, b));
        Assert.Equal(from.ReadBytes(Mib, 7 * Mib), to.ReadBytes(Mib, 7 * Mib));
        Assert.Equal(Sha(from.ReadBytes(Mib, 7 * Mib)), hash);
    }

    [Fact]
    public void Reading_back_from_a_start_hashes_only_that_part()
    {
        using var device = Patterned(6 * Mib, 21);
        Assert.Equal(Sha(device.ReadBytes(2 * Mib, 4 * Mib)), DriveCopy.Hash(device, device.Length, null, default, "Checking", start: 2 * Mib));
    }

    [Fact]
    public void Progress_counts_only_what_is_copied()
    {
        using var from = Patterned(10 * Mib, 22);
        using var to = new MemoryBlockDevice(from.Length);
        var seen = new List<CopyProgress>();

        DriveCopy.Copy(from, to, from.Length, new CopyOptions(), seen.Add, default, "Writing", start: 4 * Mib);

        Assert.All(seen, p => Assert.Equal(6 * Mib, p.Total));
        Assert.Equal(0, seen[0].Done);
        Assert.Equal(6 * Mib, seen[^1].Done);
    }

    [Fact]
    public void A_start_past_the_end_is_refused()
    {
        using var from = Patterned(2 * Mib, 23);
        using var to = new MemoryBlockDevice(from.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => DriveCopy.Copy(from, to, from.Length, new CopyOptions(), null, default, "x", start: 3 * Mib));
        Assert.Throws<ArgumentOutOfRangeException>(() => DriveCopy.Copy(from, to, from.Length, new CopyOptions(), null, default, "x", start: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DriveCopy.Hash(from, from.Length, null, default, "x", start: 3 * Mib));
    }

    [Fact]
    public void Silly_lengths_are_refused()
    {
        using var small = new MemoryBlockDevice(Mib);
        using var big = new MemoryBlockDevice(2 * Mib);
        Assert.Throws<ArgumentOutOfRangeException>(() => DriveCopy.Copy(small, big, 2 * Mib, new CopyOptions(), null, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => DriveCopy.Copy(big, small, 2 * Mib, new CopyOptions(), null, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => DriveCopy.Hash(small, 2 * Mib, null, default));
    }

    [Fact]
    public void A_read_only_destination_is_refused()
    {
        using var from = Patterned(Mib, 10);
        using var inner = new MemoryBlockDevice(Mib);
        using var to = new SliceDevice(inner, 0, Mib, writable: false);
        Assert.Throws<NotSupportedException>(() => DriveCopy.Copy(from, to, Mib, new CopyOptions(), null, default));
    }
}

public class BackupTests : IDisposable
{
    const int Mib = 1 << 20;

    readonly string dir = Path.Combine(Path.GetTempPath(), "fx-backup-" + Guid.NewGuid().ToString("N"));

    public BackupTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, true);

    string P(string name) => Path.Combine(dir, name);

    static MemoryBlockDevice Drive(long length, params (long At, int Size, int Seed)[] islands)
    {
        var device = new MemoryBlockDevice(length);
        foreach (var (at, size, seed) in islands)
            device.Write(at, TestVolumes.RandomBytes(size, seed));
        return device;
    }

    static byte[] Everything(IBlockDevice device) => device.ReadBytes(0, (int)device.Length);

    bool TempIsNtfs() => new DriveInfo(Path.GetPathRoot(dir)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    // ---- making one ----

    [Fact]
    public void Backup_is_the_whole_drive_plus_a_checksum()
    {
        using var drive = Drive(32L * Mib, (100, 4000, 1), (20L * Mib, 300_000, 2));
        var result = DriveBackup.Create(drive, P("drive.img"), new BackupOptions(), null, default);

        Assert.Equal(drive.Length, new System.IO.FileInfo(P("drive.img")).Length);
        Assert.Equal(Everything(drive), File.ReadAllBytes(P("drive.img")));
        Assert.Equal(SHA256.HashData(Everything(drive)), Convert.FromHexString(result.Sha256));
        Assert.True(result.Verified);
        Assert.Equal(drive.Length, result.Length);

        // the same layout sha256sum writes, so it can be checked with other tools
        string sidecar = File.ReadAllText(P("drive.img.sha256"));
        Assert.Matches("^[0-9a-f]{64}  drive.img\n$", sidecar);
        Assert.StartsWith(result.Sha256, sidecar);
        Assert.Equal(DriveBackup.ChecksumPath(P("drive.img")), P("drive.img.sha256"));
    }

    [Fact]
    public void An_empty_looking_drive_takes_next_to_no_room()
    {
        if (!TempIsNtfs())
            return;   // only NTFS can leave the empty parts out

        using var drive = Drive(256L * Mib, (50L * Mib, 100_000, 3));
        var result = DriveBackup.Create(drive, P("sparse.img"), new BackupOptions(), null, default);

        Assert.Equal(256L * Mib, new System.IO.FileInfo(P("sparse.img")).Length);
        Assert.True(result.SizeOnDisk < 8L * Mib, $"took {Size.Format(result.SizeOnDisk)} on disk");
        Assert.Equal(Everything(drive), File.ReadAllBytes(P("sparse.img")));
    }

    [Fact]
    public void Skipping_the_sparse_trick_gives_the_same_file()
    {
        using var drive = Drive(8L * Mib, (Mib, 5000, 4));
        DriveBackup.Create(drive, P("plain.img"), new BackupOptions(Sparse: false), null, default);
        Assert.Equal(Everything(drive), File.ReadAllBytes(P("plain.img")));
    }

    [Fact]
    public void A_file_that_is_already_there_is_never_touched()
    {
        File.WriteAllText(P("mine.img"), "precious");
        File.WriteAllText(P("mine.img.sha256"), "also precious");
        using var drive = Drive(2L * Mib);

        Assert.ThrowsAny<IOException>(() => DriveBackup.Create(drive, P("mine.img"), new BackupOptions(), null, default));

        Assert.Equal("precious", File.ReadAllText(P("mine.img")));
        Assert.Equal("also precious", File.ReadAllText(P("mine.img.sha256")));
    }

    [Fact]
    public void Cancelling_a_backup_leaves_nothing_behind()
    {
        using var inner = Drive(40L * Mib, (0, 40 * Mib, 5));
        using var drive = new MischiefDevice(inner);
        using var cts = new CancellationTokenSource();
        drive.AfterRead = offset =>
        {
            if (offset >= 8L * Mib)
                cts.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() => DriveBackup.Create(drive, P("cancelled.img"), new BackupOptions(), null, cts.Token));

        Assert.False(File.Exists(P("cancelled.img")));
        Assert.False(File.Exists(P("cancelled.img.sha256")));
    }

    [Fact]
    public void A_drive_that_fails_to_read_leaves_nothing_behind_either()
    {
        using var inner = Drive(8L * Mib, (0, 8 * Mib, 6));
        using var drive = new MischiefDevice(inner)
        {
            AfterRead = offset =>
            {
                if (offset >= 4L * Mib)
                    throw new IOException("error reading the drive at byte 4194304");
            },
        };

        var ex = Assert.Throws<IOException>(() => DriveBackup.Create(drive, P("bad.img"), new BackupOptions(), null, default));
        Assert.Contains("error reading", ex.Message);
        Assert.False(File.Exists(P("bad.img")));
        Assert.False(File.Exists(P("bad.img.sha256")));
    }

    [Fact]
    public void A_backup_that_reads_back_differently_is_thrown_away()
    {
        using var drive = Drive(12L * Mib, (0, 12 * Mib, 7));
        // as soon as the checking pass begins, something damages the file
        void Damage(CopyProgress p)
        {
            if (p.Phase == "Checking the backup" && p.Done == 0)
            {
                using var handle = File.OpenHandle(P("rot.img"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                RandomAccess.Write(handle, new byte[] { 0x5A, 0x5A }, 5L * Mib);
            }
        }

        var ex = Assert.Throws<BackupException>(() => DriveBackup.Create(drive, P("rot.img"), new BackupOptions(), Damage, default));
        Assert.Contains("doesn't read back the same", ex.Message);
        Assert.False(File.Exists(P("rot.img")));
        Assert.False(File.Exists(P("rot.img.sha256")));
    }

    [Fact]
    public void Checking_can_be_turned_off()
    {
        using var drive = Drive(4L * Mib, (0, 1000, 8));
        var phases = new List<string>();
        var result = DriveBackup.Create(drive, P("quick.img"), new BackupOptions(Verify: false), p => phases.Add(p.Phase), default);

        Assert.False(result.Verified);
        Assert.DoesNotContain("Checking the backup", phases);
        Assert.Contains("Copying the drive", phases);
    }

    // ---- putting one back ----

    [Fact]
    public void Restore_clones_and_leaves_a_bigger_drives_tail()
    {
        using var drive = Drive(24L * Mib, (0, 2000, 1), (13L * Mib, 500_000, 2), (24L * Mib - 4096, 4096, 3));
        DriveBackup.Create(drive, P("clone.img"), new BackupOptions(), null, default);

        using var target = new MemoryBlockDevice(30L * Mib);
        target.Write(0, Enumerable.Repeat((byte)0x55, 30 * Mib).ToArray());

        var result = DriveBackup.Restore(P("clone.img"), target, new RestoreOptions(), null, default);

        Assert.Equal(Everything(drive), target.ReadBytes(0, 24 * Mib));
        Assert.All(target.ReadBytes(24 * Mib, 6 * Mib), b => Assert.Equal(0x55, b));
        Assert.True(result.Verified);
        Assert.True(result.ChecksumFileUsed);
        Assert.Equal(24L * Mib, result.Length);
    }

    [Fact]
    public void Old_data_on_the_target_does_not_survive_in_the_empty_parts()
    {
        using var drive = Drive(8L * Mib, (Mib, 100, 4));   // mostly empty
        DriveBackup.Create(drive, P("mostly-empty.img"), new BackupOptions(), null, default);

        using var target = new MemoryBlockDevice(8L * Mib);
        target.Write(0, Enumerable.Repeat((byte)0xEE, 8 * Mib).ToArray());
        DriveBackup.Restore(P("mostly-empty.img"), target, new RestoreOptions(), null, default);

        Assert.Equal(Everything(drive), Everything(target));
    }

    [Fact]
    public void Too_small_a_drive_is_refused()
    {
        using var drive = Drive(16L * Mib, (0, 16 * Mib, 5));
        DriveBackup.Create(drive, P("big.img"), new BackupOptions(), null, default);

        using var inner = new MemoryBlockDevice(8L * Mib);
        using var target = new RecordingDevice(inner);
        var ex = Assert.Throws<InvalidOperationException>(() => DriveBackup.Restore(P("big.img"), target, new RestoreOptions(), null, default));

        Assert.Contains("at least as big", ex.Message);
        Assert.Equal(0, target.BytesWritten);
    }

    [Fact]
    public void A_damaged_backup_is_caught_before_the_drive_is_touched()
    {
        using var drive = Drive(10L * Mib, (0, 10 * Mib, 6));
        DriveBackup.Create(drive, P("damaged.img"), new BackupOptions(), null, default);
        using (var handle = File.OpenHandle(P("damaged.img"), FileMode.Open, FileAccess.Write, FileShare.None))
            RandomAccess.Write(handle, new byte[] { 0xFF }, 3L * Mib);

        using var inner = new MemoryBlockDevice(10L * Mib);
        using var target = new RecordingDevice(inner);
        var ex = Assert.Throws<BackupException>(() => DriveBackup.Restore(P("damaged.img"), target, new RestoreOptions(), null, default));

        Assert.Contains("damaged", ex.Message);
        Assert.Contains("Nothing was written", ex.Message);
        Assert.Equal(0, target.BytesWritten);
    }

    [Fact]
    public void Restore_without_a_checksum_file_still_works()
    {
        using var drive = Drive(6L * Mib, (0, 6 * Mib, 7));
        DriveBackup.Create(drive, P("nosum.img"), new BackupOptions(), null, default);
        File.Delete(P("nosum.img.sha256"));

        using var target = new MemoryBlockDevice(6L * Mib);
        var result = DriveBackup.Restore(P("nosum.img"), target, new RestoreOptions(), null, default);

        Assert.False(result.ChecksumFileUsed);
        Assert.True(result.Verified);
        Assert.Equal(Everything(drive), Everything(target));
    }

    [Fact]
    public void A_drive_that_does_not_keep_what_was_written_is_found_out()
    {
        using var drive = Drive(10L * Mib, (0, 10 * Mib, 8));
        DriveBackup.Create(drive, P("flip.img"), new BackupOptions(), null, default);

        using var inner = new MemoryBlockDevice(10L * Mib);
        using var target = new MischiefDevice(inner) { FlipOnWriteAt = 6L * Mib + 123 };
        var ex = Assert.Throws<BackupException>(() => DriveBackup.Restore(P("flip.img"), target, new RestoreOptions(), null, default));

        Assert.Contains("doesn't read back the same", ex.Message);
        Assert.Contains("don't use it", ex.Message);
    }

    [Fact]
    public void Checking_the_drive_afterwards_can_be_turned_off()
    {
        using var drive = Drive(6L * Mib, (0, 6 * Mib, 9));
        DriveBackup.Create(drive, P("nocheck.img"), new BackupOptions(), null, default);

        using var inner = new MemoryBlockDevice(6L * Mib);
        using var target = new MischiefDevice(inner) { FlipOnWriteAt = 2L * Mib };
        var phases = new List<string>();
        var result = DriveBackup.Restore(P("nocheck.img"), target, new RestoreOptions(Verify: false), p => phases.Add(p.Phase), default);

        Assert.False(result.Verified);
        Assert.DoesNotContain("Checking the drive", phases);
    }

    [Fact]
    public void A_read_only_drive_is_refused()
    {
        using var drive = Drive(2L * Mib, (0, 1000, 1));
        DriveBackup.Create(drive, P("ro.img"), new BackupOptions(), null, default);

        using var inner = new MemoryBlockDevice(2L * Mib);
        using var target = new SliceDevice(inner, 0, 2L * Mib, writable: false);
        Assert.Throws<NotSupportedException>(() => DriveBackup.Restore(P("ro.img"), target, new RestoreOptions(), null, default));
    }

    [Fact]
    public void An_empty_backup_file_is_refused()
    {
        File.WriteAllBytes(P("empty.img"), Array.Empty<byte>());
        using var target = new MemoryBlockDevice(Mib);
        Assert.Throws<BackupException>(() => DriveBackup.Restore(P("empty.img"), target, new RestoreOptions(), null, default));
    }

    [Fact]
    public void A_restore_can_be_cancelled()
    {
        using var drive = Drive(30L * Mib, (0, 30 * Mib, 10));
        DriveBackup.Create(drive, P("stop.img"), new BackupOptions(), null, default);

        using var inner = new MemoryBlockDevice(30L * Mib);
        using var target = new MischiefDevice(inner);
        using var cts = new CancellationTokenSource();
        target.AfterWrite = offset =>
        {
            if (offset >= 8L * Mib)
                cts.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() => DriveBackup.Restore(P("stop.img"), target, new RestoreOptions(), null, cts.Token));
        Assert.True(target.BytesWritten < 30L * Mib);
        Assert.All(inner.ReadBytes(20 * Mib, Mib), b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_restore_reports_each_stage_in_order()
    {
        using var drive = Drive(6L * Mib, (0, 6 * Mib, 11));
        DriveBackup.Create(drive, P("stages.img"), new BackupOptions(), null, default);

        using var target = new MemoryBlockDevice(6L * Mib);
        var phases = new List<string>();
        DriveBackup.Restore(P("stages.img"), target, new RestoreOptions(), p =>
        {
            if (phases.Count == 0 || phases[^1] != p.Phase)
                phases.Add(p.Phase);
        }, default);

        Assert.Equal(new[] { "Checking the backup", "Writing the drive", "Checking the drive" }, phases);
    }

    [Fact]
    public void A_restore_can_leave_the_start_of_the_drive_as_it_was()
    {
        const long skip = 512 * 1024;
        using var drive = Drive(24L * Mib, (0, (int)(24 * Mib), 1));   // different bytes everywhere, the start included
        DriveBackup.Create(drive, P("skip.img"), new BackupOptions(), null, default);

        using var inner = new MemoryBlockDevice(24L * Mib);
        inner.Write(0, Enumerable.Repeat((byte)0x55, (int)(24 * Mib)).ToArray());
        using var target = new RecordingDevice(inner);

        var result = DriveBackup.Restore(P("skip.img"), target, new RestoreOptions { SkipFirstBytes = skip }, null, default);

        Assert.All(inner.ReadBytes(0, (int)skip), b => Assert.Equal(0x55, b));
        Assert.Equal(drive.ReadBytes(skip, (int)(24 * Mib - skip)), inner.ReadBytes(skip, (int)(24 * Mib - skip)));
        Assert.True(target.LowestWrite >= skip);
        Assert.Equal(skip, result.Skipped);
        Assert.True(result.Verified);
        Assert.True(result.ChecksumFileUsed);
    }

    [Fact]
    public void Read_back_covers_only_what_was_written()
    {
        const long skip = 512 * 1024;
        using var drive = Drive(12L * Mib, (0, (int)(12 * Mib), 2));
        DriveBackup.Create(drive, P("skipcheck.img"), new BackupOptions(), null, default);

        using var inner = new MemoryBlockDevice(12L * Mib);
        using var target = new MischiefDevice(inner) { FlipOnWriteAt = 6L * Mib + 5 };
        var ex = Assert.Throws<BackupException>(() =>
            DriveBackup.Restore(P("skipcheck.img"), target, new RestoreOptions { SkipFirstBytes = skip }, null, default));
        Assert.Contains("doesn't read back the same", ex.Message);
    }

    [Fact]
    public void Restore_progress_skips_the_start()
    {
        const long skip = 1L << 20;
        using var drive = Drive(10L * Mib, (0, (int)(10 * Mib), 3));
        DriveBackup.Create(drive, P("skipprog.img"), new BackupOptions(), null, default);

        using var target = new MemoryBlockDevice(10L * Mib);
        var writing = new List<CopyProgress>();
        DriveBackup.Restore(P("skipprog.img"), target, new RestoreOptions { SkipFirstBytes = skip },
            p => { if (p.Phase == "Writing the drive") writing.Add(p); }, default);

        Assert.All(writing, p => Assert.Equal(9L * Mib, p.Total));
        Assert.Equal(9L * Mib, writing[^1].Done);
    }

    [Fact]
    public void Skipping_the_whole_drive_is_refused()
    {
        using var drive = Drive(4L * Mib, (0, 1000, 4));
        DriveBackup.Create(drive, P("all.img"), new BackupOptions(), null, default);

        using var inner = new MemoryBlockDevice(4L * Mib);
        using var target = new RecordingDevice(inner);
        Assert.Throws<InvalidOperationException>(() =>
            DriveBackup.Restore(P("all.img"), target, new RestoreOptions { SkipFirstBytes = 4L * Mib }, null, default));
        Assert.Throws<InvalidOperationException>(() =>
            DriveBackup.Restore(P("all.img"), target, new RestoreOptions { SkipFirstBytes = -1 }, null, default));
        Assert.Equal(0, target.BytesWritten);
    }

    [Fact]
    public void Damage_in_the_skipped_part_is_still_caught()
    {
        using var drive = Drive(10L * Mib, (0, (int)(10 * Mib), 5));
        DriveBackup.Create(drive, P("headrot.img"), new BackupOptions(), null, default);
        using (var handle = File.OpenHandle(P("headrot.img"), FileMode.Open, FileAccess.Write, FileShare.None))
            RandomAccess.Write(handle, new byte[] { 0xEE }, 100);

        using var inner = new MemoryBlockDevice(10L * Mib);
        using var target = new RecordingDevice(inner);
        Assert.Throws<BackupException>(() =>
            DriveBackup.Restore(P("headrot.img"), target, new RestoreOptions { SkipFirstBytes = 512 * 1024 }, null, default));
        Assert.Equal(0, target.BytesWritten);
    }

    // ---- the checksum file ----

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  file.img\n", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF *file.img\r\n", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("\n\n  0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("not a checksum at all\n", null)]
    [InlineData("0123456789abcdef  too short\n", null)]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  file.img\n", null)]
    [InlineData("", null)]
    public void The_checksum_file_is_read_leniently_and_junk_is_ignored(string text, string? expected)
    {
        File.WriteAllText(P("file.img"), "x");
        File.WriteAllText(P("file.img.sha256"), text);
        Assert.Equal(expected, DriveBackup.ReadChecksum(P("file.img")));
    }

    [Fact]
    public void No_checksum_file_means_no_checksum()
    {
        File.WriteAllText(P("lonely.img"), "x");
        Assert.Null(DriveBackup.ReadChecksum(P("lonely.img")));
    }

    // ---- where it can go ----

    [Fact]
    public void A_good_place_has_no_complaints()
    {
        var check = DriveBackup.CheckDestination(P("fine.img"), 64L * Mib);
        Assert.Null(check.Problem);
        Assert.True(check.FreeBytes > 0);
    }

    [Fact]
    public void Bad_destinations_are_problems()
    {
        File.WriteAllText(P("taken.img"), "x");
        Assert.NotNull(DriveBackup.CheckDestination(P("taken.img"), Mib).Problem);
        Assert.NotNull(DriveBackup.CheckDestination(Path.Combine(dir, "nope", "x.img"), Mib).Problem);
        Assert.NotNull(DriveBackup.CheckDestination("bad\0name", Mib).Problem);
        Assert.NotNull(DriveBackup.CheckDestination(dir, Mib).Problem);
    }

    [Fact]
    public void Not_enough_room_warns_on_ntfs_only()
    {
        var check = DriveBackup.CheckDestination(P("huge.img"), long.MaxValue / 4);
        if (check.Sparse)
        {
            Assert.Null(check.Problem);
            Assert.NotNull(check.Warning);
        }
        else
        {
            Assert.NotNull(check.Problem);
        }
    }

    // ---- with a real file system on it ----

    [Fact]
    public void Restored_volume_has_its_files_and_is_clean()
    {
        var (device, volume) = TestVolumes.Create(64L << 20);
        var data = TestVolumes.RandomBytes(300_000, 42);
        var folder = volume.CreateDirectory(volume.Root, "Content");
        var file = volume.CreateFile(folder, "save.bin");
        volume.Write(file, 0, data);
        volume.Flush();
        volume.Dispose();

        DriveBackup.Create(device, P("volume.img"), new BackupOptions(), null, default);
        using var copy = new MemoryBlockDevice(64L << 20);
        DriveBackup.Restore(P("volume.img"), copy, new RestoreOptions(), null, default);

        using var back = FatxVolume.Open(copy, writable: false);
        Assert.Equal(data, TestVolumes.ReadAll(back, back.Resolve("Content\\save.bin")));
        TestVolumes.AssertClean(back);
        device.Dispose();
    }
}
