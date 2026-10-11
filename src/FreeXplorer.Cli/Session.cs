using FreeXplorer.Fatx;
using FreeXplorer.Xbox360;

namespace FreeXplorer.Cli;

// A target, its detected partitions, and one opened volume
sealed class Session : IDisposable
{
    Session(Target target, DriveScan scan, DetectedPartition partition, FatxVolume volume)
    {
        Target = target;
        Scan = scan;
        Partition = partition;
        Volume = volume;
    }

    public Target Target { get; }
    public DriveScan Scan { get; }
    public DetectedPartition Partition { get; }
    public FatxVolume Volume { get; }

    public static Session Open(string targetSpec, string? partitionName, bool write, bool allowPcDisk = false)
    {
        var target = Target.Open(targetSpec, write, allowPcDisk);
        try
        {
            var scan = DriveScanner.Scan(target.Device);
            var partition = Target.Pick(scan, partitionName);
            var volume = DriveScanner.OpenPartition(target.Device, partition, write);
            if (write)
                volume.ScanFreeSpace();
            return new Session(target, scan, partition, volume);
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Volume.Dispose();
        Target.Dispose();
    }

    // The first positional after the target may be a partition name or already the path.
    // Returns the partition name (or null) and moves on to the rest.
    public static (string? Partition, List<string> Remaining) SplitPartition(string targetSpec, List<string> positional)
    {
        if (positional.Count == 0)
            return (null, positional);

        string first = positional[0];
        bool looksLikePath = first.Contains('\\') || first.Contains('/') || first.Contains('.') || first.Contains(':');
        if (!looksLikePath && IsPartitionName(first))
            return (first, positional.Skip(1).ToList());
        return (null, positional);
    }

    static bool IsPartitionName(string text)
    {
        if (text.Equals(DriveScanner.SinglePartitionId, StringComparison.OrdinalIgnoreCase))
            return true;
        return HddLayout.Normalize(text) is HddLayout.ContentId or HddLayout.CompatId or HddLayout.SystemExtId
            or HddLayout.SystemExt2Id or HddLayout.SystemCacheId or HddLayout.GameCacheId;
    }
}
