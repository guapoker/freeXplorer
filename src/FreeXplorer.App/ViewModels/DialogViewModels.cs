using System.Globalization;
using FreeXplorer.Fatx;
using FreeXplorer.Storage;
using FreeXplorer.Xbox360;
using FreeXplorer.App.Services;

namespace FreeXplorer.App.ViewModels;

public sealed class PartitionChoice : ObservableObject
{
    bool selected;

    public PartitionChoice(string id, string name, string description, long length, bool selected)
    {
        Id = id;
        Name = name;
        Description = description;
        Length = length;
        this.selected = selected;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public long Length { get; }
    public string SizeText => Size.Format(Length);

    public bool IsSelected
    {
        get => selected;
        set => Set(ref selected, value);
    }
}

public sealed record ClusterChoice(string Text, int Sectors);

// The format window. All the rules live here so they can be tested without opening a window.
public sealed class FormatDialogViewModel : ObservableObject
{
    string confirmText = "";
    string label = "";
    bool limitContent;
    string contentGigabytes = "";
    ClusterChoice cluster;
    bool compatOriginalStyle;
    bool compatOnly;

    public FormatDialogViewModel(FormatDialogInfo info)
    {
        Info = info;
        WholeImage = !info.LayoutFits;

        foreach (var spec in HddLayout.Partitions(info.Scan.VisibleLength).Where(p => p.IsFatx && info.LayoutFits))
        {
            if (spec.Length < 0x1000000)
                continue;
            var detected = info.Scan.Partitions.FirstOrDefault(p => p.Id == spec.Id);
            if (spec.Id == HddLayout.CompatId)
                CompatIsFormatted = detected is { CanMount: true };

            // Content and the compatibility partition are what a drive is formatted for. The two system
            // partitions are only ticked when they aren't there yet, an existing one is best left alone.
            bool ticked = spec.Id is HddLayout.ContentId or HddLayout.CompatId
                || detected is null || !detected.CanMount;
            Choices.Add(new PartitionChoice(spec.Id, spec.Name, spec.Description, spec.Length, ticked));
        }

        ClusterChoices = new[]
        {
            new ClusterChoice("Automatic (16 KB)", 0),
            new ClusterChoice("16 KB", 0x20),
            new ClusterChoice("32 KB", 0x40),
            new ClusterChoice("64 KB", 0x80),
        };
        cluster = ClusterChoices[0];
        foreach (var c in Choices)
            c.PropertyChanged += (_, _) => Raise(nameof(CanFormat));
    }

    public FormatDialogInfo Info { get; }
    public bool WholeImage { get; }
    public List<PartitionChoice> Choices { get; } = new();
    public IReadOnlyList<ClusterChoice> ClusterChoices { get; }

    // true when the drive already has a formatted compatibility partition
    public bool CompatIsFormatted { get; }

    // the two ways to format are only offered for a real layout
    public bool ShowModes => !WholeImage;

    public bool CompatOnly
    {
        get => compatOnly;
        set
        {
            if (!Set(ref compatOnly, value))
                return;
            Raise(nameof(FullFormat));
            Raise(nameof(ShowChoices));
            Raise(nameof(ShowContentOptions));
            Raise(nameof(Warning));
            Raise(nameof(CompatNote));
            Raise(nameof(CanFormat));
        }
    }

    // the other half of the pair of radio buttons
    public bool FullFormat
    {
        get => !compatOnly;
        set => CompatOnly = !value;
    }

    public bool ShowChoices => !WholeImage && !compatOnly;

    // cluster size, label and the limit on Content: all about the Content partition
    public bool ShowContentOptions => !compatOnly;
    public bool ShowAdvanced => !WholeImage;

    public string CompatNote => compatOnly
        ? CompatIsFormatted
            ? "This drive has an Xbox 1 Compatibility partition. It will be erased and made again."
            : "This drive has no Xbox 1 Compatibility partition yet. It will be created."
        : "";

    public string Heading => Info.Disk is { } d
        ? $"Format {d.Display}"
        : $"Format {Info.Title}";

