using FreeXplorer.App.Services;
using FreeXplorer.App.ViewModels;
using FreeXplorer.Fatx;
using FreeXplorer.Storage;
using FreeXplorer.Xbox360;

namespace FreeXplorer.Tests;

public class BackupViewModelTests : IDisposable
{
    const long Mib = 1L << 20;
    const long Gib = 1L << 30;

    readonly string dir = Path.Combine(Path.GetTempPath(), "fx-bkvm-" + Guid.NewGuid().ToString("N"));
    readonly FakePlatform platform = new();
    readonly FakeDialogs dialogs = new();
    readonly MainViewModel vm;

    public BackupViewModelTests()
    {
        Directory.CreateDirectory(dir);
        vm = new MainViewModel(platform, dialogs) { DeviceChangeDelay = TimeSpan.FromMilliseconds(20) };
    }

    public void Dispose()
    {
        vm.Dispose();
        Directory.Delete(dir, true);
    }

    string P(string name) => Path.Combine(dir, name);

    static PhysicalDiskInfo Info(int n, long size, bool system = false, bool volumes = false) =>
        new(n, size, 512, $"Test Drive {n}", $"SER{n}", "USB", true, volumes, system);

    // a drive with a bit of everything on it
    PhysicalDiskInfo AddDisk(int n, long size, bool system = false, bool volumes = false)
    {
        var info = Info(n, size, system, volumes);
        platform.Disks.Add(info);
        var data = new MemoryBlockDevice(size);
        data.Write(Mib, TestVolumes.RandomBytes(200_000, n));
        data.Write(size - 4096, TestVolumes.RandomBytes(4096, n + 100));
        platform.DiskData[n] = data;
        return info;
    }

    // a drive with a security sector of its own, the way one that has been in a console does
    PhysicalDiskInfo AddConsoleDisk(int n, long size, string serial, long claimed, int seed, bool system = false, bool volumes = false)
    {
        var info = Info(n, size, system, volumes);
        platform.Disks.Add(info);
        var data = SecuritySectorTests.ConsoleDrive(size, serial, claimed);
        data.Write(0x80000, TestVolumes.RandomBytes(2_000_000, seed));
        platform.DiskData[n] = data;
        return info;
    }

    PhysicalDiskInfo AddFormattedDisk(int n, long size)
    {
        var info = Info(n, size);
        platform.Disks.Add(info);
        var data = new MemoryBlockDevice(size);
        HddFormatter.Format(data, new[] { "content" });
        platform.DiskData[n] = data;
        return info;
    }

    async Task Select(PhysicalDiskInfo disk)
    {
        await vm.InitializeAsync();
        vm.SelectedSource = vm.Sources.Single(s => s.Disk?.Number == disk.Number);
        await vm.LoadingTask;
    }

