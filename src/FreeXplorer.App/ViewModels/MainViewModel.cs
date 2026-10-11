using System.Collections.ObjectModel;
using System.Text;
using FreeXplorer.App.Services;
using FreeXplorer.Fatx;
using FreeXplorer.Mount;
using FreeXplorer.Storage;
using FreeXplorer.Xbox360;

namespace FreeXplorer.App.ViewModels;

public enum DriverState
{
    Missing,
    Installed,
    Installing,
    RestartNeeded,
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    sealed class MountEntry
    {
        public required IBlockDevice Device { get; init; }
        public required FatxVolume Volume { get; init; }
        public required IMountedVolume Mounted { get; init; }
        public required PartitionItem Item { get; init; }
        public required string SourceKey { get; init; }
    }

    readonly IPlatform platform;
    readonly IDialogs dialogs;
    readonly Dictionary<string, MountEntry> mounts = new();

    SourceItem? selectedSource;
    PartitionItem? selectedPartition;
    bool allowWriting;
    bool busy;
    double progress;
    string status = "Pick a drive on the left, or load a backup.";
    Task loading = Task.CompletedTask;
    string? driverVersion;
    bool installingDriver;
    bool driverNeedsRestart;
    CancellationTokenSource? cancelSource;
    string? cancelWarning;
    int dialogDepth;
    int deviceChanges;
    bool recovering;

    public MainViewModel(IPlatform platform, IDialogs dialogs)
    {
        this.platform = platform;
        this.dialogs = dialogs;
        driverVersion = platform.DriverVersion;

        RefreshCommand = new RelayCommand(RefreshAsync, () => !Busy);
        LoadBackupCommand = new RelayCommand(LoadBackupAsync, () => !Busy);
        CreateBackupCommand = new RelayCommand(CreateBackupAsync, () => !Busy && SelectedSource is { Kind: SourceKind.Disk });
        RestoreBackupCommand = new RelayCommand(RestoreBackupAsync, () => !Busy);
        CancelCommand = new RelayCommand(CancelCurrent, () => CanCancel);
        MountCommand = new RelayCommand(MountSelectedAsync, () => !Busy && SelectedPartition is { CanMount: true, IsMounted: false });
        UnmountCommand = new RelayCommand(UnmountSelectedAsync, () => !Busy && SelectedPartition is { IsMounted: true });
        OpenInExplorerCommand = new RelayCommand(OpenInExplorer, () => SelectedPartition is { IsMounted: true });
        CheckCommand = new RelayCommand(CheckAsync, () => !Busy && SelectedPartition is { CanMount: true });
        FormatCommand = new RelayCommand(FormatAsync, () => !Busy && SelectedSource?.Scan is not null && !MountedFromSelectedSource());
        InstallDriverCommand = new RelayCommand(InstallDriverAsync, () => !Busy && DriverState == DriverState.Missing);
    }