    // what has to be typed before the Format button is enabled
    public string ConfirmationPhrase => Info.Disk is { } d ? DiskSafety.ConfirmationPhrase(d) : "format";

    public string Warning => WholeImage
        ? "The whole image will be erased and turned into one empty FATX volume."
        : compatOnly
            ? "Only the Xbox 1 Compatibility partition is touched. Everything else on the drive stays as it is, and so does its security sector."
            : "Everything on the partitions you tick will be erased. Other partitions are left alone, and so is the drive's security sector.";

    public string ConfirmText
    {
        get => confirmText;
        set
        {
            if (Set(ref confirmText, value))
                Raise(nameof(CanFormat));
        }
    }

    public string Label
    {
        get => label;
        set
        {
            if (Set(ref label, value))
            {
                Raise(nameof(LabelProblem));
                Raise(nameof(CanFormat));
            }
        }
    }

    public string? LabelProblem => !compatOnly && label.Length > 15 ? "A label can be 15 characters at most." : null;

    public bool LimitContent
    {
        get => limitContent;
        set
        {
            if (Set(ref limitContent, value))
                Raise(nameof(CanFormat));
        }
    }

    public string ContentGigabytes
    {
        get => contentGigabytes;
        set
        {
            if (Set(ref contentGigabytes, value))
            {
                Raise(nameof(CanFormat));
                Raise(nameof(ContentSizeProblem));
            }
        }
    }

    public string? ContentSizeProblem
    {
        get
        {
            if (compatOnly || !limitContent || string.IsNullOrWhiteSpace(contentGigabytes))
                return null;
            if (!TryGigabytes(out long bytes))
                return "Type a number of gigabytes.";
            var content = Choices.FirstOrDefault(c => c.Id == HddLayout.ContentId);
            if (content is not null && bytes > content.Length)
                return $"That's more than the {content.SizeText} available.";
            if (bytes < 0x1000000)
                return "That's too small.";
            return null;
        }
    }

    public ClusterChoice Cluster
    {
        get => cluster;
        set => Set(ref cluster, value);
    }

    public bool CompatOriginalStyle
    {
        get => compatOriginalStyle;
        set => Set(ref compatOriginalStyle, value);
    }

    public bool CanFormat =>
        string.Equals(confirmText.Trim(), ConfirmationPhrase, StringComparison.OrdinalIgnoreCase)
        && LabelProblem is null
        && ContentSizeProblem is null
        && (WholeImage || compatOnly || Choices.Any(c => c.IsSelected))
        && (compatOnly || !limitContent || !string.IsNullOrWhiteSpace(contentGigabytes));

    public FormatRequest? BuildRequest()
    {
        if (!CanFormat)
            return null;
        var flavor = compatOriginalStyle ? FatxFlavor.Xbox : FatxFlavor.Xbox360;
        if (compatOnly)
            return new FormatRequest(false, new[] { HddLayout.CompatId }, 0, 0, null, flavor);

        long contentLength = 0;
        if (limitContent && TryGigabytes(out long bytes))
            contentLength = bytes;
        return new FormatRequest(
            WholeImage,
            Choices.Where(c => c.IsSelected).Select(c => c.Id).ToList(),
            cluster.Sectors,
            contentLength,
            string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            flavor);
    }

    bool TryGigabytes(out long bytes)
    {
        bytes = 0;
        if (!double.TryParse(contentGigabytes.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double gb) || gb <= 0)
            return false;
        bytes = (long)(gb * (1L << 30)) / 512 * 512;
        return true;
    }
}

// The window that asks where a backup goes
public sealed class BackupDialogViewModel : ObservableObject
{
    string filePath;
    bool verify = true;
    DestinationCheck check;

    public BackupDialogViewModel(BackupDialogInfo info, string startFolder)
    {
        Info = info;
        filePath = System.IO.Path.Combine(startFolder, info.SuggestedName);
        check = DriveBackup.CheckDestination(filePath, info.Disk.Length);
    }

    public BackupDialogInfo Info { get; }

