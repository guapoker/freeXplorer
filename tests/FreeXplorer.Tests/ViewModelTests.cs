using FreeXplorer.App.Services;
using FreeXplorer.App.ViewModels;
using FreeXplorer.Fatx;
using FreeXplorer.Mount;
using FreeXplorer.Storage;
using FreeXplorer.Xbox360;

namespace FreeXplorer.Tests;

sealed class FakeMounted : IMountedVolume
{
    public FakeMounted(FatxVolume volume, string mountPoint)
    {
        Volume = volume;
        MountPoint = mountPoint;
    }

    public FatxVolume Volume { get; }
    public string MountPoint { get; }
    public bool ReadOnly => Volume.IsReadOnly;
    public bool WasUnmounted { get; private set; }

    public void Unmount()
    {
        WasUnmounted = true;
        Volume.Flush();
    }

    public void Dispose() => Unmount();
}

sealed class FakePlatform : IPlatform
{
    readonly char[] letters = "XYZ".ToCharArray();
    public string? DriverVersion { get; set; } = "2.1.25156";
    public List<PhysicalDiskInfo> Disks { get; } = new();
    public Dictionary<int, MemoryBlockDevice> DiskData { get; } = new();
    public List<FakeMounted> Mounts { get; } = new();
    public List<string> OpenedFolders { get; } = new();
    public List<(int Disk, bool Writable)> OpenCalls { get; } = new();
    public Exception? OpenDiskFailure { get; set; }
    public int ListCalls { get; private set; }

    // lets a test put something between a job and the drive it reads or writes
    public Func<int, IBlockDevice, IBlockDevice>? Wrap { get; set; }

    // power requests: how many were made and how many are still held
    public int AwakeRequests { get; private set; }
    public int AwakeHeld { get; private set; }

    // what installing the driver does: nothing, fail, or wait for the test to say go
    public int InstallCalls { get; private set; }
    public Exception? InstallFailure { get; set; }
    public DriverInstallResult InstallResult { get; set; } = DriverInstallResult.Installed;
    public Task? InstallGate { get; set; }

    public async Task<DriverInstallResult> InstallDriverAsync(Action<string>? status, Action<double>? progress)
    {
        InstallCalls++;
        status?.Invoke("Downloading WinFsp...");
        progress?.Invoke(0.5);
        if (InstallGate is not null)
            await InstallGate;
        if (InstallFailure is not null)
            throw InstallFailure;
        DriverVersion = "2.1.25156";
        return InstallResult;
    }

    public IReadOnlyList<PhysicalDiskInfo> ListDisks()
    {
        ListCalls++;
        return Disks.ToList();
    }

    public IBlockDevice OpenDisk(PhysicalDiskInfo disk, bool writable)
    {
        OpenCalls.Add((disk.Number, writable));
        if (OpenDiskFailure is not null)
            throw OpenDiskFailure;

        // the same look the real thing takes: the drive on that number has to be the one that was listed
        var now = Disks.FirstOrDefault(d => d.Number == disk.Number)
            ?? throw new DiskChangedException($"Disk {disk.Number} isn't connected any more.");
        if (now.Length != disk.Length)
            throw new DiskChangedException($"Disk {disk.Number} is a different drive now. It was {Size.Format(disk.Length)} when the list was loaded and it is {Size.Format(now.Length)} now.");

        var data = DiskData[disk.Number];
        IBlockDevice device = new SliceDevice(data, 0, data.Length, writable);
        return Wrap?.Invoke(disk.Number, device) ?? device;
    }

    public IMountedVolume Mount(FatxVolume volume, string label)
    {
        var mounted = new FakeMounted(volume, $"{letters[Mounts.Count % letters.Length]}:");
        Mounts.Add(mounted);
        return mounted;
    }

    public void OpenFolder(string path) => OpenedFolders.Add(path);

    public IDisposable KeepAwake(string reason)
    {
        AwakeRequests++;
        AwakeHeld++;
        return new Release(this);
    }

    sealed class Release : IDisposable
    {
        FakePlatform? owner;
        public Release(FakePlatform owner) => this.owner = owner;

        public void Dispose()
        {
            if (owner is null)
                return;
            owner.AwakeHeld--;
            owner = null;
        }
    }
}

sealed class FakeDialogs : IDialogs
{
    public string? BackupToLoad { get; set; }
    public BackupRequest? CreateBackup { get; set; }
    public RestoreRequest? Restore { get; set; }
    public FormatRequest? Format { get; set; }
    public bool ConfirmAnswer { get; set; } = true;
    public List<string> Errors { get; } = new();
    public List<(string Title, string Text)> Texts { get; } = new();
    public List<string> Confirmations { get; } = new();
    public FormatDialogInfo? FormatInfo { get; private set; }
    public BackupDialogInfo? BackupInfo { get; private set; }
    public RestoreDialogInfo? RestoreInfo { get; private set; }

