namespace FreeXplorer.Storage;

public sealed record SafetyVerdict(bool Allowed, string? Reason)
{
    public static SafetyVerdict Ok { get; } = new(true, null);
    public static SafetyVerdict Blocked(string reason) => new(false, reason);
}

// The rules for letting this program write to a physical disk. Reading is always fine.
public static class DiskSafety
{
    public static SafetyVerdict CanWrite(PhysicalDiskInfo disk, bool allowPcDisk = false)
    {
        if (disk.IsSystem)
            return SafetyVerdict.Blocked(OperatingSystem.IsWindows()
                ? $"Disk {disk.Number} is the disk Windows is running from. This program will never write to it."
                : $"Disk {disk.Number} ({disk.Path}) is the disk the system is running from. This program will never write to it.");
        if (disk.HasVolumes && !allowPcDisk && !OperatingSystem.IsWindows())
            return SafetyVerdict.Blocked(
                $"Disk {disk.Number} ({disk.Path}) has partitions that are mounted or in use, so it looks like a normal PC disk and not an Xbox 360 drive. " +
                "If you're sure, unmount them and pass --allow-pc-disk to the command line tool.");
        if (disk.HasVolumes && !allowPcDisk)
            return SafetyVerdict.Blocked(
                $"Disk {disk.Number} has drive letters assigned, so it looks like a normal PC disk and not an Xbox 360 drive. " +
                "If you're sure, pass --allow-pc-disk to the command line tool.");
        return SafetyVerdict.Ok;
    }

    // what the user has to type before a destructive operation starts
    public static string ConfirmationPhrase(PhysicalDiskInfo disk) => $"disk {disk.Number}";

    public static bool IsConfirmed(PhysicalDiskInfo disk, string? typed) =>
        string.Equals(typed?.Trim(), ConfirmationPhrase(disk), StringComparison.OrdinalIgnoreCase);
}
