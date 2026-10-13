using FreeXplorer.Fatx;
using FreeXplorer.Storage;
using FreeXplorer.Xbox360;

namespace FreeXplorer.Cli;

static partial class Commands
{
    static readonly HashSet<string> NoValues = new();

    public static int List(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, NoValues);
        a.RejectUnknown("scan");

        var disks = PhysicalDisks.List();
        if (disks.Count == 0)
        {
            ctx.Out.WriteLine("No physical disks found.");
            return 0;
        }

        bool admin = PhysicalDisks.IsAdministrator();
        foreach (var d in disks)
        {
            var notes = new List<string>();
            if (d.IsSystem) notes.Add("system disk");
            else if (d.HasVolumes) notes.Add(OperatingSystem.IsWindows() ? "has drive letters" : "mounted");
            ctx.Out.WriteLine($"disk:{d.Number,-3} {d.Display}{(notes.Count > 0 ? "  [" + string.Join(", ", notes) + "]" : "")}");

            if (a.Has("scan") && admin && !d.IsSystem)
            {
                try
                {
                    using var dev = PhysicalDiskDevice.Open(d, writable: false);
                    var scan = DriveScanner.Scan(dev);
                    ctx.Out.WriteLine($"        {Describe(scan.Kind)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ctx.Out.WriteLine($"        couldn't read it: {ex.Message}");
                }
            }
        }
        string elevated = OperatingSystem.IsWindows() ? "from an administrator prompt" : "with sudo";
        if (a.Has("scan") && !admin)
            ctx.Out.WriteLine($"\nRun this {elevated} to look inside the disks.");
        else if (!a.Has("scan"))
            ctx.Out.WriteLine($"\nUse \"info disk:N\" to see what's on one, {elevated}.");
        return 0;
    }

    public static int Info(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, NoValues);
        a.RejectUnknown("usage");
        string spec = a.Require(0, "target (disk:N or an image file)");

        using var target = Target.Open(spec, writable: false);
        var scan = DriveScanner.Scan(target.Device);

        ctx.Out.WriteLine(target.Description);
        ctx.Out.WriteLine($"  {Describe(scan.Kind)}, {Size.Format(scan.VisibleLength)} visible to the console" +
            (scan.HasSecuritySector ? ", security sector present" : ""));
        ctx.Out.WriteLine();

        if (scan.Partitions.Count == 0)
        {
            ctx.Out.WriteLine("  Nothing recognisable here. If this is a blank drive, \"format\" can lay out the Xbox 360 partitions.");
            return 0;
        }

        ctx.Out.WriteLine($"  {"id",-10} {"name",-22} {"offset",-14} {"size",-10} state");
        foreach (var p in scan.Partitions)
        {
            string state = StateOf(target, p, a.Has("usage"));
            ctx.Out.WriteLine($"  {p.Id,-10} {p.Name,-22} 0x{p.Offset,-12:X} {Size.Format(p.Length),-10} {state}");
        }
        return 0;
    }

    public static int Fsck(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, NoValues);
        a.RejectUnknown("fix", "allow-pc-disk");
        string spec = a.Require(0, "target");
        var (partition, _) = Session.SplitPartition(spec, a.Positional.Skip(1).ToList());
        bool fix = a.Has("fix");

        using var session = Session.Open(spec, partition, write: fix, allowPcDisk: a.Has("allow-pc-disk"));
        ctx.Out.WriteLine($"checking {session.Partition.Name} ({Size.Format(session.Partition.Length)})...");
        var report = session.Volume.Check(freeOrphans: fix);
        session.Volume.Flush();

        foreach (var issue in report.Issues)
            ctx.Out.WriteLine($"  {issue.Severity.ToString().ToLowerInvariant(),-7} {issue.Message}");
        ctx.Out.WriteLine($"{report.Directories} folders, {report.Files} files, {Size.Format(report.FileBytes)} of data, " +
            $"{report.UsedClusters} clusters in use");
        if (report.FixedClusters > 0)
            ctx.Out.WriteLine($"released {report.FixedClusters} leaked clusters");

        if (report.Errors > 0)
        {
            ctx.Out.WriteLine("The volume has real damage. Copy your files off before doing anything else to it.");
            return 1;
        }
        ctx.Out.WriteLine(report.Warnings > 0 && !fix ? "No damage, but there's some leftover space that --fix can release." : "Clean.");
        return 0;
    }

    static string Describe(DriveKind kind) => kind switch
    {
        DriveKind.Xbox360Drive => "Xbox 360 drive",
        DriveKind.SinglePartition => "single FATX partition image",
        _ => "no Xbox 360 partitions found",
    };

    static string StateOf(Target target, DetectedPartition p, bool withUsage)
    {
        if (!p.Spec.IsFatx)
            return "console cache, not FATX";
        if (!p.CanMount)
            return "empty or not formatted";
        try
        {
            using var vol = DriveScanner.OpenPartition(target.Device, p, writable: false);
            string text = $"{(vol.Flavor == FatxFlavor.Xbox360 ? "XTAF" : "FATX")}, {vol.ClusterSize / 1024} KB clusters, FAT{vol.Geometry.FatEntrySize * 8}";
            if (vol.Label is { Length: > 0 } label)
                text += $", \"{label}\"";
            if (withUsage)
            {
                vol.ScanFreeSpace();
                text += $", {Size.Format(vol.TotalBytes - vol.FreeBytes)} used of {Size.Format(vol.TotalBytes)}";
            }
            return text;
        }
        catch (IOException ex)
        {
            return $"looks damaged ({ex.Message})";
        }
    }
}
