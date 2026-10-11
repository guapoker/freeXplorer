using FreeXplorer.Fatx;
using FreeXplorer.Mount;
using FreeXplorer.Storage;
using FreeXplorer.Xbox360;

namespace FreeXplorer.App.Services;

// Everything the view models need from Windows, so tests can stand in for it
public interface IPlatform
{
    // the installed WinFsp version, or null when the driver isn't there
    string? DriverVersion { get; }
    Task<DriverInstallResult> InstallDriverAsync(Action<string>? status, Action<double>? progress);

    IReadOnlyList<PhysicalDiskInfo> ListDisks();

    // Opens a physical disk, after checking it's still the drive that was listed. Throws
    // DiskChangedException when it isn't.
    IBlockDevice OpenDisk(PhysicalDiskInfo disk, bool writable);

    // shows up as a drive letter in This PC
    IMountedVolume Mount(FatxVolume volume, string label);
    void OpenFolder(string path);

    // stops Windows going to sleep in the middle of a long copy until the result is disposed
    IDisposable KeepAwake(string reason);
}

public interface IMountedVolume : IDisposable
{
    string MountPoint { get; }
    bool ReadOnly { get; }
    void Unmount();
}

public sealed record FormatRequest(
    bool WholeImage,
    IReadOnlyList<string> PartitionIds,
    int ContentSectorsPerCluster,
    long ContentLength,
    string? Label,
    FatxFlavor CompatFlavor);

public sealed record FormatDialogInfo(
    string Title,
    PhysicalDiskInfo? Disk,
    long Length,
    bool LayoutFits,
    DriveScan Scan);

public sealed record BackupDialogInfo(PhysicalDiskInfo Disk, string SuggestedName);

public sealed record BackupRequest(string Path, bool Verify);

// the disks on offer, the backup to start from if one is selected, the keys ("disk:4") of drives that
// have partitions mounted from them, and the security sector of each disk that has one
public sealed record RestoreDialogInfo(
    IReadOnlyList<PhysicalDiskInfo> Disks,
    string? BackupPath,
    IReadOnlySet<string> InUse,
    IReadOnlyDictionary<int, SecuritySector>? SecuritySectors = null);

public sealed record RestoreRequest(string BackupPath, PhysicalDiskInfo Target, bool Verify, bool KeepTargetHeader = false);

public interface IDialogs
{
    string? PickBackupToLoad();
    BackupRequest? AskCreateBackup(BackupDialogInfo info);
    RestoreRequest? AskRestore(RestoreDialogInfo info);
    FormatRequest? AskFormat(FormatDialogInfo info);
    bool Confirm(string title, string message);
    void ShowText(string title, string text);
    void ShowError(string message);
}
