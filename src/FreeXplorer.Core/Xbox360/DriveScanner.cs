using FreeXplorer.Fatx;
using FreeXplorer.Storage;

namespace FreeXplorer.Xbox360;

public enum DriveKind
{
    // doesn't look like anything we know
    Unknown,
    // a whole 360 drive, at least one FATX partition is where the console would put it
    Xbox360Drive,
    // a dump of one FATX partition, signature right at the start
    SinglePartition,
}

public sealed class DetectedPartition
{
    public DetectedPartition(PartitionSpec spec, bool hasSignature, FatxFlavor? flavor)
    {
        Spec = spec;
        HasSignature = hasSignature;
        Flavor = flavor;
    }

    public PartitionSpec Spec { get; }
    public bool HasSignature { get; }
    public FatxFlavor? Flavor { get; }

    public string Id => Spec.Id;
    public string Name => Spec.Name;
    public long Offset => Spec.Offset;
    public long Length => Spec.Length;
    public bool CanMount => Spec.IsFatx && HasSignature;
}

public sealed class DriveScan
{
    public DriveScan(DriveKind kind, long visibleLength, bool hasSecuritySector, IReadOnlyList<DetectedPartition> partitions)
    {
        Kind = kind;
        VisibleLength = visibleLength;
        HasSecuritySector = hasSecuritySector;
        Partitions = partitions;
    }

    public DriveKind Kind { get; }
    public long VisibleLength { get; }
    public bool HasSecuritySector { get; }
    public IReadOnlyList<DetectedPartition> Partitions { get; }
}

public static class DriveScanner
{
    public const string SinglePartitionId = "image";

    public static DriveScan Scan(IBlockDevice device)
    {
        if (device.Length >= FatxGeometry.HeaderSize && TryGetSignature(device, 0, out var flavor0))
        {
            var spec = new PartitionSpec(SinglePartitionId, "Partition image", 0, device.Length / 512 * 512,
                PartitionFileSystem.Fatx, "A single FATX partition.");
            return new DriveScan(DriveKind.SinglePartition, spec.Length, false,
                new[] { new DetectedPartition(spec, true, flavor0) });
        }

        long visible = HddLayout.VisibleLength(device);
        bool security = HddLayout.LooksLikeSecuritySector(device);
        var found = new List<DetectedPartition>();
        bool anyFatx = false;

        foreach (var spec in HddLayout.Partitions(visible))
        {
            if (spec.Length <= 0 || spec.End > device.Length)
                continue;
            if (!spec.IsFatx)
            {
                found.Add(new DetectedPartition(spec, false, null));
                continue;
            }
            FatxFlavor? flavor = null;
            bool has = TryGetSignature(device, spec.Offset, out var f);
            if (has)
            {
                flavor = f;
                anyFatx = true;
            }
            found.Add(new DetectedPartition(spec, has, flavor));
        }

        return new DriveScan(anyFatx ? DriveKind.Xbox360Drive : DriveKind.Unknown, visible, security, found);
    }

    // Opens a detected partition as a volume.
    //
    // Content runs to the end of the drive, and where its FAT ends depends on exactly how
    // long it is. The console goes by the security sector's sector count, other tools might
    // use the whole disk. Guess wrong and every cluster is read from the wrong place, so
    // when the first guess doesn't produce a believable root directory we try the other.
    public static FatxVolume OpenPartition(IBlockDevice device, DetectedPartition partition, bool writable)
    {
        if (!partition.CanMount)
            throw new FatxException(FatxError.NotFatx, $"{partition.Name} has no FATX signature");

        long alternative = (device.Length - partition.Offset) / 512 * 512;
        bool canRetry = partition.Id == HddLayout.ContentId && alternative != partition.Length;

        FatxVolume? first = null;
        try
        {
            first = FatxVolume.Open(new SliceDevice(device, partition.Offset, partition.Length, writable), writable, ownsDevice: true);
            if (!canRetry || first.RootLooksSane())
                return first;
        }
        catch (FatxException) when (canRetry)
        {
        }

        try
        {
            var second = FatxVolume.Open(new SliceDevice(device, partition.Offset, alternative, writable), writable, ownsDevice: true);
            if (second.RootLooksSane() || first is null)
            {
                first?.Dispose();
                return second;
            }
            second.Dispose();
        }
        catch (FatxException) when (first is not null)
        {
        }
        return first!;
    }

    static bool TryGetSignature(IBlockDevice device, long offset, out FatxFlavor flavor)
    {
        flavor = default;
        if (offset < 0 || offset > device.Length - 4)
            return false;
        Span<byte> sig = stackalloc byte[4];
        device.Read(offset, sig);
        if (sig.SequenceEqual("XTAF"u8))
        {
            flavor = FatxFlavor.Xbox360;
            return true;
        }
        if (sig.SequenceEqual("FATX"u8))
        {
            flavor = FatxFlavor.Xbox;
            return true;
        }
        return false;
    }
}