    static async Task Eventually(Func<bool> condition, int milliseconds = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("it never happened");
            await Task.Delay(10);
        }
    }

    string AllLog() => string.Join("\n", vm.Log);

    // ---- making a backup ----

    [Fact]
    public async Task A_backup_saves_the_whole_drive_and_says_so()
    {
        var disk = AddDisk(4, 24 * Mib);
        await Select(disk);
        dialogs.CreateBackup = new BackupRequest(P("drive.img"), true);

        await vm.CreateBackupCommand.ExecuteAsync();

        Assert.Equal(platform.DiskData[4].ReadBytes(0, (int)(24 * Mib)), File.ReadAllBytes(P("drive.img")));
        Assert.True(File.Exists(P("drive.img.sha256")));

        var (title, text) = Assert.Single(dialogs.Texts);
        Assert.Equal("Backup finished", title);
        Assert.Contains("read back and matches", text);
        Assert.Contains(P("drive.img.sha256").Substring(dir.Length + 1), text);
        Assert.Contains("Backed up", AllLog());

        Assert.Equal(disk, dialogs.BackupInfo!.Disk);
        Assert.StartsWith("Xbox 360 drive ", dialogs.BackupInfo.SuggestedName);
        Assert.EndsWith(".img", dialogs.BackupInfo.SuggestedName);
        Assert.Contains((4, false), platform.OpenCalls);
        Assert.DoesNotContain(platform.OpenCalls, c => c.Writable);
        Assert.False(vm.Busy);
        Assert.False(vm.CanCancel);
        Assert.Equal(1, platform.AwakeRequests);
        Assert.Equal(0, platform.AwakeHeld);
    }

    [Fact]
    public async Task Skipping_the_check_is_passed_on()
    {
        var disk = AddDisk(4, 8 * Mib);
        await Select(disk);
        dialogs.CreateBackup = new BackupRequest(P("quick.img"), false);
        await vm.CreateBackupCommand.ExecuteAsync();
        Assert.Contains("wasn't read back", Assert.Single(dialogs.Texts).Text);
    }

    [Fact]
    public async Task Declining_the_window_makes_no_backup()
    {
        var disk = AddDisk(4, 8 * Mib);
        await Select(disk);
        int opened = platform.OpenCalls.Count;
        dialogs.CreateBackup = null;

        await vm.CreateBackupCommand.ExecuteAsync();

        Assert.NotNull(dialogs.BackupInfo);
        Assert.Equal(opened, platform.OpenCalls.Count);   // the drive wasn't even opened
        Assert.Empty(dialogs.Texts);
        Assert.Empty(Directory.GetFiles(dir));
        Assert.Equal(0, platform.AwakeRequests);
    }

    [Fact]
    public async Task Create_backup_is_only_there_for_a_real_drive()
    {
        Assert.False(vm.CreateBackupCommand.CanExecute(null));

        var disk = AddDisk(4, 8 * Mib);
        await Select(disk);
        Assert.True(vm.CreateBackupCommand.CanExecute(null));

        File.WriteAllBytes(P("some.img"), new byte[4096]);
        await vm.LoadBackupAsync(P("some.img"));
        Assert.False(vm.CreateBackupCommand.CanExecute(null));
    }

    [Fact]
    public async Task Writable_mount_blocks_a_backup()
    {
        var disk = AddFormattedDisk(4, 6 * Gib);
        await Select(disk);
        vm.AllowWriting = true;
        await vm.MountCommand.ExecuteAsync();
        Assert.Equal(1, vm.MountedCount);

        dialogs.CreateBackup = new BackupRequest(P("nope.img"), true);
        await vm.CreateBackupCommand.ExecuteAsync();

        Assert.Contains("mounted with writing turned on", Assert.Single(dialogs.Errors));
        Assert.Null(dialogs.BackupInfo);
    }

    [Fact]
    public async Task A_partition_mounted_read_only_does_not_stand_in_the_way()
    {
        var disk = AddFormattedDisk(4, 6 * Gib);
        await Select(disk);
        await vm.MountCommand.ExecuteAsync();

        dialogs.CreateBackup = null;   // getting as far as the window is the proof, the drive isn't read
        await vm.CreateBackupCommand.ExecuteAsync();

        Assert.Empty(dialogs.Errors);
        Assert.NotNull(dialogs.BackupInfo);
    }

    [Fact]
    public async Task Stopping_a_backup_ends_it_and_deletes_what_was_written()
    {
        var disk = AddDisk(4, 48 * Mib);
        await Select(disk);
        platform.Wrap = (_, device) => new MischiefDevice(device)
        {
            AfterRead = offset =>
            {
                if (offset >= 8 * Mib)
                    vm.CancelCommand.Execute(null);
            },
        };
        dialogs.CreateBackup = new BackupRequest(P("stopped.img"), true);

        await vm.CreateBackupCommand.ExecuteAsync();

        Assert.False(File.Exists(P("stopped.img")));
        Assert.False(File.Exists(P("stopped.img.sha256")));
        Assert.Equal("Backup stopped.", vm.Status);
        Assert.Contains("stopped", AllLog());
        Assert.Empty(dialogs.Errors);
        Assert.Empty(dialogs.Confirmations);   // a backup stops without any fuss
        Assert.False(vm.Busy);
        Assert.False(vm.CanCancel);
        Assert.Equal(0, platform.AwakeHeld);
    }

    [Fact]
    public async Task The_cancel_button_only_works_while_something_is_running()
    {
        Assert.False(vm.CanCancel);
        Assert.False(vm.CancelCommand.CanExecute(null));

        var disk = AddDisk(4, 24 * Mib);
        await Select(disk);
        bool sawIt = false;
        platform.Wrap = (_, device) => new MischiefDevice(device)
        {
            AfterRead = offset =>
            {
                if (offset == 0)
                    sawIt = vm.CanCancel && vm.CancelCommand.CanExecute(null) && vm.Busy;
            },
        };
        dialogs.CreateBackup = new BackupRequest(P("watched.img"), false);
        await vm.CreateBackupCommand.ExecuteAsync();

        Assert.True(sawIt);
        Assert.False(vm.CanCancel);
    }

    [Fact]
    public async Task Backup_that_cant_be_written_is_reported()
    {
        var disk = AddDisk(4, 8 * Mib);
        await Select(disk);
        dialogs.CreateBackup = new BackupRequest(Path.Combine(dir, "no", "such", "folder", "x.img"), true);

        await vm.CreateBackupCommand.ExecuteAsync();

        Assert.Single(dialogs.Errors);
        Assert.False(vm.Busy);
        Assert.Equal(0, platform.AwakeHeld);
        Assert.True(vm.CreateBackupCommand.CanExecute(null));
    }

    // ---- restoring one ----

    string MakeBackup(string name, long size, int seed)
    {
        var drive = new MemoryBlockDevice(size);
        drive.Write(0, TestVolumes.RandomBytes(2000, seed));
        drive.Write(5 * Mib, TestVolumes.RandomBytes(300_000, seed + 1));
        drive.Write(size - 512, TestVolumes.RandomBytes(512, seed + 2));
        DriveBackup.Create(drive, P(name), new BackupOptions(), null, default);
        return P(name);
    }

    [Fact]
    public async Task Restoring_writes_the_backup_onto_the_drive_that_was_picked()
    {
        string backup = MakeBackup("clone.img", 24 * Mib, 1);
        var target = AddDisk(5, 32 * Mib);
        platform.DiskData[5].Write(0, Enumerable.Repeat((byte)0x77, (int)(32 * Mib)).ToArray());
        await vm.InitializeAsync();

        dialogs.Restore = new RestoreRequest(backup, target, true);
        await vm.RestoreBackupCommand.ExecuteAsync();

        Assert.Equal(File.ReadAllBytes(backup), platform.DiskData[5].ReadBytes(0, (int)(24 * Mib)));
        Assert.All(platform.DiskData[5].ReadBytes(24 * Mib, (int)(8 * Mib)), b => Assert.Equal(0x77, b));
        Assert.Contains((5, true), platform.OpenCalls);
        Assert.Contains("Restored clone.img", AllLog());
        Assert.Equal(1, platform.AwakeRequests);
        Assert.Equal(0, platform.AwakeHeld);
        Assert.False(vm.Busy);
        Assert.Empty(dialogs.Errors);
        Assert.Equal(5, dialogs.RestoreInfo!.Disks.Single().Number);
    }

    [Fact]
    public async Task Restoring_can_keep_the_drives_own_security_sector()
    {
        // the drive that was backed up, and another of the same size with a serial of its own
        var source = SecuritySectorTests.ConsoleDrive(24 * Mib, "SOURCE", 24 * Mib);
        source.Write(0x80000, TestVolumes.RandomBytes(3_000_000, 3));
        DriveBackup.Create(source, P("keep.img"), new BackupOptions(), null, default);
        var target = AddConsoleDisk(5, 24 * Mib, "TARGET", 24 * Mib, 7);
        var targetStart = platform.DiskData[5].ReadBytes(0, 0x80000);
        await vm.InitializeAsync();

        dialogs.Restore = new RestoreRequest(P("keep.img"), target, true, KeepTargetHeader: true);
        await vm.RestoreBackupCommand.ExecuteAsync();

        Assert.Equal(targetStart, platform.DiskData[5].ReadBytes(0, 0x80000));
        Assert.Equal(File.ReadAllBytes(P("keep.img"))[0x80000..], platform.DiskData[5].ReadBytes(0x80000, (int)(24 * Mib - 0x80000)));
        Assert.Contains("keeping the drive's own security sector", AllLog());
        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task Unticked_the_backups_security_sector_is_used()
    {
        var source = SecuritySectorTests.ConsoleDrive(24 * Mib, "SOURCE", 24 * Mib);
        DriveBackup.Create(source, P("plain.img"), new BackupOptions(), null, default);
        var target = AddConsoleDisk(5, 24 * Mib, "TARGET", 24 * Mib, 7);
        await vm.InitializeAsync();

        dialogs.Restore = new RestoreRequest(P("plain.img"), target, true);
        await vm.RestoreBackupCommand.ExecuteAsync();

        Assert.Equal(source.ReadBytes(0, 0x80000), platform.DiskData[5].ReadBytes(0, 0x80000));
        Assert.DoesNotContain("keeping", AllLog());
    }

    [Fact]
    public async Task Keep_is_refused_if_sizes_differ_at_the_last_moment()
    {
        var source = SecuritySectorTests.ConsoleDrive(24 * Mib, "SOURCE", 24 * Mib);
        DriveBackup.Create(source, P("disagree.img"), new BackupOptions(), null, default);
        var target = AddConsoleDisk(5, 24 * Mib, "TARGET", 16 * Mib, 7);   // the console sees it smaller than the drive the backup came from
        var before = platform.DiskData[5].ReadBytes(0, (int)(24 * Mib));
        await vm.InitializeAsync();

        dialogs.Restore = new RestoreRequest(P("disagree.img"), target, true, KeepTargetHeader: true);
        await vm.RestoreBackupCommand.ExecuteAsync();

        Assert.Contains("same size", Assert.Single(dialogs.Errors));
        Assert.Equal(before, platform.DiskData[5].ReadBytes(0, (int)(24 * Mib)));
        Assert.DoesNotContain("partial copy", AllLog());
        Assert.False(vm.Busy);
    }

    [Fact]
    public async Task Restore_window_gets_each_drives_security_sector()
    {
        AddConsoleDisk(0, 24 * Mib, "SYSTEM-DISK", 24 * Mib, 1, system: true);   // Windows runs from it, nobody looks inside
        AddConsoleDisk(2, 24 * Mib, "PC-DISK", 24 * Mib, 2, volumes: true);
        AddConsoleDisk(5, 24 * Mib, "TARGET", 24 * Mib, 3);
        AddDisk(6, 24 * Mib);   // nothing like a security sector on this one
        await vm.InitializeAsync();
        platform.OpenCalls.Clear();

        dialogs.Restore = null;
        await vm.RestoreBackupCommand.ExecuteAsync();

        var sectors = dialogs.RestoreInfo!.SecuritySectors!;
        Assert.Equal(new[] { 5 }, sectors.Keys);
        Assert.Equal("TARGET", sectors[5].Serial);
        Assert.Equal(24 * Mib, sectors[5].ClaimedLength);
        Assert.DoesNotContain(platform.OpenCalls, c => c.Disk is 0 or 2);
        Assert.DoesNotContain(platform.OpenCalls, c => c.Writable);
    }

    [Fact]
    public async Task Unreadable_drive_has_no_security_sector_entry()
    {
        AddDisk(5, 8 * Mib);
        await vm.InitializeAsync();
        platform.OpenDiskFailure = new IOException("the drive isn't ready");

        dialogs.Restore = null;
        await vm.RestoreBackupCommand.ExecuteAsync();

        Assert.NotNull(dialogs.RestoreInfo);
        Assert.Empty(dialogs.RestoreInfo!.SecuritySectors!);
        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task The_loaded_backup_is_offered_to_the_restore_window()
    {
        string backup = MakeBackup("offered.img", 8 * Mib, 2);
        await vm.LoadBackupAsync(backup);
        dialogs.Restore = null;

        await vm.RestoreBackupCommand.ExecuteAsync();

        Assert.Equal(Path.GetFullPath(backup), dialogs.RestoreInfo!.BackupPath);
    }

    [Fact]
    public async Task With_a_drive_selected_there_is_no_backup_to_suggest()
    {
        var disk = AddDisk(4, 8 * Mib);
        await Select(disk);
        dialogs.Restore = null;
        await vm.RestoreBackupCommand.ExecuteAsync();
        Assert.Null(dialogs.RestoreInfo!.BackupPath);
    }

    [Fact]
    public async Task Restore_window_hears_which_drives_are_in_use()
    {
        var disk = AddFormattedDisk(4, 6 * Gib);
        await Select(disk);
        await vm.MountCommand.ExecuteAsync();

        dialogs.Restore = null;
        await vm.RestoreBackupCommand.ExecuteAsync();
        Assert.Contains("disk:4", dialogs.RestoreInfo!.InUse);
    }

    [Fact]
    public async Task System_disk_restore_is_refused()
    {
        string backup = MakeBackup("sys.img", 8 * Mib, 3);
        var system = AddDisk(0, 16 * Mib, system: true);
        await vm.InitializeAsync();

        dialogs.Restore = new RestoreRequest(backup, system, true);
        await vm.RestoreBackupCommand.ExecuteAsync();

        Assert.Contains("never write", Assert.Single(dialogs.Errors));
        Assert.DoesNotContain(platform.OpenCalls, c => c.Writable);
        Assert.Equal(0, platform.AwakeRequests);
    }

    [Fact]
    public async Task Restoring_over_a_pc_disk_is_refused_too()
    {
        string backup = MakeBackup("pc.img", 8 * Mib, 4);
        var pc = AddDisk(2, 16 * Mib, volumes: true);
        await vm.InitializeAsync();

        dialogs.Restore = new RestoreRequest(backup, pc, true);
        await vm.RestoreBackupCommand.ExecuteAsync();

        Assert.Contains("drive letters", Assert.Single(dialogs.Errors));
        Assert.DoesNotContain(platform.OpenCalls, c => c.Writable);
    }

    [Fact]
    public async Task A_drive_with_partitions_mounted_cant_be_restored_to()
    {
        string backup = MakeBackup("busy.img", 8 * Mib, 5);
        var disk = AddFormattedDisk(4, 6 * Gib);
        await Select(disk);
        await vm.MountCommand.ExecuteAsync();

        dialogs.Restore = new RestoreRequest(backup, disk, true);
        await vm.RestoreBackupCommand.ExecuteAsync();

        Assert.Contains("Unmount", Assert.Single(dialogs.Errors));
        Assert.DoesNotContain(platform.OpenCalls, c => c.Writable);
    }

    [Fact]
    public async Task Damaged_backup_is_refused_drive_untouched()
    {
        string backup = MakeBackup("rot.img", 12 * Mib, 6);
        using (var f = File.OpenWrite(backup))
        {
            f.Position = 5 * Mib + 100;
            f.WriteByte(0xEE);
        }
        var target = AddDisk(5, 16 * Mib);
        var before = platform.DiskData[5].ReadBytes(0, (int)(16 * Mib));
        await vm.InitializeAsync();

        dialogs.Restore = new RestoreRequest(backup, target, true);
        await vm.RestoreBackupCommand.ExecuteAsync();

        Assert.Contains("damaged", Assert.Single(dialogs.Errors));
        Assert.Equal(before, platform.DiskData[5].ReadBytes(0, (int)(16 * Mib)));
        Assert.DoesNotContain("partial copy", AllLog());
    }

    [Fact]
    public async Task Stopping_a_restore_asks_first()
    {
        string backup = MakeBackup("half.img", 40 * Mib, 7);
        var target = AddDisk(5, 40 * Mib);
        platform.DiskData[5].Write(0, Enumerable.Repeat((byte)0x77, (int)(40 * Mib)).ToArray());
        await vm.InitializeAsync();
        platform.Wrap = (_, device) => new MischiefDevice(device)
        {
            AfterWrite = offset =>
            {
                if (offset >= 8 * Mib)
                    vm.CancelCommand.Execute(null);
            },
        };
        dialogs.Restore = new RestoreRequest(backup, target, true);

        await vm.RestoreBackupCommand.ExecuteAsync();

        Assert.Equal("Stop?", Assert.Single(dialogs.Confirmations));
        Assert.Equal("Restore stopped", Assert.Single(dialogs.Texts).Title);
        Assert.Contains("partial copy", AllLog());
        Assert.All(platform.DiskData[5].ReadBytes(30 * Mib, (int)Mib), b => Assert.Equal(0x77, b));
        Assert.False(vm.Busy);
        Assert.False(vm.CanCancel);
        Assert.Equal(0, platform.AwakeHeld);
    }

    [Fact]
    public async Task Saying_no_to_the_stop_question_lets_the_restore_finish()
    {
        string backup = MakeBackup("carry-on.img", 24 * Mib, 8);
        var target = AddDisk(5, 24 * Mib);
        await vm.InitializeAsync();
        dialogs.ConfirmAnswer = false;
        platform.Wrap = (_, device) => new MischiefDevice(device)
        {
            AfterWrite = offset =>
            {
                if (offset == 8 * Mib)
                    vm.CancelCommand.Execute(null);
            },
        };
        dialogs.Restore = new RestoreRequest(backup, target, false);

        await vm.RestoreBackupCommand.ExecuteAsync();

        Assert.Equal("Stop?", Assert.Single(dialogs.Confirmations));
        Assert.Empty(dialogs.Texts);
        Assert.Equal(File.ReadAllBytes(backup), platform.DiskData[5].ReadBytes(0, (int)(24 * Mib)));
    }

    [Fact]
    public async Task Restore_refreshes_the_list()
    {
        // a blank drive, selected and read, then a backup with a full layout goes onto it
        var drive = AddDisk(5, 6 * Gib);
        var image = new MemoryBlockDevice(6 * Gib);
        HddFormatter.Format(image, new[] { "compat", "content" }, new HddFormatOptions { ContentLabel = "Cloned" });
        string backup = P("layout.img");
        DriveBackup.Create(image, backup, new BackupOptions(Verify: false), null, default);

        await Select(drive);
        Assert.DoesNotContain(vm.Partitions, p => p.Label == "Cloned");

        dialogs.Restore = new RestoreRequest(backup, drive, false);
        await vm.RestoreBackupCommand.ExecuteAsync();
        await vm.LoadingTask;

        Assert.Contains(vm.Partitions, p => p.Label == "Cloned");
    }

    // ---- a drive that was swapped behind the list's back ----

    [Fact]
    public async Task Swapped_drive_refreshes_the_list()
    {
        var old = AddFormattedDisk(4, 6 * Gib);
        await Select(old);
        int listed = platform.ListCalls;

        // somebody puts a bigger drive in the adapter and doesn't press Refresh
        platform.Disks[0] = Info(4, 7 * Gib);
        var bigger = new MemoryBlockDevice(7 * Gib);
        HddFormatter.Format(bigger, new[] { "content" });
        platform.DiskData[4] = bigger;

        dialogs.Format = new FormatRequest(false, new[] { "content" }, 0, 0, null, FatxFlavor.Xbox360);
        await vm.FormatCommand.ExecuteAsync();

        string message = Assert.Single(dialogs.Errors);
        Assert.Contains("different drive now", message);
        Assert.Contains("refreshed", message);
        Assert.True(platform.ListCalls > listed);
        Assert.Equal(7 * Gib, vm.Sources.Single(s => s.Disk?.Number == 4).Disk!.Length);

        // and the next try just works
        await vm.LoadingTask;
        dialogs.Errors.Clear();
        await vm.FormatCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Contains("Formatted", AllLog());
    }

    [Fact]
    public async Task A_drive_that_was_pulled_out_is_reported_the_same_way()
    {
        var disk = AddFormattedDisk(4, 6 * Gib);
        await Select(disk);
        platform.Disks.Clear();

        dialogs.Format = new FormatRequest(false, new[] { "content" }, 0, 0, null, FatxFlavor.Xbox360);
        await vm.FormatCommand.ExecuteAsync();

        Assert.Contains("isn't connected any more", Assert.Single(dialogs.Errors));
        Assert.Empty(vm.Sources);
        Assert.False(vm.Busy);
    }

    [Fact]
    public async Task Swapped_drive_is_not_backed_up_by_mistake()
    {
        var disk = AddDisk(4, 16 * Mib);
        await Select(disk);
        platform.Disks[0] = Info(4, 32 * Mib);
        platform.DiskData[4] = new MemoryBlockDevice(32 * Mib);

        dialogs.CreateBackup = new BackupRequest(P("wrong.img"), true);
        await vm.CreateBackupCommand.ExecuteAsync();

        Assert.Contains("different drive now", Assert.Single(dialogs.Errors));
        Assert.False(File.Exists(P("wrong.img")));
    }

    [Fact]
    public async Task Selecting_a_stale_entry_repairs_the_list_by_itself()
    {
        var disk = AddDisk(4, 16 * Mib);
        await vm.InitializeAsync();
        platform.Disks[0] = Info(4, 32 * Mib);
        platform.DiskData[4] = new MemoryBlockDevice(32 * Mib);

        vm.SelectedSource = vm.Sources.Single();
        await vm.LoadingTask;
        await Eventually(() => vm.Sources.Single().Disk!.Length == 32 * Mib);

        Assert.Contains("different drive now", Assert.Single(dialogs.Errors));
    }

    // ---- plugging and unplugging ----

    [Fact]
    public async Task A_new_drive_shows_up_by_itself()
    {
        AddDisk(4, 8 * Mib);
        await vm.InitializeAsync();
        Assert.Single(vm.Sources);

        AddDisk(5, 8 * Mib);
        vm.DevicesChanged();

        await Eventually(() => vm.Sources.Count == 2);
        Assert.Contains("Found 2 disks", AllLog());
    }

    [Fact]
    public async Task A_drive_that_goes_away_leaves_the_list_by_itself()
    {
        AddDisk(4, 8 * Mib);
        AddDisk(5, 8 * Mib);
        await vm.InitializeAsync();
        platform.Disks.RemoveAt(0);

        vm.DevicesChanged();

        await Eventually(() => vm.Sources.Count == 1);
        Assert.Equal(5, vm.Sources[0].Disk!.Number);
    }

    [Fact]
    public async Task A_change_that_changes_nothing_does_not_disturb_the_list()
    {
        var disk = AddDisk(4, 8 * Mib);
        await Select(disk);
        var shown = vm.SelectedSource;
        int lines = vm.Log.Count;
        int calls = platform.ListCalls;

        vm.DevicesChanged();
        await Eventually(() => platform.ListCalls > calls);
        await Task.Delay(100);

        Assert.Same(shown, vm.SelectedSource);
        Assert.Equal(lines, vm.Log.Count);
        Assert.Equal(calls + 1, platform.ListCalls);
    }

    [Fact]
    public async Task A_burst_of_events_makes_one_refresh()
    {
        AddDisk(4, 8 * Mib);
        await vm.InitializeAsync();
        vm.DeviceChangeDelay = TimeSpan.FromMilliseconds(150);
        AddDisk(5, 8 * Mib);

        for (int i = 0; i < 6; i++)
            vm.DevicesChanged();

        await Eventually(() => vm.Sources.Count == 2);
        await Task.Delay(400);
        Assert.Equal(1, vm.Log.Count(l => l.Contains("Found 2 disks")));
    }

    [Fact]
    public async Task No_refresh_during_a_job()
    {
        platform.DriverVersion = null;
        var model = new MainViewModel(platform, dialogs) { DeviceChangeDelay = TimeSpan.FromMilliseconds(20) };
        try
        {
            AddDisk(4, 8 * Mib);
            await model.InitializeAsync();
            var gate = new TaskCompletionSource();
            platform.InstallGate = gate.Task;
            var running = model.InstallDriverCommand.ExecuteAsync();
            await Eventually(() => platform.InstallCalls == 1);

            AddDisk(5, 8 * Mib);
            int calls = platform.ListCalls;
            model.DevicesChanged();
            await Task.Delay(500);
            Assert.Equal(calls, platform.ListCalls);
            Assert.Single(model.Sources);

            gate.SetResult();
            await running;
            await Eventually(() => model.Sources.Count == 2);
        }
        finally
        {
            model.Dispose();
        }
    }

    [Fact]
    public async Task No_refresh_while_a_window_is_open()
    {
        var disk = AddFormattedDisk(4, 6 * Gib);
        await Select(disk);
        int duringTheWindow = -1, beforeTheWindow = -1;
        dialogs.Format = new FormatRequest(false, new[] { "content" }, 0, 0, null, FatxFlavor.Xbox360);
        dialogs.WhileFormatWindowIsOpen = () =>
        {
            beforeTheWindow = platform.ListCalls;
            AddDisk(5, 8 * Mib);
            vm.DevicesChanged();
            Thread.Sleep(600);
            duringTheWindow = platform.ListCalls;
        };

        await vm.FormatCommand.ExecuteAsync();

        Assert.Equal(beforeTheWindow, duringTheWindow);
        await Eventually(() => vm.Sources.Count == 2);
    }
}

public class BackupDialogTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "fx-bkdlg-" + Guid.NewGuid().ToString("N"));

    public BackupDialogTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, true);

    static BackupDialogInfo Info(long size = 120034123776) =>
        new(new PhysicalDiskInfo(4, size, 512, "SABRENT", "S", "USB", false, false, false), "Xbox 360 drive 111.8GB 2026-10-10.img");

    [Fact]
    public void It_starts_with_a_sensible_place_and_name_and_checking_on()
    {
        var model = new BackupDialogViewModel(Info(), dir);
        Assert.Equal(Path.Combine(dir, "Xbox 360 drive 111.8GB 2026-10-10.img"), model.FilePath);
        Assert.True(model.Verify);
        Assert.True(model.CanCreate);
        Assert.Null(model.Problem);
        Assert.Contains("SABRENT", model.Heading);
    }

    [Fact]
    public void Taken_name_or_missing_folder_disables_the_button()
    {
        var model = new BackupDialogViewModel(Info(), dir);
        File.WriteAllText(Path.Combine(dir, "taken.img"), "x");

        model.FilePath = Path.Combine(dir, "taken.img");
        Assert.False(model.CanCreate);
        Assert.NotNull(model.Problem);

        model.FilePath = Path.Combine(dir, "no", "folder", "x.img");
        Assert.False(model.CanCreate);

        model.FilePath = "   ";
        Assert.Equal("Say where to save it.", model.Problem);
        Assert.False(model.CanCreate);
        Assert.Null(model.BuildRequest());

        model.FilePath = Path.Combine(dir, "fine.img");
        Assert.True(model.CanCreate);
    }

    [Fact]
    public void The_request_carries_the_full_path_and_the_checking_choice()
    {
        var model = new BackupDialogViewModel(Info(), dir) { Verify = false };
        model.FilePath = Path.Combine(dir, "x.img");
        var request = model.BuildRequest()!;
        Assert.Equal(Path.Combine(dir, "x.img"), request.Path);
        Assert.False(request.Verify);
    }

    [Fact]
    public void Dialog_shows_the_size_and_the_sparse_note()
    {
        var model = new BackupDialogViewModel(Info(), dir);
        Assert.Contains("111.8 GB", model.SizeText);
        Assert.True(model.SizeText.Contains("less room") || model.SizeText.Contains("takes all of that"));
        Assert.Contains("free", model.SpaceText);
    }
}

