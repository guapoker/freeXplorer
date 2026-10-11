using System.Text.RegularExpressions;
using FreeXplorer.Fatx;
using FreeXplorer.Storage;
using FreeXplorer.Xbox360;

namespace FreeXplorer.Cli;

// What the user pointed us at: a physical disk or an image file
sealed partial class Target : IDisposable
{
    Target(IBlockDevice device, string description, PhysicalDiskInfo? disk, bool writable)
    {
        Device = device;
        Description = description;
        Disk = disk;
        Writable = writable;
    }

    public IBlockDevice Device { get; }
    public string Description { get; }
    public PhysicalDiskInfo? Disk { get; }
    public bool Writable { get; }
    public bool IsPhysical => Disk is not null;

    [GeneratedRegex(@"^(?:disk:|\\\\\.\\physicaldrive)(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiskPattern();

    public static bool TryParseDisk(string spec, out int number)
    {
        var m = DiskPattern().Match(spec.Trim());
        number = m.Success ? int.Parse(m.Groups[1].Value) : -1;
        return m.Success;
    }

    public static Target Open(string spec, bool writable, bool allowPcDisk = false)
    {
        if (TryParseDisk(spec, out int number))
        {
            var info = PhysicalDisks.List().FirstOrDefault(d => d.Number == number)
                ?? throw new FileNotFoundException($"there's no disk {number}. Run \"list\" to see what's connected.");
            if (writable)
            {
                var verdict = DiskSafety.CanWrite(info, allowPcDisk);
                if (!verdict.Allowed)
                    throw new InvalidOperationException(verdict.Reason);
            }
            return new Target(PhysicalDiskDevice.Open(number, writable), info.Display, info, writable);
        }

        if (!File.Exists(spec))
            throw new FileNotFoundException($"can't find \"{spec}\". Give a disk like disk:3 or the path of an image file.");
        var device = new FileBlockDevice(spec, writable);
        return new Target(device, device.Name, null, writable);
    }

    // The partition the user asked for, or the obvious one if they didn't say
    public static DetectedPartition Pick(DriveScan scan, string? name)
    {
        if (name is null)
        {
            var mountable = scan.Partitions.Where(p => p.CanMount).ToList();
            if (mountable.Count == 1)
                return mountable[0];
            throw new UsageException(mountable.Count == 0
                ? "there are no FATX partitions here to open"
                : "say which partition: " + string.Join(", ", mountable.Select(p => p.Id)));
        }

        string id = name.Equals(DriveScanner.SinglePartitionId, StringComparison.OrdinalIgnoreCase)
            ? DriveScanner.SinglePartitionId
            : HddLayout.Normalize(name);
        var found = scan.Partitions.FirstOrDefault(p => p.Id == id)
            ?? throw new UsageException($"there's no partition called \"{name}\" here. Try: {string.Join(", ", scan.Partitions.Select(p => p.Id))}");
        if (!found.CanMount)
            throw new UsageException(found.Spec.IsFatx
                ? $"{found.Name} has no FATX volume on it yet. Format it first."
                : $"{found.Name} isn't a FATX partition and can't be opened.");
        return found;
    }

    public void Dispose() => Device.Dispose();
}

static class SizeText
{
    public static long Parse(string text)
    {
        text = text.Trim().Replace(" ", "");
        long scale = 1;
        char last = char.ToUpperInvariant(text[^1]);
        string digits = text;
        if (last is 'K' or 'M' or 'G' or 'T' or 'B')
        {
            digits = text[..^1];
            if (last == 'B' && digits.Length > 0 && char.ToUpperInvariant(digits[^1]) is 'K' or 'M' or 'G' or 'T')
            {
                last = char.ToUpperInvariant(digits[^1]);
                digits = digits[..^1];
            }
            scale = last switch
            {
                'K' => 1L << 10,
                'M' => 1L << 20,
                'G' => 1L << 30,
                'T' => 1L << 40,
                _ => 1,
            };
        }
        if (!double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) || value <= 0)
            throw new UsageException($"\"{text}\" isn't a size. Use something like 64M, 8G or 1.5T.");
        return (long)(value * scale);
    }

    public static int ClusterSectors(string text)
    {
        long bytes = Parse(text);
        if (bytes is < 4096 or > 65536 || (bytes & (bytes - 1)) != 0)
            throw new UsageException("cluster size has to be 4K, 8K, 16K, 32K or 64K");
        return (int)(bytes / 512);
    }
}