    // runs while a window is "open", for tests about what happens behind one
    public Action? WhileFormatWindowIsOpen { get; set; }

    public string? PickBackupToLoad() => BackupToLoad;

    public BackupRequest? AskCreateBackup(BackupDialogInfo info)
    {
        BackupInfo = info;
        return CreateBackup;
    }

    public RestoreRequest? AskRestore(RestoreDialogInfo info)
    {
        RestoreInfo = info;
        return Restore;
    }

    public FormatRequest? AskFormat(FormatDialogInfo info)
    {
        FormatInfo = info;
        WhileFormatWindowIsOpen?.Invoke();
        return Format;
    }

    public bool Confirm(string title, string message)
    {
        Confirmations.Add(title);
        return ConfirmAnswer;
    }

    public void ShowText(string title, string text) => Texts.Add((title, text));
    public void ShowError(string message) => Errors.Add(message);
}

public class ViewModelTests : IDisposable
{
    const long Gib = 1L << 30;

    readonly string dir = Path.Combine(Path.GetTempPath(), "fx-vm-" + Guid.NewGuid().ToString("N"));
    readonly FakePlatform platform = new();
    readonly FakeDialogs dialogs = new();
    readonly MainViewModel vm;
    readonly List<MainViewModel> extra = new();

    public ViewModelTests()
    {
        Directory.CreateDirectory(dir);
        vm = new MainViewModel(platform, dialogs);
    }

    public void Dispose()
    {
        vm.Dispose();
        foreach (var model in extra)
            model.Dispose();
        Directory.Delete(dir, true);
    }

    // a window that starts up on a PC without WinFsp
    MainViewModel StartWithoutDriver()
    {
        platform.DriverVersion = null;
        var model = new MainViewModel(platform, dialogs);
        extra.Add(model);
        return model;
    }

    string MakeImage(string name = "drive.img", long size = 6 * Gib)
    {
        string path = Path.Combine(dir, name);
        using var dev = FileBlockDevice.CreateImage(path, size);
        HddFormatter.Format(dev, new[] { "sysext", "sysext2", "compat", "content" });
        return path;
    }

    static PhysicalDiskInfo Disk(int n, bool system = false, bool volumes = false, long size = 6 * Gib) =>
        new(n, size, 512, $"Test Drive {n}", $"SER{n}", "USB", true, volumes, system);

    [Fact]
    public async Task Disks_are_listed_with_notes_on_the_risky_ones()
    {
        platform.Disks.Add(Disk(0, system: true));
        platform.Disks.Add(Disk(1, volumes: true));
        platform.Disks.Add(Disk(2));
        await vm.InitializeAsync();

        Assert.Equal(3, vm.Sources.Count);
        Assert.Equal("Windows is running from this disk", vm.Sources[0].Note);
        Assert.Contains("drive letters", vm.Sources[1].Note);
        Assert.Null(vm.Sources[2].Note);
        Assert.Contains("Found 3 disks", string.Join("\n", vm.Log));
    }

    [Fact]
    public async Task Refresh_keeps_images_and_picks_up_new_disks()
    {
        await vm.InitializeAsync();
        await vm.LoadBackupAsync(MakeImage());
        platform.Disks.Add(Disk(5));
        await vm.RefreshAsync();

        Assert.Equal(2, vm.Sources.Count);
        Assert.Equal(SourceKind.Disk, vm.Sources[0].Kind);
        Assert.Equal(SourceKind.Image, vm.Sources[1].Kind);
    }

    [Fact]
    public async Task Opening_an_image_lists_its_partitions()
    {
        await vm.LoadBackupAsync(MakeImage());

        Assert.Equal(6, vm.Partitions.Count);
        var sysext = vm.Partitions.Single(p => p.Id == "sysext");
        Assert.True(sysext.CanMount);
        Assert.Equal("XTAF, 16 KB clusters, FAT16", sysext.Format);
        Assert.Equal("Ready to mount", sysext.Status);
        var cache = vm.Partitions.Single(p => p.Id == "syscache");
        Assert.False(cache.CanMount);
        Assert.Contains("Cache", cache.Status);
        Assert.Equal("content", vm.SelectedPartition!.Id);
    }

