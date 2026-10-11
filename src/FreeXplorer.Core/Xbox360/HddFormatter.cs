using FreeXplorer.Fatx;
using FreeXplorer.Storage;

namespace FreeXplorer.Xbox360;

public sealed class HddFormatOptions
{
    // 0 picks a cluster size from the partition size (16K for anything under 4 TB)
    public int ContentSectorsPerCluster { get; init; }

    public string? ContentLabel { get; init; }

    // Content can be made shorter than the rest of the drive, 0 means all of it
    public long ContentLength { get; init; }

    // the 360 itself seems to format the compatibility partition like the others,
    // but the original Xbox style is available in case it turns out to matter
    public FatxFlavor CompatFlavor { get; init; } = FatxFlavor.Xbox360;
}

public sealed record FormatStep(PartitionSpec Partition, int SectorsPerCluster, FatxGeometry Geometry);

public static class HddFormatter
{
    // Works out what would be written without writing anything
    public static IReadOnlyList<FormatStep> Plan(IBlockDevice device, IEnumerable<string> partitionIds, HddFormatOptions? options = null)
    {
        options ??= new HddFormatOptions();
        long visible = HddLayout.VisibleLength(device);
        var steps = new List<FormatStep>();

        foreach (var raw in partitionIds.Select(HddLayout.Normalize).Distinct())
        {
            var spec = HddLayout.Find(raw, visible)
                ?? throw new ArgumentException($"there's no partition called \"{raw}\"");
            if (!spec.IsFatx)
                throw new ArgumentException($"{spec.Name} isn't a FATX partition, there's nothing to format there");

            if (spec.Id == HddLayout.ContentId && options.ContentLength > 0)
                spec = spec with { Length = Math.Min(spec.Length, options.ContentLength / 512 * 512) };

            if (spec.Length < 0x1000000)
                throw new ArgumentException($"the drive is too small, {spec.Name} would only get {spec.Length} bytes");
            if (spec.End > device.Length)
                throw new ArgumentException($"{spec.Name} would end at 0x{spec.End:X} but the drive is only 0x{device.Length:X} bytes");

            int spc = spec.Id == HddLayout.ContentId && options.ContentSectorsPerCluster != 0
                ? options.ContentSectorsPerCluster
                : spec.Id == HddLayout.ContentId
                    ? FatxGeometry.PickSectorsPerCluster(spec.Length)
                    : 0x20;
            steps.Add(new FormatStep(spec, spc, FatxGeometry.Compute(spec.Length, spc)));
        }
        return steps;
    }

    public static void Format(IBlockDevice device, IEnumerable<string> partitionIds, HddFormatOptions? options = null,
        Action<FormatStep, double>? progress = null)
    {
        options ??= new HddFormatOptions();
        var steps = Plan(device, partitionIds, options);
        foreach (var step in steps)
        {
            // never touch the first 512 KB of the drive, that's where the security sector lives
            if (step.Partition.Offset < HddLayout.SystemCacheOffset)
                throw new InvalidOperationException("refusing to write into the drive header area");

            var flavor = step.Partition.Id == HddLayout.CompatId ? options.CompatFlavor : FatxFlavor.Xbox360;
            FatxFormatter.Format(device, step.Partition.Offset, step.Partition.Length, new FatxFormatOptions
            {
                Flavor = flavor,
                SectorsPerCluster = step.SectorsPerCluster,
                Label = step.Partition.Id == HddLayout.ContentId ? options.ContentLabel : null,
            }, p => progress?.Invoke(step, p));
            progress?.Invoke(step, 1);
        }
    }
}