    public ObservableCollection<SourceItem> Sources { get; } = new();
    public ObservableCollection<PartitionItem> Partitions { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand LoadBackupCommand { get; }
    public RelayCommand CreateBackupCommand { get; }
    public RelayCommand RestoreBackupCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand MountCommand { get; }
    public RelayCommand UnmountCommand { get; }
    public RelayCommand OpenInExplorerCommand { get; }
    public RelayCommand CheckCommand { get; }
    public RelayCommand FormatCommand { get; }
    public RelayCommand InstallDriverCommand { get; }

    public DriverState DriverState =>
        installingDriver ? DriverState.Installing
        : driverNeedsRestart ? DriverState.RestartNeeded
        : driverVersion is null ? DriverState.Missing
        : DriverState.Installed;

    // the label of the driver button, which doubles as the driver's status
    public string DriverText => DriverState switch
    {
        DriverState.Installed => "Driver installed",
        DriverState.Installing => "Installing driver...",
        DriverState.RestartNeeded => "Restart Windows",
        _ => "Install driver",
    };

    public string DriverDetail => DriverState switch
    {
        DriverState.Installed => $"WinFsp {driverVersion} is installed. It's the free driver that lets Windows show an Xbox 360 partition as a drive.",
        DriverState.Installing => "WinFsp is being installed.",
        DriverState.RestartNeeded => "WinFsp is installed, but Windows needs a restart before it will work.",
        _ => "The WinFsp driver isn't installed. It's the free driver that lets Windows show an Xbox 360 partition as a drive, and mounting needs it. Click to download and install it (about 2 MB).",
    };

    public SourceItem? SelectedSource
    {
        get => selectedSource;
        set
        {
            if (!Set(ref selectedSource, value))
                return;
            loading = ShowSourceAsync(value);
            UpdateCommands();
        }
    }

    public PartitionItem? SelectedPartition
    {
        get => selectedPartition;
        set
        {
            if (Set(ref selectedPartition, value))
                UpdateCommands();
        }
    }

    public bool AllowWriting
    {
        get => allowWriting;
        set => Set(ref allowWriting, value);
    }

    public bool Busy
    {
        get => busy;
        private set
        {
            if (Set(ref busy, value))
                UpdateCommands();
        }
    }

    public double Progress
    {
        get => progress;
        private set => Set(ref progress, value);
    }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public int MountedCount => mounts.Count;

    // true while something long that can be stopped is running
    public bool CanCancel => cancelSource is { IsCancellationRequested: false };

    // how long to wait after Windows reports a plug or unplug before looking again, so a burst of
    // events from one drive turns into one refresh
    public TimeSpan DeviceChangeDelay { get; set; } = TimeSpan.FromSeconds(2);

    // lets tests wait for the load that a selection kicks off
    public Task LoadingTask => loading;

    // ---- sources ----

    public async Task InitializeAsync()
    {
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        await Run("Looking for drives...", async () =>
        {
            RefreshDriver();
            var disks = await Task.Run(platform.ListDisks);
            var keep = SelectedSource?.Key;

            foreach (var old in Sources.Where(s => s.Kind == SourceKind.Disk).ToList())
            {
                if (mounts.Values.Any(m => m.SourceKey == old.Key))
                    continue;   // can't forget a disk that is mounted
                DisposeReader(old);
                Sources.Remove(old);
            }
            int index = 0;
            foreach (var disk in disks)
            {
                if (Sources.Any(s => s.Key == $"disk:{disk.Number}"))
                    continue;
                Sources.Insert(index++, new SourceItem(disk));
            }
            AddLog(disks.Count == 0 ? "No physical disks found." : $"Found {disks.Count} disk{(disks.Count == 1 ? "" : "s")}.");

            if (keep is not null && Sources.FirstOrDefault(s => s.Key == keep) is { } same)
            {
                same.Scan = null;
                if (SelectedSource == same)
                    await ShowSourceAsync(same, force: true);
                else
                    SelectedSource = same;
            }
            Status = "Ready.";
        });
    }

    async Task LoadBackupAsync()
    {
        string? path = Ask(dialogs.PickBackupToLoad);
        if (path is null)
            return;
        await LoadBackupAsync(path);
    }

    public async Task LoadBackupAsync(string path)
    {
        var existing = Sources.FirstOrDefault(s => s.Key == Path.GetFullPath(path));
        if (existing is null)
        {
            existing = new SourceItem(path);
            Sources.Add(existing);
        }
        SelectedSource = existing;
        await LoadingTask;
    }

    async Task ShowSourceAsync(SourceItem? source, bool force = false)
    {
        Partitions.Clear();
        SelectedPartition = null;
        if (source is null)
            return;

        if (source.Scan is null || force)
            await LoadSourceAsync(source);
        else if (SelectedSource == source)
            ShowItems(source);
    }

    async Task LoadSourceAsync(SourceItem source)
    {
        await Run($"Reading {source.Title}...", async () =>
        {
            try
            {
                var (reader, scan, items) = await Task.Run(() => ReadSource(source));
                DisposeReader(source);
                source.Reader = reader;
                source.Scan = scan;
                source.Items = items;
                foreach (var item in items)
                    if (mounts.TryGetValue(MountKey(source, item), out var m))
                    {
                        item.MountPoint = m.Mounted.MountPoint;
                        item.MountedReadOnly = m.Mounted.ReadOnly;
                    }
                if (SelectedSource == source)
                    ShowItems(source);

                int usable = items.Count(i => i.CanMount);
                Status = scan.Kind switch
                {
                    DriveKind.Unknown => $"{source.Title}: no Xbox 360 partitions found. It may be blank, you can format it.",
                    DriveKind.SinglePartition => $"{source.Title}: a single FATX partition.",
                    _ => $"{source.Title}: Xbox 360 drive, {usable} partition{(usable == 1 ? "" : "s")} you can open.",
                };
                AddLog(Status);
            }
            catch (UnauthorizedAccessException ex)
            {
                Status = ex.Message;
                AddLog(ex.Message);
                source.Scan = null;
            }
        });
    }

    (IBlockDevice Reader, DriveScan Scan, List<PartitionItem> Items) ReadSource(SourceItem source)
    {
        IBlockDevice device = source.Disk is { } disk
            ? platform.OpenDisk(disk, writable: false)
            : new FileBlockDevice(source.ImagePath!, writable: false);
        try
        {
            var scan = DriveScanner.Scan(device);
            var items = scan.Partitions.Select(p => Describe(device, p)).ToList();
            return (device, scan, items);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    static PartitionItem Describe(IBlockDevice device, DetectedPartition p)
    {
        string format = "";
        string? label = null;
        if (p.CanMount)
        {
            try
            {
                using var volume = DriveScanner.OpenPartition(device, p, writable: false);
                string kind = volume.Flavor == FatxFlavor.Xbox360 ? "XTAF" : "FATX";
                format = $"{kind}, {volume.ClusterSize / 1024} KB clusters, FAT{volume.Geometry.FatEntrySize * 8}";
                label = volume.Label;
            }
            catch (IOException ex)
            {
                format = $"Looks damaged: {ex.Message}";
            }
        }
        return new PartitionItem(p, format, label);
    }

    void ShowItems(SourceItem source)
    {
        Partitions.Clear();
        foreach (var item in source.Items ?? new())
            Partitions.Add(item);
        // Content is what people come for, so start there when it's usable
        SelectedPartition = Partitions.FirstOrDefault(p => p.Id == HddLayout.ContentId && p.CanMount)
            ?? Partitions.FirstOrDefault(p => p.CanMount)
            ?? Partitions.FirstOrDefault();
        UpdateCommands();
    }

    static void DisposeReader(SourceItem source)
    {
        source.Reader?.Dispose();
        source.Reader = null;
    }

    // ---- mounting ----

    async Task MountSelectedAsync()
    {
        if (SelectedSource is { } source && SelectedPartition is { } part)
            await MountAsync(source, part);
    }

    async Task MountAsync(SourceItem source, PartitionItem part)
    {
        // it may have been installed since the last look
        RefreshDriver();
        if (DriverState != DriverState.Installed)
        {
            ShowError(DriverState == DriverState.RestartNeeded
                ? "WinFsp is installed, but Windows needs to restart before it will work."
                : "The WinFsp driver isn't installed yet. It's the free driver that lets Windows show these partitions as drives. Press \"Install driver\" at the top right, then try again.");
            return;
        }

        bool write = AllowWriting;
        if (write)
        {
            if (source.Disk is { } disk)
            {
                var verdict = DiskSafety.CanWrite(disk);
                if (!verdict.Allowed)
                {
                    ShowError(verdict.Reason!);
                    return;
                }
            }
            if (!Confirm("Mount with writing turned on?",
                "Changes you make in Explorer are written straight to the drive. A bug here, or pulling the cable at the wrong moment, can corrupt it.\n\nMake sure anything you care about is backed up. Continue?"))
                return;
        }

        await Run($"Mounting {part.Name}...", async () =>
        {
            var entry = await Task.Run(() => MountOne(source, part, write));
            mounts[MountKey(source, part)] = entry;
            part.MountPoint = entry.Mounted.MountPoint;
            part.MountedReadOnly = entry.Mounted.ReadOnly;
            part.Usage = $"{Size.Format(entry.Volume.TotalBytes - entry.Volume.FreeBytes)} used of {Size.Format(entry.Volume.TotalBytes)}";
            Status = $"{part.Name} is now {entry.Mounted.MountPoint}";
            AddLog($"{Status} ({(entry.Mounted.ReadOnly ? "read-only" : "read/write")})");
            Raise(nameof(MountedCount));
        });
        if (part.IsMounted)
            platform.OpenFolder(part.MountPoint + "\\");
    }

    MountEntry MountOne(SourceItem source, PartitionItem part, bool write)
    {
        IBlockDevice device = source.Disk is { } disk
            ? platform.OpenDisk(disk, write)
            : new FileBlockDevice(source.ImagePath!, write);
        FatxVolume? volume = null;
        try
        {
            volume = DriveScanner.OpenPartition(device, part.Detected, write);
            volume.ScanFreeSpace();
            string label = volume.Label is { Length: > 0 } l ? l : part.Name;
            var mounted = platform.Mount(volume, label);
            return new MountEntry { Device = device, Volume = volume, Mounted = mounted, Item = part, SourceKey = source.Key };
        }
        catch
        {
            volume?.Dispose();
            device.Dispose();
            throw;
        }
    }

    async Task UnmountSelectedAsync()
    {
        if (SelectedSource is { } source && SelectedPartition is { IsMounted: true } part)
            await Run($"Unmounting {part.Name}...", () => UnmountAsync(MountKey(source, part)));
    }

    public async Task UnmountAllAsync()
    {
        await Run("Unmounting...", async () =>
        {
            foreach (var key in mounts.Keys.ToList())
                await UnmountAsync(key);
        });
    }

    async Task UnmountAsync(string key)
    {
        if (!mounts.TryGetValue(key, out var entry))
            return;
        string point = entry.Mounted.MountPoint;
        await Task.Run(() =>
        {
            try
            {
                entry.Mounted.Unmount();
            }
            finally
            {
                entry.Volume.Dispose();
                entry.Device.Dispose();
            }
        });
        mounts.Remove(key);
        entry.Item.MountPoint = null;
        Status = $"Unmounted {point}";
        AddLog(Status);
        Raise(nameof(MountedCount));
    }

    void OpenInExplorer()
    {
        if (SelectedPartition is { MountPoint: { } point })
            platform.OpenFolder(point + "\\");
    }

    // lets go of every handle: mounts first, then the readers behind the drive list
    public void Dispose()
    {
        foreach (var entry in mounts.Values)
        {
            try { entry.Mounted.Unmount(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            entry.Volume.Dispose();
            entry.Device.Dispose();
            entry.Item.MountPoint = null;
        }
        mounts.Clear();
        foreach (var source in Sources)
            DisposeReader(source);
    }

    // false means the user changed their mind about quitting
    public async Task<bool> PrepareToCloseAsync()
    {
        if (Busy)
        {
            ShowError("freeXplorer is still working. Wait for it to finish before quitting, a drive can be left half written otherwise.");
            return false;
        }
        if (mounts.Count == 0)
            return true;
        if (!Confirm("Quit freeXplorer?",
            $"{mounts.Count} partition{(mounts.Count == 1 ? " is" : "s are")} still mounted. They'll be unmounted if you quit."))
            return false;
        await UnmountAllAsync();
        return mounts.Count == 0;
    }

    // ---- the driver ----

    async Task InstallDriverAsync()
    {
        if (!Confirm("Install the WinFsp driver?",
            $"freeXplorer will download WinFsp {WinFspInstaller.Version} (about 2 MB) from github.com/winfsp/winfsp, check it against a known checksum and install it. " +
            "WinFsp is the free, open source driver that lets Windows show an Xbox 360 partition as a drive.\n\n" +
            "This changes Windows. You can remove WinFsp again in Settings > Apps.\n\nInstall it now?"))
            return;

        await Run("Installing the driver...", async () =>
        {
            installingDriver = true;
            RaiseDriver();
            try
            {
                var result = await Task.Run(() => platform.InstallDriverAsync(s => Status = s, p => Progress = p));
                driverNeedsRestart = result == DriverInstallResult.NeedsRestart;
                if (driverNeedsRestart)
                {
                    Status = "Restart Windows to finish installing the driver.";
                    AddLog("Installed WinFsp. Windows wants a restart before it will work.");
                    ShowText("Restart needed", "WinFsp is installed, but Windows needs to restart before it can use it.\n\nRestart Windows, then start freeXplorer again.");
                }
                else
                {
                    Status = "The driver is installed. You can mount now.";
                    AddLog($"Installed WinFsp {platform.DriverVersion}.");
                }
            }
            finally
            {
                installingDriver = false;
                RefreshDriver();
            }
        });
    }

    void RefreshDriver()
    {
        driverVersion = platform.DriverVersion;
        RaiseDriver();
    }

    void RaiseDriver()
    {
        Raise(nameof(DriverState));
        Raise(nameof(DriverText));
        Raise(nameof(DriverDetail));
        InstallDriverCommand.Changed();
    }

    // ---- checking and formatting ----

    async Task CheckAsync()
    {
        if (SelectedSource is not { } source || SelectedPartition is not { CanMount: true } part)
            return;

        await Run($"Checking {part.Name}...", async () =>
        {
            string text = await Task.Run(() =>
            {
                // a mounted volume is being modified through its own copy of the directory tree, so check it read-only
                using var reader = OpenForReading(source);
                using var volume = DriveScanner.OpenPartition(reader, part.Detected, writable: false);
                var report = volume.Check();
                return FormatReport(part.Name, report);
            });
            ShowText($"Check of {part.Name}", text);
            AddLog($"Checked {part.Name}.");
        });
    }

    IBlockDevice OpenForReading(SourceItem source) =>
        source.Disk is { } disk ? platform.OpenDisk(disk, false) : new FileBlockDevice(source.ImagePath!, false);

    static string FormatReport(string name, FsckReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{name}: {report.Directories} folders, {report.Files} files, {Size.Format(report.FileBytes)} of data.");
        sb.AppendLine();
        if (report.Issues.Count == 0)
        {
            sb.AppendLine("No problems found.");
        }
        else
        {
            foreach (var issue in report.Issues)
                sb.AppendLine($"{issue.Severity}: {issue.Message}");
            sb.AppendLine();
            sb.AppendLine(report.Errors > 0
                ? "There is real damage. Copy your files somewhere safe before changing anything."
                : "Nothing is lost, there is just some unused space that the command line tool can release (fsck --fix).");
        }
        return sb.ToString();
    }

    bool MountedFromSelectedSource() =>
        SelectedSource is { } s && mounts.Values.Any(m => m.SourceKey == s.Key);

    async Task FormatAsync()
    {
        if (SelectedSource is not { Scan: { } scan } source)
            return;
        if (MountedFromSelectedSource())
        {
            ShowError("Unmount this drive's partitions before formatting it.");
            return;
        }
        if (source.Disk is { } disk)
        {
            var verdict = DiskSafety.CanWrite(disk);
            if (!verdict.Allowed)
            {
                ShowError(verdict.Reason!);
                return;
            }
        }

        long length = source.Reader?.Length ?? source.Disk?.Length ?? 0;
        var info = new FormatDialogInfo(source.Title, source.Disk, length, length >= HddLayout.MinimumDriveLength, scan);
        var request = Ask(() => dialogs.AskFormat(info));
        if (request is null)
            return;

        await Run($"Formatting {source.Title}...", async () =>
        {
            DisposeReader(source);
            await Task.Run(() =>
            {
                using IBlockDevice device = source.Disk is { } d
                    ? platform.OpenDisk(d, writable: true)
                    : new FileBlockDevice(source.ImagePath!, writable: true);

                if (request.WholeImage)
                {
                    FatxFormatter.Format(device, 0, device.Length / 512 * 512, new FatxFormatOptions
                    {
                        SectorsPerCluster = request.ContentSectorsPerCluster,
                        Label = request.Label,
                    }, p => Progress = p);
                }
                else
                {
                    HddFormatter.Format(device, request.PartitionIds, new HddFormatOptions
                    {
                        ContentSectorsPerCluster = request.ContentSectorsPerCluster,
                        ContentLength = request.ContentLength,
                        ContentLabel = request.Label,
                        CompatFlavor = request.CompatFlavor,
                    }, (step, p) => Progress = p);
                }
                device.Flush();
            });
            AddLog($"Formatted {source.Title}.");
            source.Scan = null;
            await LoadSourceAsync(source);
        });
    }

    // ---- backups ----

    async Task CreateBackupAsync()
    {
        if (SelectedSource is not { Disk: { } disk } source)
            return;
        if (mounts.Values.Any(m => m.SourceKey == source.Key && !m.Mounted.ReadOnly))
        {
            ShowError("A partition on this drive is mounted with writing turned on. Unmount it first, so the backup can't catch a file half way through changing.");
            return;
        }

        var info = new BackupDialogInfo(disk, SuggestedBackupName(disk));
        var request = Ask(() => dialogs.AskCreateBackup(info));
        if (request is null)
            return;

        await RunCancellable($"Backing up {source.Title}...", null, async cancel =>
        {
            using var awake = platform.KeepAwake("Backing up a drive");
            BackupResult result;
            try
            {
                result = await Task.Run(() =>
                {
                    using var reader = platform.OpenDisk(disk, writable: false);
                    return DriveBackup.Create(reader, request.Path, new BackupOptions(request.Verify), ReportCopy, cancel);
                }, cancel);
            }
            catch (OperationCanceledException)
            {
                Status = "Backup stopped.";
                AddLog($"Backup of {source.Title} stopped, the partly written file was deleted.");
                return;
            }

            Status = $"Backup of {source.Title} is done.";
            AddLog($"Backed up {disk.Display} to {result.Path}.");
            ShowText("Backup finished", BackupSummary(disk, result));
        });
    }

    static string SuggestedBackupName(PhysicalDiskInfo disk)
    {
        string size = Size.Format(disk.Length).Replace(" ", "");
        return $"Xbox 360 drive {size} {DateTime.Now:yyyy-MM-dd}.img";
    }

    static string BackupSummary(PhysicalDiskInfo disk, BackupResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.Path);
        sb.AppendLine();
        sb.AppendLine($"Drive: {disk.Display}");
        sb.AppendLine($"Size: {Size.Format(result.Length)}, taking {Size.Format(result.SizeOnDisk)} on disk (the empty parts of the drive aren't stored)");
        sb.AppendLine(result.Verified ? "Checked: the file was read back and matches the drive." : "Checked: no, the file wasn't read back.");
        sb.AppendLine($"SHA-256: {result.Sha256}");
        sb.AppendLine();
        sb.AppendLine($"A small file called {Path.GetFileName(DriveBackup.ChecksumPath(result.Path))} was saved beside it. Keep the two together: Restore uses it to spot a damaged backup before anything is written to a drive.");
        return sb.ToString();
    }

    async Task RestoreBackupAsync()
    {
        var disks = await Task.Run(platform.ListDisks);
        var sectors = await Task.Run(() => ReadSecuritySectors(disks));
        string? backup = SelectedSource is { Kind: SourceKind.Image } image ? image.ImagePath : null;
        var inUse = mounts.Values.Select(m => m.SourceKey).ToHashSet();
        var request = Ask(() => dialogs.AskRestore(new RestoreDialogInfo(disks, backup, inUse, sectors)));
        if (request is null)
            return;

        // the window already refuses these, this is the last look before anything is written
        var verdict = DiskSafety.CanWrite(request.Target);
        if (!verdict.Allowed)
        {
            ShowError(verdict.Reason!);
            return;
        }
        string key = $"disk:{request.Target.Number}";
        if (mounts.Values.Any(m => m.SourceKey == key))
        {
            ShowError("Unmount this drive's partitions before restoring a backup to it.");
            return;
        }

        var loaded = Sources.FirstOrDefault(s => s.Key == key);
        bool wroteSomething = false;
        await RunCancellable($"Restoring to {request.Target.Display}...",
            "Stop restoring?\n\nThe drive has only been partly written. It won't work until you restore a backup to it again.",
            async cancel =>
            {
                if (loaded is not null)
                    DisposeReader(loaded);
                using var awake = platform.KeepAwake("Restoring a backup to a drive");
                try
                {
                    var options = new RestoreOptions(request.Verify)
                    {
                        WritingStarted = () => wroteSomething = true,
                        SkipFirstBytes = request.KeepTargetHeader ? SecuritySectors.HeaderLength : 0,
                    };
                    var result = await Task.Run(() =>
                    {
                        using var target = platform.OpenDisk(request.Target, writable: true);
                        if (request.KeepTargetHeader)
                        {
                            // the window checked this already, but the drive in the slot could have changed since
                            using var image = new FileBlockDevice(request.BackupPath, writable: false);
                            if (SecuritySectors.KeepProblem(image, target) is { } problem)
                                throw new InvalidOperationException(problem);
                        }
                        return DriveBackup.Restore(request.BackupPath, target, options, ReportCopy, cancel);
                    }, cancel);
                    Status = $"{request.Target.Display} now matches the backup.";
                    AddLog($"Restored {Path.GetFileName(request.BackupPath)} to {request.Target.Display}"
                        + (result.Skipped > 0 ? ", keeping the drive's own security sector" : "")
                        + (result.Verified ? ", read back and checked." : "."));
                    if (!result.ChecksumFileUsed)
                        AddLog("There was no checksum file beside the backup, so the backup itself couldn't be checked for damage.");
                }
                catch (OperationCanceledException)
                {
                    Status = "Restore stopped.";
                    if (wroteSomething)
                    {
                        AddLog($"Restore to {request.Target.Display} stopped part way. The drive holds a partial copy.");
                        ShowText("Restore stopped", "The drive was only partly written, so it won't work yet. Restore the backup to it again to finish the job.");
                    }
                    else
                    {
                        AddLog($"Restore to {request.Target.Display} stopped before anything was written.");
                    }
                }
                catch (Exception) when (wroteSomething)
                {
                    AddLog($"{request.Target.Display} may hold a partial copy now, restore the backup to it again.");
                    throw;
                }
            });

        // whatever happened, what the list knows about that drive is out of date now
        await RefreshAsync();
    }

    // The security sector of every drive that could be restored to and has one, for the restore window to
    // say whether a drive can keep its own. A drive that won't be read just has no answer.
    Dictionary<int, SecuritySector> ReadSecuritySectors(IReadOnlyList<PhysicalDiskInfo> disks)
    {
        var found = new Dictionary<int, SecuritySector>();
        foreach (var disk in disks.Where(d => !d.IsSystem && !d.HasVolumes))
        {
            try
            {
                using var device = platform.OpenDisk(disk, writable: false);
                if (SecuritySectors.Read(device) is { } sector)
                    found[disk.Number] = sector;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found;
    }

    void ReportCopy(CopyProgress p)
    {
        Progress = p.Fraction;
        Status = $"{p.Phase} ({p.Summary})";
    }

    // A long job that can be cancelled. The Cancel button shows while it runs, and the warning (if there
    // is one) is confirmed before the cancel goes through.
    async Task RunCancellable(string working, string? warnBeforeStopping, Func<CancellationToken, Task> work)
    {
        using var cts = new CancellationTokenSource();
        cancelSource = cts;
        cancelWarning = warnBeforeStopping;
        Raise(nameof(CanCancel));
        CancelCommand.Changed();
        try
        {
            await Run(working, () => work(cts.Token));
        }
        finally
        {
            cancelSource = null;
            cancelWarning = null;
            Raise(nameof(CanCancel));
            CancelCommand.Changed();
        }
    }

    void CancelCurrent()
    {
        if (cancelSource is not { IsCancellationRequested: false } cts)
            return;
        if (cancelWarning is { } warning && !Confirm("Stop?", warning))
            return;
        // the job may have finished while the question was on screen
        if (!ReferenceEquals(cancelSource, cts))
            return;
        cts.Cancel();
        Status = "Stopping...";
        Raise(nameof(CanCancel));
        CancelCommand.Changed();
    }

    // ---- drives coming and going ----

    // Windows says something was plugged in or pulled out. Wait for it to settle, then look again, so the
    // list never shows a drive that has been swapped for another one.
    public void DevicesChanged()
    {
        int version = Interlocked.Increment(ref deviceChanges);
        _ = RefreshWhenQuietAsync(version);
    }

    async Task RefreshWhenQuietAsync(int version)
    {
        try
        {
            await Task.Delay(DeviceChangeDelay);
            while (version == Volatile.Read(ref deviceChanges) && (Busy || dialogDepth > 0))
                await Task.Delay(300);   // not in the middle of a job, and not while a window is asking something
            if (version != Volatile.Read(ref deviceChanges))
                return;   // another change came in, it gets its own turn

            var disks = await Task.Run(platform.ListDisks);
            if (!SameAsListed(disks))
                await RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            AddLog($"Couldn't look for drives: {ex.Message}");
        }
    }

    bool SameAsListed(IReadOnlyList<PhysicalDiskInfo> disks)
    {
        var listed = Sources.Where(s => s.Kind == SourceKind.Disk).Select(s => s.Disk).ToList();
        return listed.Count == disks.Count && listed.Zip(disks).All(pair => pair.First == pair.Second);
    }

    // ---- plumbing ----

    async Task Run(string working, Func<Task> work)
    {
        DiskChangedException? changed = null;
        Busy = true;
        Progress = 0;
        Status = working;
        try
        {
            await work();
        }
        catch (DiskChangedException ex)
        {
            Status = "The drive changed.";
            AddLog($"Error: {ex.Message}");
            changed = ex;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException or MountException or DriverInstallException)
        {
            Status = "That didn't work.";
            AddLog($"Error: {ex.Message}");
            ShowError(ex.Message);
        }
        finally
        {
            Busy = false;
            Progress = 0;
            UpdateCommands();
        }

        if (changed is not null)
            await RecoverFromChangeAsync(changed);
    }

    // A drive was swapped behind the list's back. Refresh so the next try starts from what's really
    // plugged in, and say what happened.
    async Task RecoverFromChangeAsync(DiskChangedException problem)
    {
        if (!recovering)
        {
            recovering = true;
            try
            {
                await RefreshAsync();
            }
            finally
            {
                recovering = false;
            }
        }
        ShowError($"{problem.Message} The list has been refreshed. Check it's the drive you mean, then try again.");
    }

    // every window that stops for an answer goes through here, which is how the list knows to leave things alone
    T Ask<T>(Func<T> ask)
    {
        dialogDepth++;
        try
        {
            return ask();
        }
        finally
        {
            dialogDepth--;
        }
    }

    bool Confirm(string title, string message) => Ask(() => dialogs.Confirm(title, message));

    void ShowError(string message) => Ask(() =>
    {
        dialogs.ShowError(message);
        return 0;
    });

    void ShowText(string title, string text) => Ask(() =>
    {
        dialogs.ShowText(title, text);
        return 0;
    });

    static string MountKey(SourceItem source, PartitionItem part) => $"{source.Key}|{part.Id}";

    void AddLog(string text)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {text}");
        while (Log.Count > 300)
            Log.RemoveAt(0);
    }

    void UpdateCommands()
    {
        RefreshCommand.Changed();
        LoadBackupCommand.Changed();
        CreateBackupCommand.Changed();
        RestoreBackupCommand.Changed();
        MountCommand.Changed();
        UnmountCommand.Changed();
        OpenInExplorerCommand.Changed();
        CheckCommand.Changed();
        FormatCommand.Changed();
        InstallDriverCommand.Changed();
    }
}
