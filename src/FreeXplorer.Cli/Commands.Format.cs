using FreeXplorer.Fatx;
using FreeXplorer.Storage;
using FreeXplorer.Xbox360;

namespace FreeXplorer.Cli;

static partial class Commands
{
    static readonly HashSet<string> FormatValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "partitions", "label", "cluster-size", "content-size", "confirm", "compat-flavor", "size",
    };

    public static int Format(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, FormatValues);
        a.RejectUnknown("all", "partitions", "whole", "label", "cluster-size", "content-size", "dry-run",
            "yes", "confirm", "allow-pc-disk", "compat-flavor");
        string spec = a.Require(0, "target");

        bool whole = a.Has("whole");
        bool layout = a.Has("all") || a.Has("partitions");
        if (whole == layout)
            throw new UsageException("say what to format: --all (the four Xbox 360 partitions), --partitions content,compat,... or --whole (one volume over the entire image)");
        if (whole && Target.TryParseDisk(spec, out _))
            throw new UsageException("--whole is only for image files. A physical disk keeps the Xbox 360 layout.");

        bool dryRun = a.Has("dry-run");
        using var target = Target.Open(spec, writable: !dryRun, allowPcDisk: a.Has("allow-pc-disk"));

        int clusterSectors = a.Has("cluster-size") ? SizeText.ClusterSectors(a.Get("cluster-size")!) : 0;
        string? label = a.Get("label");
        if (label is { Length: > 15 })
            throw new UsageException("labels can be 15 characters at most");

        ctx.Out.WriteLine(target.Description);
        List<Action<Action<double>>> jobs = new();

        if (whole)
        {
            long length = target.Device.Length / 512 * 512;
            int spc = clusterSectors != 0 ? clusterSectors : FatxGeometry.PickSectorsPerCluster(length);
            var geometry = FatxGeometry.Compute(length, spc);
            ctx.Out.WriteLine($"  whole image  {Size.Format(length)}, {geometry.ClusterSize / 1024} KB clusters, FAT{geometry.FatEntrySize * 8}");
            jobs.Add(progress => FatxFormatter.Format(target.Device, 0, length,
                new FatxFormatOptions { SectorsPerCluster = spc, Label = label }, progress));
        }
        else
        {
            var ids = a.Has("all")
                ? new[] { HddLayout.SystemExtId, HddLayout.SystemExt2Id, HddLayout.CompatId, HddLayout.ContentId }
                : a.Get("partitions")!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var options = new HddFormatOptions
            {
                ContentSectorsPerCluster = clusterSectors,
                ContentLabel = label,
                ContentLength = a.Has("content-size") ? SizeText.Parse(a.Get("content-size")!) : 0,
                CompatFlavor = ParseFlavor(a.Get("compat-flavor")),
            };

            IReadOnlyList<FormatStep> plan;
            try
            {
                plan = HddFormatter.Plan(target.Device, ids, options);
            }
            catch (ArgumentException ex)
            {
                throw new UsageException(ex.Message);
            }
            foreach (var step in plan)
                ctx.Out.WriteLine($"  {step.Partition.Name,-22} 0x{step.Partition.Offset:X} {Size.Format(step.Partition.Length),9}, " +
                    $"{step.Geometry.ClusterSize / 1024} KB clusters, FAT{step.Geometry.FatEntrySize * 8}");
            jobs.Add(progress => HddFormatter.Format(target.Device, ids, options, (_, p) => progress(p)));
        }

        if (dryRun)
        {
            ctx.Out.WriteLine("Dry run, nothing was written.");
            return 0;
        }

        ctx.Out.WriteLine("Everything on the partitions listed above will be erased.");
        if (!Confirm(ctx, a, target))
        {
            ctx.Out.WriteLine("Not confirmed, nothing was changed.");
            return 1;
        }

        double lastShown = -1;
        foreach (var job in jobs)
            job(p =>
            {
                if (p - lastShown >= 0.25 || p >= 1)
                {
                    lastShown = p >= 1 ? -1 : p;
                    ctx.Out.WriteLine($"  {p:P0}");
                }
            });
        target.Device.Flush();
        ctx.Out.WriteLine("Formatted.");
        return 0;
    }

    public static int NewImage(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, FormatValues);
        a.RejectUnknown("size", "partition", "label", "cluster-size");
        string path = a.Require(0, "file name for the new image");
        if (!a.Has("size"))
            throw new UsageException("how big? for example --size 8G");
        if (File.Exists(path))
            throw new UsageException($"{path} already exists, not overwriting it");

        long size = SizeText.Parse(a.Get("size")!) / 512 * 512;
        bool single = a.Has("partition");
        if (!single && size < HddLayout.MinimumDriveLength)
            throw new UsageException(
                $"a full Xbox 360 layout needs at least {Size.Format(HddLayout.MinimumDriveLength)}. Make it bigger, or add --partition for a single volume.");

        int clusterSectors = a.Has("cluster-size") ? SizeText.ClusterSectors(a.Get("cluster-size")!) : 0;
        using var device = FileBlockDevice.CreateImage(path, size);
        try
        {
            if (single)
            {
                FatxFormatter.Format(device, 0, size, new FatxFormatOptions { SectorsPerCluster = clusterSectors, Label = a.Get("label") });
            }
            else
            {
                HddFormatter.Format(device,
                    new[] { HddLayout.SystemExtId, HddLayout.SystemExt2Id, HddLayout.CompatId, HddLayout.ContentId },
                    new HddFormatOptions { ContentSectorsPerCluster = clusterSectors, ContentLabel = a.Get("label") });
            }
            device.Flush();
        }
        catch
        {
            device.Dispose();
            File.Delete(path);
            throw;
        }
        ctx.Out.WriteLine($"created {device.Name} ({Size.Format(size)}{(single ? ", one FATX volume" : ", Xbox 360 layout")})");
        return 0;
    }

    static FatxFlavor ParseFlavor(string? text) => text?.ToLowerInvariant() switch
    {
        null or "xbox360" or "360" or "xtaf" => FatxFlavor.Xbox360,
        "xbox1" or "xbox" or "fatx" => FatxFlavor.Xbox,
        _ => throw new UsageException("--compat-flavor is xbox360 or xbox1"),
    };

    static bool Confirm(CliContext ctx, ParsedArgs a, Target target)
    {
        if (target.Disk is { } disk)
        {
            string phrase = DiskSafety.ConfirmationPhrase(disk);
            string? typed = a.Get("confirm");
            if (typed is null)
            {
                ctx.Out.Write($"This is a real disk: {disk.Display}. Type \"{phrase}\" to go ahead: ");
                typed = ctx.In.ReadLine();
            }
            return DiskSafety.IsConfirmed(disk, typed);
        }

        if (a.Has("yes"))
            return true;
        ctx.Out.Write("Type yes to go ahead: ");
        return string.Equals(ctx.In.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }
}