    public string Heading => $"Back up {Info.Disk.Display}";

    public string FilePath
    {
        get => filePath;
        set
        {
            if (!Set(ref filePath, value))
                return;
            check = DriveBackup.CheckDestination(filePath, Info.Disk.Length);
            Raise(nameof(Problem));
            Raise(nameof(Warning));
            Raise(nameof(SpaceText));
            Raise(nameof(SizeText));
            Raise(nameof(CanCreate));
        }
    }

    public bool Verify
    {
        get => verify;
        set => Set(ref verify, value);
    }

    public string? Problem => string.IsNullOrWhiteSpace(filePath) ? "Say where to save it." : check.Problem;
    public string? Warning => check.Warning;

    public string SpaceText => check.FreeBytes > 0 ? $"{Size.Format(check.FreeBytes)} free there." : "";

    public string SizeText => $"The drive is {Size.Format(Info.Disk.Length)}. " + (check.Sparse
        ? "Empty parts of it aren't stored, so the file takes less room than that."
        : "The file takes all of that, this disk can't leave out the empty parts.");

    public bool CanCreate => Problem is null;

    public BackupRequest? BuildRequest() =>
        CanCreate ? new BackupRequest(System.IO.Path.GetFullPath(filePath), verify) : null;
}

// One line in the restore window's list of drives, and why it can't be picked if it can't
public sealed class RestoreTarget
{
    public RestoreTarget(PhysicalDiskInfo disk, string? problem)
    {
        Disk = disk;
        Problem = problem;
    }

    public PhysicalDiskInfo Disk { get; }
    public string? Problem { get; }
    public bool Enabled => Problem is null;
    public string Title => Disk.Display;
    public string Note => Problem ?? "";
}

// The window that asks which backup goes onto which drive. The disk can't be picked by accident: the
// risky ones are greyed out, and the number has to be typed before the button works.
public sealed class RestoreDialogViewModel : ObservableObject
{
    readonly RestoreDialogInfo info;
    string backupPath;
    long backupLength;
    long backupVisible;
    string? backupProblem;
    bool verify = true;
    bool keepHeader;
    RestoreTarget? selected;
    string confirmText = "";

    public RestoreDialogViewModel(RestoreDialogInfo info)
    {
        this.info = info;
        backupPath = info.BackupPath ?? "";
        Targets = new List<RestoreTarget>();
        LookAtBackup();
    }

    public List<RestoreTarget> Targets { get; private set; }

    public string BackupPath
    {
        get => backupPath;
        set
        {
            if (!Set(ref backupPath, value))
                return;
            LookAtBackup();
            Raise(nameof(Targets));
            Raise(nameof(BackupProblem));
            Raise(nameof(BackupNote));
            Raise(nameof(SelectedTarget));
            RaiseKeep();
            Raise(nameof(CanRestore));
        }
    }

    public string? BackupProblem => backupProblem;

    // what is known about the backup file, shown under its name
    public string BackupNote
    {
        get
        {
            if (backupProblem is not null)
                return "";
            string size = Size.Format(backupLength);
            return DriveBackup.ReadChecksum(backupPath) is not null
                ? $"{size}. Its checksum file was found, so it will be checked for damage before anything is written."
                : $"{size}. There's no checksum file beside it, so it can't be checked for damage first.";
        }
    }

    public bool Verify
    {
        get => verify;
        set => Set(ref verify, value);
    }

    public RestoreTarget? SelectedTarget
    {
        get => selected;
        set
        {
            if (value is { Enabled: false })
                value = null;
            if (!Set(ref selected, value))
                return;
            Raise(nameof(ConfirmationPhrase));
            RaiseKeep();
            Raise(nameof(CanRestore));
        }
    }

    // Leave the drive's own first 512 KB, its security sector, as it is. Only possible when the drive has
    // one and the console sees it at the same size as the drive the backup came from.
    public bool KeepTargetHeader
    {
        get => keepHeader;
        set
        {
            if (value && !CanKeepHeader)
                value = false;
            if (Set(ref keepHeader, value))
                Raise(nameof(Warning));
        }
    }

