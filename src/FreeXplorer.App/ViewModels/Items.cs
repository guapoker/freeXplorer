using FreeXplorer.Storage;
using FreeXplorer.Xbox360;

namespace FreeXplorer.App.ViewModels;

public enum SourceKind
{
    Disk,
    Image,
}

// A physical disk or an image file in the list on the left
public sealed class SourceItem : ObservableObject
{
    public SourceItem(PhysicalDiskInfo disk)
    {
        Kind = SourceKind.Disk;
        Disk = disk;
        Key = $"disk:{disk.Number}";
        Title = $"Disk {disk.Number}";
        Subtitle = $"{(string.IsNullOrWhiteSpace(disk.Model) ? "Unknown drive" : disk.Model)}  ·  {Size.Format(disk.Length)}  ·  {disk.Bus}";
        Note = disk.IsSystem ? "Windows is running from this disk"
            : disk.HasVolumes ? "Has drive letters, probably a PC disk"
            : null;
    }

    public SourceItem(string imagePath)
    {
        Kind = SourceKind.Image;
        ImagePath = Path.GetFullPath(imagePath);
        Key = ImagePath;
        Title = Path.GetFileName(ImagePath);
        long length = File.Exists(ImagePath) ? new System.IO.FileInfo(ImagePath).Length : 0;
        Subtitle = $"Backup file  ·  {Size.Format(length)}";
        Note = null;
    }

    public SourceKind Kind { get; }
    public PhysicalDiskInfo? Disk { get; }
    public string? ImagePath { get; }
    public string Key { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string? Note { get; }
    public bool HasNote => Note is not null;

    internal IBlockDevice? Reader { get; set; }
    internal DriveScan? Scan { get; set; }
    internal List<PartitionItem>? Items { get; set; }
}

public sealed class PartitionItem : ObservableObject
{
    string? mountPoint;
    bool mountedReadOnly;
    string usage = "";

    public PartitionItem(DetectedPartition detected, string format, string? label)
    {
        Detected = detected;
        Format = format;
        Label = label;
    }

    internal DetectedPartition Detected { get; }

    public string Id => Detected.Id;
    public string Name => Detected.Name;
    public string OffsetText => $"0x{Detected.Offset:X}";
    public string SizeText => Size.Format(Detected.Length);
    public string Format { get; }
    public string? Label { get; }
    public bool CanMount => Detected.CanMount;

    public string? MountPoint
    {
        get => mountPoint;
        set
        {
            if (Set(ref mountPoint, value))
            {
                Raise(nameof(IsMounted));
                Raise(nameof(Status));
                Raise(nameof(StatusDetail));
            }
        }
    }

    public bool MountedReadOnly
    {
        get => mountedReadOnly;
        set
        {
            if (Set(ref mountedReadOnly, value))
                Raise(nameof(Status));
        }
    }

    public string Usage
    {
        get => usage;
        set => Set(ref usage, value);
    }

    public bool IsMounted => mountPoint is not null;

    // shown as a tooltip, the full story where Status has to be short
    public string StatusDetail => mountPoint is { Length: > 3 } ? $"{Status}: {mountPoint}" : Status;

    public string Status
    {
        get
        {
            if (mountPoint is not null)
            {
                // a drive letter is short enough to show, a folder path isn't (see StatusDetail)
                string where = mountPoint.Length <= 3 ? $"as {mountPoint}" : "in a folder";
                return $"Mounted {where} ({(mountedReadOnly ? "read-only" : "read/write")})";
            }
            if (!Detected.Spec.IsFatx)
                return "Cache, not browsable";
            if (!Detected.HasSignature)
                return "Not formatted";
            return "Ready to mount";
        }
    }
}