public class RestoreDialogTests : IDisposable
{
    const long Gib = 1L << 30;

    readonly string dir = Path.Combine(Path.GetTempPath(), "fx-rsdlg-" + Guid.NewGuid().ToString("N"));

    public RestoreDialogTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, true);

    // sparse, so the backup of a 120 GB drive doesn't take 120 GB while the test runs
    string Backup(string name, long length)
    {
        string path = Path.Combine(dir, name);
        using var f = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        SparseFile.TryMark(f.SafeFileHandle);
        f.SetLength(length);
        return path;
    }

    static readonly PhysicalDiskInfo System = new(0, 953 * Gib, 512, "Samsung SSD", "S1", "NVMe", false, true, true);
    static readonly PhysicalDiskInfo Pc = new(2, 465 * Gib, 512, "Seagate", "S2", "USB", true, true, false);
    static readonly PhysicalDiskInfo Good = new(4, 120034123776, 512, "SABRENT", "S3", "USB", false, false, false);
    static readonly PhysicalDiskInfo Small = new(5, 8 * Gib, 512, "Stick", "S4", "USB", true, false, false);

    RestoreDialogViewModel Make(string? backup, params string[] inUse) =>
        new(new RestoreDialogInfo(new[] { System, Pc, Good, Small }, backup, new HashSet<string>(inUse)));

    RestoreDialogViewModel MakeKnowing(string? backup, Dictionary<int, SecuritySector> sectors) =>
        new(new RestoreDialogInfo(new[] { System, Pc, Good, Small }, backup, new HashSet<string>(), sectors));

    [Fact]
    public void Risky_drives_are_listed_but_cant_be_picked_and_say_why()
    {
        var model = Make(Backup("b.img", 60 * Gib));

        Assert.Contains("Windows is running", model.Targets.Single(t => t.Disk.Number == 0).Problem);
        Assert.Contains("drive letters", model.Targets.Single(t => t.Disk.Number == 2).Problem);
        Assert.Contains("Too small", model.Targets.Single(t => t.Disk.Number == 5).Problem);
        Assert.Null(model.Targets.Single(t => t.Disk.Number == 4).Problem);
        Assert.Equal(new[] { 4 }, model.Targets.Where(t => t.Enabled).Select(t => t.Disk.Number));

        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 0);
        Assert.Null(model.SelectedTarget);
        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 5);
        Assert.Null(model.SelectedTarget);
    }

    [Fact]
    public void Usable_drives_come_first_and_the_greyed_out_ones_follow()
    {
        var model = Make(Backup("order.img", 1 * Gib));
        // by number would be 0 (system), 2 (PC), 4, 5
        Assert.Equal(new[] { 4, 5, 0, 2 }, model.Targets.Select(t => t.Disk.Number));
    }

    [Fact]
    public void Mounted_drive_has_to_be_unmounted_first()
    {
        var model = Make(Backup("b.img", 1 * Gib), "disk:4");
        Assert.Contains("unmount", model.Targets.Single(t => t.Disk.Number == 4).Problem);
    }

    [Fact]
    public void Restore_needs_backup_drive_and_typed_number()
    {
        var model = Make(Backup("b.img", 1 * Gib));
        Assert.False(model.CanRestore);
        Assert.Equal("the disk number", model.ConfirmationPhrase);

        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);
        Assert.Equal("disk 4", model.ConfirmationPhrase);
        Assert.False(model.CanRestore);

        model.ConfirmText = "disk 5";
        Assert.False(model.CanRestore);
        model.ConfirmText = " Disk 4 ";
        Assert.True(model.CanRestore);

        var request = model.BuildRequest()!;
        Assert.Equal(4, request.Target.Number);
        Assert.Equal(Path.Combine(dir, "b.img"), request.BackupPath);
        Assert.True(request.Verify);
    }

    [Fact]
    public void The_warning_names_the_drive_that_will_be_overwritten()
    {
        var model = Make(Backup("b.img", 1 * Gib));
        Assert.Contains("the drive you pick", model.Warning);
        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);
        Assert.Contains("SABRENT", model.Warning);
        Assert.Contains("can't be undone", model.Warning);
    }

    [Fact]
    public void Without_a_backup_there_is_nothing_to_restore()
    {
        var none = Make(null);
        Assert.Equal("Pick the backup file.", none.BackupProblem);

        var missing = Make(Path.Combine(dir, "not-there.img"));
        Assert.Equal("That file doesn't exist.", missing.BackupProblem);

        var empty = Make(Backup("empty.img", 0));
        Assert.Equal("That file is empty.", empty.BackupProblem);

        none.SelectedTarget = none.Targets.Single(t => t.Disk.Number == 4);
        none.ConfirmText = "disk 4";
        Assert.False(none.CanRestore);
        Assert.Null(none.BuildRequest());
    }

    [Fact]
    public void New_backup_rechecks_drive_sizes()
    {
        var model = Make(Backup("small.img", 1 * Gib));
        Assert.True(model.Targets.Single(t => t.Disk.Number == 5).Enabled);

        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 5);
        model.ConfirmText = "disk 5";
        Assert.True(model.CanRestore);

        model.BackupPath = Backup("big.img", 60 * Gib);
        Assert.False(model.Targets.Single(t => t.Disk.Number == 5).Enabled);
        Assert.Null(model.SelectedTarget);
        Assert.False(model.CanRestore);

        model.BackupPath = Path.Combine(dir, "small.img");
        Assert.True(model.Targets.Single(t => t.Disk.Number == 5).Enabled);
    }

    [Fact]
    public void A_pick_that_still_fits_survives_changing_the_backup()
    {
        var model = Make(Backup("a.img", 1 * Gib));
        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);
        model.BackupPath = Backup("b.img", 2 * Gib);
        Assert.Equal(4, model.SelectedTarget!.Disk.Number);
    }

    [Fact]
    public void The_note_says_whether_the_backup_can_be_checked_for_damage()
    {
        string path = Backup("noted.img", 5 * Gib);
        var model = Make(path);
        Assert.Contains("5 GB", model.BackupNote);
        Assert.Contains("no checksum file", model.BackupNote);

        File.WriteAllText(path + ".sha256", new string('a', 64) + "  noted.img\n");
        var again = Make(path);
        Assert.Contains("checked for damage before anything is written", again.BackupNote);

        Assert.Equal("", Make(null).BackupNote);
    }

    [Fact]
    public void Same_size_drive_can_keep_its_header()
    {
        var sectors = new Dictionary<int, SecuritySector> { [4] = new("DRIVE-4", Good.Length) };
        var model = MakeKnowing(Backup("same.img", Good.Length), sectors);

        Assert.False(model.CanKeepHeader);
        Assert.Contains("Pick a drive", model.KeepHeaderNote);

        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);
        Assert.True(model.CanKeepHeader);
        Assert.Contains("DRIVE-4", model.KeepHeaderNote);

        model.KeepTargetHeader = true;
        Assert.True(model.KeepTargetHeader);
        Assert.Contains("except its own security sector", model.Warning);

        model.ConfirmText = "disk 4";
        Assert.True(model.BuildRequest()!.KeepTargetHeader);
    }

    [Fact]
    public void A_drive_the_console_sees_at_another_size_cant_keep_its_own()
    {
        var sectors = new Dictionary<int, SecuritySector> { [4] = new("DRIVE-4", Good.Length / 2) };
        var model = MakeKnowing(Backup("big.img", Good.Length), sectors);
        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);

        Assert.False(model.CanKeepHeader);
        Assert.Contains("same size", model.KeepHeaderNote);
        model.KeepTargetHeader = true;
        Assert.False(model.KeepTargetHeader);
    }

    [Fact]
    public void A_drive_with_no_security_sector_has_nothing_to_keep()
    {
        var model = MakeKnowing(Backup("b.img", 1 * Gib), new Dictionary<int, SecuritySector>());
        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);

        Assert.False(model.CanKeepHeader);
        Assert.Contains("nothing to keep", model.KeepHeaderNote);
        model.ConfirmText = "disk 4";
        Assert.False(model.BuildRequest()!.KeepTargetHeader);
    }

    [Fact]
    public void Without_any_word_on_the_drives_nothing_can_be_kept()
    {
        var model = Make(Backup("b.img", 1 * Gib));
        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);
        Assert.False(model.CanKeepHeader);
    }

    [Fact]
    public void Tick_clears_when_the_pick_cant_keep_its_own()
    {
        long size = Small.Length;
        var sectors = new Dictionary<int, SecuritySector> { [5] = new("STICK", size) };
        var model = MakeKnowing(Backup("eight.img", size), sectors);

        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 5);
        model.KeepTargetHeader = true;
        Assert.True(model.KeepTargetHeader);

        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);   // big enough, but no security sector
        Assert.False(model.KeepTargetHeader);
        Assert.Contains("Everything on", model.Warning);
        Assert.DoesNotContain("except its own", model.Warning);
    }

    [Fact]
    public void Tick_clears_when_the_backup_size_changes()
    {
        var sectors = new Dictionary<int, SecuritySector> { [4] = new("DRIVE-4", Good.Length) };
        var model = MakeKnowing(Backup("fits.img", Good.Length), sectors);
        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);
        model.KeepTargetHeader = true;
        Assert.True(model.KeepTargetHeader);

        model.BackupPath = Backup("smaller.img", 8 * Gib);
        Assert.False(model.KeepTargetHeader);
        Assert.False(model.CanKeepHeader);
    }

    [Fact]
    public void A_backup_that_cant_be_used_leaves_the_note_empty()
    {
        var model = MakeKnowing(Path.Combine(dir, "missing.img"), new Dictionary<int, SecuritySector> { [4] = new("X", 1) });
        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);
        Assert.Equal("", model.KeepHeaderNote);
        Assert.False(model.CanKeepHeader);
    }

    [Fact]
    public void Checking_afterwards_is_on_unless_turned_off()
    {
        var model = Make(Backup("v.img", Gib));
        model.SelectedTarget = model.Targets.Single(t => t.Disk.Number == 4);
        model.ConfirmText = "disk 4";
        Assert.True(model.BuildRequest()!.Verify);
        model.Verify = false;
        Assert.False(model.BuildRequest()!.Verify);
    }
}