    public bool CanKeepHeader =>
        backupProblem is null && selected is { Enabled: true } target
        && SecuritySectors.KeepProblem(backupVisible, SectorOf(target)) is null;

    public string KeepHeaderNote
    {
        get
        {
            if (selected is null)
                return "Pick a drive to see whether it can keep its own.";
            if (backupProblem is not null)
                return "";
            var own = SectorOf(selected);
            return SecuritySectors.KeepProblem(backupVisible, own)
                ?? $"This drive has its own security sector (serial {own!.Serial}) and the console sees it at the same size as the original, so it can keep it.";
        }
    }

    SecuritySector? SectorOf(RestoreTarget target) =>
        info.SecuritySectors is { } all && all.TryGetValue(target.Disk.Number, out var sector) ? sector : null;

    void RaiseKeep()
    {
        if (keepHeader && !CanKeepHeader)
            keepHeader = false;
        Raise(nameof(KeepTargetHeader));
        Raise(nameof(CanKeepHeader));
        Raise(nameof(KeepHeaderNote));
        Raise(nameof(Warning));
    }

    public string ConfirmationPhrase => selected is null ? "the disk number" : DiskSafety.ConfirmationPhrase(selected.Disk);

    public string Warning
    {
        get
        {
            string what = selected is null ? "the drive you pick" : selected.Disk.Display;
            return keepHeader
                ? $"Everything on {what} except its own security sector (the first 512 KB) will be overwritten with the backup. That can't be undone."
                : $"Everything on {what} will be overwritten with the backup. That can't be undone.";
        }
    }

    public string ConfirmText
    {
        get => confirmText;
        set
        {
            if (Set(ref confirmText, value))
                Raise(nameof(CanRestore));
        }
    }

    public bool CanRestore =>
        backupProblem is null
        && selected is { Enabled: true } target
        && DiskSafety.IsConfirmed(target.Disk, confirmText);

    public RestoreRequest? BuildRequest() =>
        CanRestore ? new RestoreRequest(backupPath, selected!.Disk, verify, keepHeader && CanKeepHeader) : null;

    // sizes the backup up and works out which drives it could go on
    void LookAtBackup()
    {
        backupLength = 0;
        backupVisible = 0;
        backupProblem = null;
        if (string.IsNullOrWhiteSpace(backupPath))
            backupProblem = "Pick the backup file.";
        else if (!File.Exists(backupPath))
            backupProblem = "That file doesn't exist.";
        else
        {
            backupLength = new System.IO.FileInfo(backupPath).Length;
            if (backupLength == 0)
                backupProblem = "That file is empty.";
            else
                backupVisible = VisibleLengthOf(backupPath);
        }

        // the drives that can be used come first, the greyed out ones are only there so nobody wonders where a disk went
        Targets = info.Disks
            .Select(d => new RestoreTarget(d, ProblemWith(d)))
            .OrderByDescending(t => t.Enabled)
            .ThenBy(t => t.Disk.Number)
            .ToList();
        if (selected is not null)
        {
            // keep the pick if it's still a good one
            var again = Targets.FirstOrDefault(t => t.Disk.Number == selected.Disk.Number);
            selected = again is { Enabled: true } ? again : null;
        }
    }

    // how big the console would see the drive this backup came from, which its security sector says
    static long VisibleLengthOf(string path)
    {
        try
        {
            using var image = new FileBlockDevice(path, writable: false);
            return HddLayout.VisibleLength(image);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    string? ProblemWith(PhysicalDiskInfo disk)
    {
        if (disk.IsSystem)
            return "Windows is running from this disk";
        if (disk.HasVolumes)
            return "It has drive letters, so it looks like a PC disk";
        if (info.InUse.Contains($"disk:{disk.Number}"))
            return "Partitions from it are mounted, unmount them first";
        if (backupLength > disk.Length)
            return $"Too small, the backup is {Size.Format(backupLength)}";
        return null;
    }
}