    [Fact]
    public async Task A_blank_image_says_so()
    {
        string path = Path.Combine(dir, "blank.img");
        using (var f = File.Create(path))
            f.SetLength(7 * Gib);
        await vm.LoadBackupAsync(path);
        Assert.Contains("no Xbox 360 partitions", vm.Status);
        Assert.All(vm.Partitions, p => Assert.False(p.CanMount));
        Assert.True(vm.FormatCommand.CanExecute(null));
    }

    [Fact]
    public async Task Mounting_is_read_only_unless_asked_otherwise()
    {
        await vm.LoadBackupAsync(MakeImage());
        await vm.MountCommand.ExecuteAsync();

        var mount = Assert.Single(platform.Mounts);
        Assert.True(mount.ReadOnly);
        Assert.Equal("X:", vm.SelectedPartition!.MountPoint);
        Assert.Equal("Mounted as X: (read-only)", vm.SelectedPartition.Status);
        Assert.Equal("Mounted as X: (read-only)", vm.SelectedPartition.StatusDetail);
        Assert.Equal(new[] { "X:\\" }, platform.OpenedFolders);
        Assert.Empty(dialogs.Confirmations);
        Assert.False(vm.MountCommand.CanExecute(null));
        Assert.True(vm.UnmountCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_folder_mount_point_gets_the_long_form_in_the_tooltip()
    {
        await vm.LoadBackupAsync(MakeImage());
        var part = vm.SelectedPartition!;
        part.MountedReadOnly = true;
        part.MountPoint = @"C:\mnt\content";
        Assert.Equal("Mounted in a folder (read-only)", part.Status);
        Assert.Equal(@"Mounted in a folder (read-only): C:\mnt\content", part.StatusDetail);
    }

    [Fact]
    public async Task Unmount_releases_everything()
    {
        await vm.LoadBackupAsync(MakeImage());
        await vm.MountCommand.ExecuteAsync();
        await vm.UnmountCommand.ExecuteAsync();

        Assert.True(platform.Mounts[0].WasUnmounted);
        Assert.Null(vm.SelectedPartition!.MountPoint);
        Assert.Equal(0, vm.MountedCount);

        // the image file can be written to again, so nothing was left holding it open
        string path = vm.SelectedSource!.ImagePath!;
        using var dev = new FileBlockDevice(path, writable: true);
        dev.Write(0, new byte[16]);
    }

    [Fact]
    public async Task Partitions_are_mounted_one_at_a_time_each_on_its_own_drive()
    {
        await vm.LoadBackupAsync(MakeImage());
        await vm.MountCommand.ExecuteAsync();

        vm.SelectedPartition = vm.Partitions.Single(p => p.Id == "compat");
        await vm.MountCommand.ExecuteAsync();

        Assert.Equal(2, platform.Mounts.Count);
        Assert.Equal(2, vm.MountedCount);
        Assert.Equal(new[] { "X:", "Y:" }, vm.Partitions.Where(p => p.IsMounted).Select(p => p.MountPoint).OrderBy(m => m));
        Assert.Equal(2, platform.OpenedFolders.Count);
    }

    [Fact]
    public async Task Writing_needs_a_yes_and_actually_writes()
    {
        string path = MakeImage();
        await vm.LoadBackupAsync(path);
        vm.AllowWriting = true;

        dialogs.ConfirmAnswer = false;
        await vm.MountCommand.ExecuteAsync();
        Assert.Empty(platform.Mounts);
        Assert.Single(dialogs.Confirmations);

        dialogs.ConfirmAnswer = true;
        await vm.MountCommand.ExecuteAsync();
        var mount = Assert.Single(platform.Mounts);
        Assert.False(mount.ReadOnly);

        var file = mount.Volume.CreateFile(mount.Volume.Root, "hello.txt");
        mount.Volume.Write(file, 0, new byte[] { 1, 2, 3 });
        await vm.UnmountCommand.ExecuteAsync();

        using var dev = new FileBlockDevice(path, false);
        var content = DriveScanner.Scan(dev).Partitions.Single(p => p.Id == "content");
        using var check = DriveScanner.OpenPartition(dev, content, false);
        Assert.Equal(3, check.Resolve("hello.txt").Size);
    }

    [Fact]
    public async Task The_system_disk_cant_be_mounted_for_writing()
    {
        platform.Disks.Add(Disk(0, system: true, size: 6 * Gib));
        var data = new MemoryBlockDevice(6 * Gib);
        HddFormatter.Format(data, new[] { "content" });
        platform.DiskData[0] = data;
        await vm.InitializeAsync();
        vm.SelectedSource = vm.Sources[0];
        await vm.LoadingTask;

        vm.AllowWriting = true;
        await vm.MountCommand.ExecuteAsync();

        Assert.Empty(platform.Mounts);
        Assert.Contains("never write", Assert.Single(dialogs.Errors));
        Assert.DoesNotContain(platform.OpenCalls, c => c.Writable);
    }

    [Fact]
    public async Task Reading_a_disk_never_opens_it_for_writing()
    {
        platform.Disks.Add(Disk(3));
        var data = new MemoryBlockDevice(6 * Gib);
        HddFormatter.Format(data, new[] { "content" });
        platform.DiskData[3] = data;
        await vm.InitializeAsync();
        vm.SelectedSource = vm.Sources[0];
        await vm.LoadingTask;
        await vm.MountCommand.ExecuteAsync();
        await vm.CheckCommand.ExecuteAsync();

        Assert.NotEmpty(platform.OpenCalls);
        Assert.All(platform.OpenCalls, c => Assert.False(c.Writable));
    }

    [Fact]
    public async Task Missing_winfsp_is_explained_not_attempted()
    {
        platform.DriverVersion = null;
        await vm.LoadBackupAsync(MakeImage());
        await vm.MountCommand.ExecuteAsync();
        Assert.Empty(platform.Mounts);
        Assert.Contains("Install driver", Assert.Single(dialogs.Errors));
    }

    [Fact]
    public async Task A_disk_windows_refuses_to_open_says_why()
    {
        platform.OpenDiskFailure = new UnauthorizedAccessException("Windows won't let this program open disk 3. Run it as administrator.");
        platform.Disks.Add(Disk(3));
        platform.DiskData[3] = new MemoryBlockDevice(6 * Gib);
        await vm.InitializeAsync();

        vm.SelectedSource = vm.Sources[0];
        await vm.LoadingTask;
        Assert.Contains("administrator", vm.Status);
        Assert.Empty(vm.Partitions);
    }

    [Fact]
    public void The_driver_button_shows_what_is_installed()
    {
        Assert.Equal(DriverState.Installed, vm.DriverState);
        Assert.Equal("Driver installed", vm.DriverText);
        Assert.Contains("2.1.25156", vm.DriverDetail);
        Assert.False(vm.InstallDriverCommand.CanExecute(null));
    }

    [Fact]
    public void Without_the_driver_the_button_offers_to_install_it()
    {
        var model = StartWithoutDriver();
        Assert.Equal(DriverState.Missing, model.DriverState);
        Assert.Equal("Install driver", model.DriverText);
        Assert.Contains("isn't installed", model.DriverDetail);
        Assert.True(model.InstallDriverCommand.CanExecute(null));
    }

    [Fact]
    public async Task Install_driver_asks_then_installs()
    {
        var model = StartWithoutDriver();
        var changed = new List<string>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        await model.InstallDriverCommand.ExecuteAsync();

        Assert.Equal("Install the WinFsp driver?", Assert.Single(dialogs.Confirmations));
        Assert.Equal(1, platform.InstallCalls);
        Assert.Equal(DriverState.Installed, model.DriverState);
        Assert.Equal("Driver installed", model.DriverText);
        Assert.False(model.InstallDriverCommand.CanExecute(null));
        Assert.Contains(nameof(MainViewModel.DriverState), changed);
        Assert.Contains(nameof(MainViewModel.DriverText), changed);
        Assert.Contains("Installed WinFsp 2.1.25156", string.Join("\n", model.Log));
        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task Saying_no_to_the_question_installs_nothing()
    {
        var model = StartWithoutDriver();
        dialogs.ConfirmAnswer = false;
        await model.InstallDriverCommand.ExecuteAsync();
        Assert.Equal(0, platform.InstallCalls);
        Assert.Single(dialogs.Confirmations);
        Assert.Equal(DriverState.Missing, model.DriverState);
    }

    [Fact]
    public async Task A_failed_install_is_reported_and_can_be_tried_again()
    {
        var model = StartWithoutDriver();
        platform.InstallFailure = new DriverInstallException("Couldn't download WinFsp (no internet).");
        await model.InstallDriverCommand.ExecuteAsync();

        Assert.Equal("Couldn't download WinFsp (no internet).", Assert.Single(dialogs.Errors));
        Assert.Equal(DriverState.Missing, model.DriverState);
        Assert.True(model.InstallDriverCommand.CanExecute(null));
        Assert.Contains("no internet", string.Join("\n", model.Log));

        platform.InstallFailure = null;
        await model.InstallDriverCommand.ExecuteAsync();
        Assert.Equal(DriverState.Installed, model.DriverState);
    }

    [Fact]
    public async Task When_windows_wants_a_restart_the_button_says_so()
    {
        var model = StartWithoutDriver();
        platform.InstallResult = DriverInstallResult.NeedsRestart;
        await model.InstallDriverCommand.ExecuteAsync();

        Assert.Equal(DriverState.RestartNeeded, model.DriverState);
        Assert.Equal("Restart Windows", model.DriverText);
        Assert.False(model.InstallDriverCommand.CanExecute(null));
        Assert.Contains("Restart", Assert.Single(dialogs.Texts).Title);

        await model.LoadBackupAsync(MakeImage());
        await model.MountCommand.ExecuteAsync();
        Assert.Empty(platform.Mounts);
        Assert.Contains("restart", Assert.Single(dialogs.Errors));
    }

    [Fact]
    public async Task Install_driver_shows_progress()
    {
        var model = StartWithoutDriver();
        var gate = new TaskCompletionSource();
        platform.InstallGate = gate.Task;

        var running = model.InstallDriverCommand.ExecuteAsync();
        while (platform.InstallCalls == 0)
            await Task.Delay(5);

        Assert.True(model.Busy);
        Assert.Equal(DriverState.Installing, model.DriverState);
        Assert.Equal("Installing driver...", model.DriverText);
        Assert.False(model.RefreshCommand.CanExecute(null));
        Assert.False(model.InstallDriverCommand.CanExecute(null));

        gate.SetResult();
        await running;
        Assert.False(model.Busy);
        Assert.Equal(DriverState.Installed, model.DriverState);
    }

    [Fact]
    public async Task Refresh_notices_a_driver_that_was_installed_some_other_way()
    {
        var model = StartWithoutDriver();
        Assert.Equal(DriverState.Missing, model.DriverState);

        platform.DriverVersion = "2.1.25156";
        await model.RefreshAsync();
        Assert.Equal(DriverState.Installed, model.DriverState);
    }

    [Fact]
    public async Task Mount_notices_a_driver_installed_elsewhere()
    {
        var model = StartWithoutDriver();
        await model.LoadBackupAsync(MakeImage());
        platform.DriverVersion = "2.1.25156";
        await model.MountCommand.ExecuteAsync();
        Assert.Single(platform.Mounts);
        Assert.Equal(DriverState.Installed, model.DriverState);
    }

    [Fact]
    public async Task Quitting_while_busy_is_refused()
    {
        var model = StartWithoutDriver();
        var gate = new TaskCompletionSource();
        platform.InstallGate = gate.Task;
        var running = model.InstallDriverCommand.ExecuteAsync();
        while (platform.InstallCalls == 0)
            await Task.Delay(5);

        Assert.False(await model.PrepareToCloseAsync());
        Assert.Contains("still working", Assert.Single(dialogs.Errors));

        gate.SetResult();
        await running;
        Assert.True(await model.PrepareToCloseAsync());
    }

    [Fact]
    public async Task Check_reports_back()
    {
        await vm.LoadBackupAsync(MakeImage());
        await vm.CheckCommand.ExecuteAsync();
        var (title, text) = Assert.Single(dialogs.Texts);
        Assert.Contains("Content", title);
        Assert.Contains("No problems found", text);
    }

    [Fact]
    public async Task Check_describes_damage()
    {
        string path = MakeImage();
        await vm.LoadBackupAsync(path);
        using (var dev = new FileBlockDevice(path, true))
        {
            var content = DriveScanner.Scan(dev).Partitions.Single(p => p.Id == "content");
            using var vol = DriveScanner.OpenPartition(dev, content, true);
            var file = vol.CreateFile(vol.Root, "f.bin");
            vol.Write(file, 0, new byte[100_000]);
            // break the chain behind the volume's back
            var geo = vol.Geometry;
            dev.Write(content.Offset + geo.FatOffset + (file.FirstCluster + 1) * geo.FatEntrySize, new byte[geo.FatEntrySize]);
        }
        await vm.CheckCommand.ExecuteAsync();
        Assert.Contains("real damage", dialogs.Texts[0].Text);
    }

    [Fact]
    public async Task Format_runs_through_the_dialog_and_rescans()
    {
        string path = MakeImage();
        await vm.LoadBackupAsync(path);

        // put something on Content so we can see it disappear
        using (var dev = new FileBlockDevice(path, true))
        {
            var content = DriveScanner.Scan(dev).Partitions.Single(p => p.Id == "content");
            using var vol = DriveScanner.OpenPartition(dev, content, true);
            vol.CreateFile(vol.Root, "old.txt");
        }

        dialogs.Format = new FormatRequest(false, new[] { "content" }, 0, 0, "Fresh", FatxFlavor.Xbox360);
        await vm.FormatCommand.ExecuteAsync();

        Assert.NotNull(dialogs.FormatInfo);
        Assert.True(dialogs.FormatInfo!.LayoutFits);
        using var after = new FileBlockDevice(path, false);
        var p2 = DriveScanner.Scan(after).Partitions.Single(p => p.Id == "content");
        using var vol2 = DriveScanner.OpenPartition(after, p2, false);
        Assert.Null(vol2.Find("old.txt"));
        Assert.Equal("Fresh", vol2.Label);
        Assert.Equal("Fresh", vm.Partitions.Single(p => p.Id == "content").Label);
    }

    [Fact]
    public async Task Cancelling_the_format_dialog_changes_nothing()
    {
        string path = MakeImage();
        await vm.LoadBackupAsync(path);
        var before = new System.IO.FileInfo(path).LastWriteTimeUtc;
        dialogs.Format = null;
        await vm.FormatCommand.ExecuteAsync();
        Assert.Equal(before, new System.IO.FileInfo(path).LastWriteTimeUtc);
    }

    [Fact]
    public async Task A_mounted_drive_cant_be_formatted()
    {
        await vm.LoadBackupAsync(MakeImage());
        await vm.MountCommand.ExecuteAsync();
        Assert.False(vm.FormatCommand.CanExecute(null));
    }

    [Fact]
    public async Task Format_of_the_system_disk_is_refused_before_any_dialog()
    {
        platform.Disks.Add(Disk(0, system: true));
        platform.DiskData[0] = new MemoryBlockDevice(6 * Gib);
        await vm.InitializeAsync();
        vm.SelectedSource = vm.Sources[0];
        await vm.LoadingTask;

        dialogs.Format = new FormatRequest(false, new[] { "content" }, 0, 0, null, FatxFlavor.Xbox360);
        await vm.FormatCommand.ExecuteAsync();

        Assert.Null(dialogs.FormatInfo);
        Assert.Contains("never write", Assert.Single(dialogs.Errors));
        Assert.DoesNotContain(platform.OpenCalls, c => c.Writable);
    }

    [Fact]
    public async Task Whole_image_format_for_a_small_file()
    {
        string path = Path.Combine(dir, "small.img");
        using (var f = File.Create(path))
            f.SetLength(64 << 20);
        await vm.LoadBackupAsync(path);

        dialogs.Format = new FormatRequest(true, Array.Empty<string>(), 0, 0, "Tiny", FatxFlavor.Xbox360);
        await vm.FormatCommand.ExecuteAsync();

        Assert.False(dialogs.FormatInfo!.LayoutFits);
        Assert.Single(vm.Partitions, p => p.CanMount);
        Assert.Equal("Tiny", vm.Partitions.Single(p => p.CanMount).Label);
    }

    [Fact]
    public async Task Quitting_with_mounts_asks_and_unmounts()
    {
        await vm.LoadBackupAsync(MakeImage());
        await vm.MountCommand.ExecuteAsync();

        dialogs.ConfirmAnswer = false;
        Assert.False(await vm.PrepareToCloseAsync());
        Assert.Equal(1, vm.MountedCount);

        dialogs.ConfirmAnswer = true;
        Assert.True(await vm.PrepareToCloseAsync());
        Assert.Equal(0, vm.MountedCount);
        Assert.True(platform.Mounts[0].WasUnmounted);
    }

    [Fact]
    public async Task Quitting_with_nothing_mounted_just_quits()
    {
        await vm.LoadBackupAsync(MakeImage());
        Assert.True(await vm.PrepareToCloseAsync());
        Assert.Empty(dialogs.Confirmations);
    }

    [Fact]
    public async Task Switching_between_sources_remembers_what_is_mounted()
    {
        await vm.LoadBackupAsync(MakeImage("a.img"));
        var first = vm.SelectedSource!;
        await vm.MountCommand.ExecuteAsync();
        await vm.LoadBackupAsync(MakeImage("b.img"));
        Assert.All(vm.Partitions, p => Assert.False(p.IsMounted));

        vm.SelectedSource = first;
        await vm.LoadingTask;
        Assert.Contains(vm.Partitions, p => p.IsMounted);
    }
}

public class DialogViewModelTests
{
    const long Gib = 1L << 30;

    static FormatDialogInfo InfoFor(PhysicalDiskInfo? disk, long length, bool fits = true)
    {
        var dev = new MemoryBlockDevice(length);
        return new FormatDialogInfo(disk?.Display ?? "image.img", disk, length, fits, DriveScanner.Scan(dev));
    }

    static PhysicalDiskInfo Disk(int n) => new(n, 10 * Gib, 512, "Test", "S", "USB", true, false, false);

    [Fact]
    public void Format_stays_disabled_until_the_phrase_is_typed()
    {
        var vm = new FormatDialogViewModel(InfoFor(Disk(4), 10 * Gib));
        Assert.False(vm.CanFormat);
        Assert.Equal("disk 4", vm.ConfirmationPhrase);
        vm.ConfirmText = "disk 3";
        Assert.False(vm.CanFormat);
        vm.ConfirmText = " Disk 4 ";
        Assert.True(vm.CanFormat);
    }

    [Fact]
    public void Images_use_a_simpler_phrase()
    {
        var vm = new FormatDialogViewModel(InfoFor(null, 10 * Gib));
        Assert.Equal("format", vm.ConfirmationPhrase);
        vm.ConfirmText = "format";
        Assert.True(vm.CanFormat);
    }

    [Fact]
    public void Blank_drive_offers_all_four_partitions()
    {
        var vm = new FormatDialogViewModel(InfoFor(null, 10 * Gib));
        Assert.Equal(new[] { "sysext", "sysext2", "compat", "content" }, vm.Choices.Select(c => c.Id));
        Assert.All(vm.Choices, c => Assert.True(c.IsSelected));
    }

    [Fact]
    public void Formatted_drive_ticks_content_and_compat()
    {
        var dev = new MemoryBlockDevice(10 * Gib);
        HddFormatter.Format(dev, new[] { "sysext", "sysext2", "compat", "content" });
        var info = new FormatDialogInfo("x", null, dev.Length, true, DriveScanner.Scan(dev));
        var vm = new FormatDialogViewModel(info);
        Assert.Equal(new[] { "compat", "content" }, vm.Choices.Where(c => c.IsSelected).Select(c => c.Id).OrderBy(i => i));
    }

    [Fact]
    public void Missing_partitions_get_ticked_too()
    {
        var dev = new MemoryBlockDevice(10 * Gib);
        HddFormatter.Format(dev, new[] { "sysext", "content" });
        var info = new FormatDialogInfo("x", null, dev.Length, true, DriveScanner.Scan(dev));
        var vm = new FormatDialogViewModel(info);
        Assert.Equal(new[] { "compat", "content", "sysext2" }, vm.Choices.Where(c => c.IsSelected).Select(c => c.Id).OrderBy(i => i));
    }

    [Fact]
    public void The_compat_partition_can_be_switched_off()
    {
        var vm = new FormatDialogViewModel(InfoFor(null, 10 * Gib)) { ConfirmText = "format" };
        vm.Choices.Single(c => c.Id == "compat").IsSelected = false;
        var request = vm.BuildRequest()!;
        Assert.DoesNotContain("compat", request.PartitionIds);
        Assert.Contains("content", request.PartitionIds);
    }

    [Fact]
    public void Compat_only_asks_for_just_that_partition()
    {
        var vm = new FormatDialogViewModel(InfoFor(Disk(4), 10 * Gib)) { CompatOnly = true, ConfirmText = "disk 4" };
        var request = vm.BuildRequest()!;

        Assert.Equal(new[] { "compat" }, request.PartitionIds);
        Assert.False(request.WholeImage);
        Assert.Equal(0, request.ContentLength);
        Assert.Null(request.Label);
        Assert.False(vm.ShowChoices);
        Assert.False(vm.ShowContentOptions);
        Assert.True(vm.ShowAdvanced);
        Assert.Contains("Only the Xbox 1 Compatibility partition", vm.Warning);
        Assert.False(vm.FullFormat);
    }

    [Fact]
    public void Compat_only_says_whether_it_creates_or_replaces()
    {
        var blank = new FormatDialogViewModel(InfoFor(null, 10 * Gib)) { CompatOnly = true };
        Assert.False(blank.CompatIsFormatted);
        Assert.Contains("will be created", blank.CompatNote);

        var dev = new MemoryBlockDevice(10 * Gib);
        HddFormatter.Format(dev, new[] { "compat", "content" });
        var formatted = new FormatDialogViewModel(new FormatDialogInfo("x", null, dev.Length, true, DriveScanner.Scan(dev))) { CompatOnly = true };
        Assert.True(formatted.CompatIsFormatted);
        Assert.Contains("erased and made again", formatted.CompatNote);

        Assert.Equal("", new FormatDialogViewModel(InfoFor(null, 10 * Gib)).CompatNote);
    }

    [Fact]
    public void Compat_only_ignores_content_settings()
    {
        var vm = new FormatDialogViewModel(InfoFor(null, 10 * Gib)) { ConfirmText = "format", LimitContent = true, ContentGigabytes = "nonsense" };
        vm.Label = new string('x', 20);
        Assert.False(vm.CanFormat);

        vm.CompatOnly = true;
        Assert.Null(vm.ContentSizeProblem);
        Assert.Null(vm.LabelProblem);
        Assert.True(vm.CanFormat);

        // and going back brings the complaints back
        vm.FullFormat = true;
        Assert.False(vm.CanFormat);
    }

    [Fact]
    public void Compat_only_still_needs_the_phrase()
    {
        var vm = new FormatDialogViewModel(InfoFor(Disk(2), 10 * Gib)) { CompatOnly = true, CompatOriginalStyle = true };
        Assert.Null(vm.BuildRequest());
        vm.ConfirmText = "disk 2";
        Assert.Equal(FatxFlavor.Xbox, vm.BuildRequest()!.CompatFlavor);
    }

    [Fact]
    public void A_whole_image_has_no_modes_to_choose_between()
    {
        var vm = new FormatDialogViewModel(InfoFor(null, 64L << 20, fits: false));
        Assert.False(vm.ShowModes);
        Assert.False(vm.ShowAdvanced);
        Assert.True(vm.ShowContentOptions);
        Assert.False(vm.ShowChoices);

        var real = new FormatDialogViewModel(InfoFor(null, 10 * Gib));
        Assert.True(real.ShowModes);
        Assert.True(real.FullFormat);
        Assert.True(real.ShowChoices);
    }

    [Fact]
    public void Unticking_everything_disables_format()
    {
        var vm = new FormatDialogViewModel(InfoFor(null, 10 * Gib)) { ConfirmText = "format" };
        Assert.True(vm.CanFormat);
        foreach (var c in vm.Choices)
            c.IsSelected = false;
        Assert.False(vm.CanFormat);
    }

    [Fact]
    public void Content_limit_is_validated()
    {
        var vm = new FormatDialogViewModel(InfoFor(null, 10 * Gib)) { ConfirmText = "format", LimitContent = true };
        vm.ContentGigabytes = "abc";
        Assert.NotNull(vm.ContentSizeProblem);
        Assert.False(vm.CanFormat);
        vm.ContentGigabytes = "500";
        Assert.Contains("more than", vm.ContentSizeProblem);
        vm.ContentGigabytes = "2";
        Assert.Null(vm.ContentSizeProblem);
        Assert.True(vm.CanFormat);
        Assert.Equal(2 * Gib, vm.BuildRequest()!.ContentLength);
    }

    [Fact]
    public void Labels_are_limited_to_15_characters()
    {
        var vm = new FormatDialogViewModel(InfoFor(null, 10 * Gib)) { ConfirmText = "format" };
        vm.Label = new string('x', 16);
        Assert.NotNull(vm.LabelProblem);
        Assert.False(vm.CanFormat);
        vm.Label = "Games";
        Assert.True(vm.CanFormat);
        Assert.Equal("Games", vm.BuildRequest()!.Label);
    }

    [Fact]
    public void Request_carries_the_options()
    {
        var vm = new FormatDialogViewModel(InfoFor(null, 10 * Gib)) { ConfirmText = "format" };
        vm.Cluster = vm.ClusterChoices[2];
        vm.CompatOriginalStyle = true;
        vm.Choices.First(c => c.Id == "sysext").IsSelected = false;
        var request = vm.BuildRequest()!;
        Assert.False(request.WholeImage);
        Assert.Equal(0x40, request.ContentSectorsPerCluster);
        Assert.Equal(FatxFlavor.Xbox, request.CompatFlavor);
        Assert.DoesNotContain("sysext", request.PartitionIds);
        Assert.Contains("content", request.PartitionIds);
    }

    [Fact]
    public void A_small_image_only_offers_one_volume()
    {
        var vm = new FormatDialogViewModel(InfoFor(null, 64L << 20, fits: false)) { ConfirmText = "format" };
        Assert.True(vm.WholeImage);
        Assert.Empty(vm.Choices);
        Assert.True(vm.CanFormat);
        Assert.True(vm.BuildRequest()!.WholeImage);
    }

    [Fact]
    public void Request_is_null_until_valid()
    {
        var vm = new FormatDialogViewModel(InfoFor(Disk(2), 10 * Gib));
        Assert.Null(vm.BuildRequest());
    }
}
