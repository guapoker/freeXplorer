using FreeXplorer.Storage;
using FreeXplorer.Xbox360;

namespace FreeXplorer.Cli;

static partial class Commands
{
    // backup <disk:N | image> <file> [--no-verify] [--no-sparse]
    public static int Backup(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, NoValues);
        a.RejectUnknown("no-verify", "no-sparse");
        string spec = a.Require(0, "drive to back up (disk:N or an image file)");
        string file = a.Require(1, "file to save the backup in");

        using var source = Target.Open(spec, writable: false);
        long length = source.Device.Length;

        var check = DriveBackup.CheckDestination(file, length);
        if (check.Problem is not null)
            throw new UsageException(check.Problem);

        ctx.Out.WriteLine($"{source.Description}, {Size.Format(length)}");
        ctx.Out.WriteLine($"saving to {Path.GetFullPath(file)}");
        if (check.Warning is not null)
            ctx.Out.WriteLine(check.Warning);

        var options = new BackupOptions(Verify: !a.Has("no-verify"), Sparse: !a.Has("no-sparse"));
        try
        {
            var result = DriveBackup.Create(source.Device, file, options, ConsoleProgress(ctx), ctx.Cancel);
            ctx.Out.WriteLine($"Done. {Size.Format(result.Length)} saved, taking {Size.Format(result.SizeOnDisk)} on disk{(result.Verified ? ", read back and checked" : "")}.");
            ctx.Out.WriteLine($"SHA-256 {result.Sha256}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            ctx.Out.WriteLine("Stopped, the partly written backup was deleted.");
            return 1;
        }
    }

    static readonly HashSet<string> RestoreValues = new(StringComparer.OrdinalIgnoreCase) { "confirm" };

    // restore <file> <disk:N | image> [--keep-header] [--no-verify] [--confirm "disk 4"] [--yes] [--allow-pc-disk]
    public static int Restore(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, RestoreValues);
        a.RejectUnknown("no-verify", "yes", "confirm", "allow-pc-disk", "keep-header");
        string file = a.Require(0, "backup file");
        string spec = a.Require(1, "drive to restore to (disk:N or an image file)");
        if (!File.Exists(file))
            throw new UsageException($"can't find the backup \"{file}\"");
        long length = new System.IO.FileInfo(file).Length;

        // restoring into an image file that isn't there yet just makes it
        bool fresh = !Target.TryParseDisk(spec, out _) && !File.Exists(spec);
        bool keep = a.Has("keep-header");
        if (keep && fresh)
            throw new UsageException("a new file has no security sector of its own to keep");
        if (fresh)
            FileBlockDevice.CreateImage(spec, length).Dispose();

        using var target = Target.Open(spec, writable: true, allowPcDisk: a.Has("allow-pc-disk"));
        bool started = false;
        try
        {
            if (length > target.Device.Length)
                throw new UsageException($"the backup is {Size.Format(length)} and {target.Description} is only {Size.Format(target.Device.Length)}. A drive has to be at least as big as the one that was backed up.");

            if (keep)
            {
                using var image = new FileBlockDevice(file, writable: false);
                if (SecuritySectors.KeepProblem(image, target.Device) is { } problem)
                    throw new UsageException(problem);
            }

            ctx.Out.WriteLine($"{target.Description}, {Size.Format(target.Device.Length)}");
            if (!fresh)
            {
                ctx.Out.WriteLine(keep
                    ? $"Everything on it except its own first {Size.Format(SecuritySectors.HeaderLength)} (the security sector) will be overwritten with {Path.GetFullPath(file)} ({Size.Format(length)})."
                    : $"Everything on it will be overwritten with {Path.GetFullPath(file)} ({Size.Format(length)}).");
                if (!Confirm(ctx, a, target))
                {
                    ctx.Out.WriteLine("Not confirmed, nothing was changed.");
                    return 1;
                }
            }

            var options = new RestoreOptions(Verify: !a.Has("no-verify"))
            {
                WritingStarted = () => started = true,
                SkipFirstBytes = keep ? SecuritySectors.HeaderLength : 0,
            };
            var result = DriveBackup.Restore(file, target.Device, options, ConsoleProgress(ctx), ctx.Cancel);
            ctx.Out.WriteLine($"Done. {Size.Format(result.Length - result.Skipped)} written{(result.Verified ? ", read back and checked" : "")}.");
            if (result.Skipped > 0)
                ctx.Out.WriteLine($"The first {Size.Format(result.Skipped)} of the drive, with its own security sector, was left as it was.");
            if (!result.ChecksumFileUsed)
                ctx.Out.WriteLine("There was no .sha256 file beside the backup, so the backup itself couldn't be checked for damage.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            if (fresh)
                DiscardHalfMade(target, spec);
            ctx.Out.WriteLine(fresh ? "Stopped, the partly written image was deleted."
                : started ? "Stopped part way. The drive now holds a partial copy and won't work until it is restored again."
                : "Stopped before anything was written.");
            return 1;
        }
        catch when (fresh)
        {
            DiscardHalfMade(target, spec);
            throw;
        }
    }

    // a half-made image that never worked is no use to anyone
    static void DiscardHalfMade(Target target, string path)
    {
        target.Dispose();
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // a line every 10 per cent, which is plenty for something that takes half an hour
    static Action<CopyProgress> ConsoleProgress(CliContext ctx)
    {
        string? phase = null;
        int shown = -1;
        return p =>
        {
            if (p.Phase != phase)
            {
                phase = p.Phase;
                shown = -1;
                ctx.Out.WriteLine($"{p.Phase}...");
            }
            int step = (int)(p.Fraction * 10);
            if (p.Done == 0 || (step == shown && p.Fraction < 1))
                return;
            shown = step;
            ctx.Out.WriteLine($"  {p.Fraction,4:P0}  {p.Summary}");
        };
    }
}
