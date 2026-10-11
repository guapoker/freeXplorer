using FreeXplorer.Fatx;
using FreeXplorer.Mount;
using FreeXplorer.Xbox360;

namespace FreeXplorer.Cli;

static partial class Commands
{
    // mount <target> [partition|all] [--rw] [--letter X:]
    public static int Mount(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "letter", "folder", "hold" });
        a.RejectUnknown("rw", "letter", "folder", "hold", "allow-pc-disk");
        string spec = a.Require(0, "target");
        string? which = a.Optional(1);
        bool write = a.Has("rw");

        if (!WinFspMount.IsInstalled)
        {
            ctx.Err.WriteLine("freexplorer: WinFsp isn't installed. It's the free driver that lets Windows show a FATX partition as a drive.");
            ctx.Err.WriteLine($"Install it from {WinFspMount.DownloadPage} (or: winget install WinFsp.WinFsp, or the Install driver button in the freeXplorer window) and run this again.");
            return 1;
        }

        using var target = Target.Open(spec, write, a.Has("allow-pc-disk"));
        var scan = DriveScanner.Scan(target.Device);

        List<DetectedPartition> chosen;
        if (which is null || which.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            chosen = scan.Partitions.Where(p => p.CanMount).ToList();
            if (chosen.Count == 0)
                throw new UsageException("there are no FATX partitions here to mount");
        }
        else
        {
            chosen = new List<DetectedPartition> { Target.Pick(scan, which) };
        }
        if ((a.Has("letter") || a.Has("folder")) && chosen.Count > 1)
            throw new UsageException("--letter and --folder only make sense for one partition, name it instead of using \"all\"");
        if (a.Has("letter") && a.Has("folder"))
            throw new UsageException("pick either --letter or --folder");

        var mounted = new List<MountedVolume>();
        try
        {
            foreach (var p in chosen)
            {
                var volume = DriveScanner.OpenPartition(target.Device, p, write);
                try
                {
                    ctx.Out.WriteLine($"reading {p.Name}...");
                    volume.ScanFreeSpace();
                    string label = volume.Label is { Length: > 0 } l ? l : p.Name;
                    string? point = a.Get("folder") ?? NormalizeLetter(a.Get("letter"));
                    var m = WinFspMount.Mount(volume, label, point);
                    mounted.Add(m);
                    ctx.Out.WriteLine($"{p.Name} is now {m.MountPoint} ({(m.ReadOnly ? "read-only" : "read/write")})");
                }
                catch
                {
                    volume.Dispose();
                    throw;
                }
            }

            // --hold keeps it mounted for a fixed time, which is what a script wants
            Task waiting;
            if (a.Has("hold"))
            {
                if (!int.TryParse(a.Get("hold"), out int seconds) || seconds < 1)
                    throw new UsageException("--hold takes a number of seconds");
                ctx.Out.WriteLine($"Mounted for {seconds} seconds (Ctrl+C to stop sooner).");
                waiting = Task.Delay(TimeSpan.FromSeconds(seconds));
            }
            else
            {
                ctx.Out.WriteLine("Press Enter (or Ctrl+C) to unmount.");
                waiting = Task.Run(() => ctx.In.ReadLine());
            }
            try
            {
                Task.WaitAny(new[] { waiting }, ctx.Cancel);
            }
            catch (OperationCanceledException)
            {
            }
            return 0;
        }
        catch (MountException ex)
        {
            ctx.Err.WriteLine($"freexplorer: {ex.Message}");
            return 1;
        }
        finally
        {
            foreach (var m in mounted)
            {
                ctx.Out.WriteLine($"unmounting {m.MountPoint}");
                m.Unmount();
            }
        }
    }

    static string? NormalizeLetter(string? letter)
    {
        if (letter is null)
            return null;
        letter = letter.Trim();
        if (letter.Length == 1 && char.IsLetter(letter[0]))
            return char.ToUpperInvariant(letter[0]) + ":";
        if (letter.Length == 2 && char.IsLetter(letter[0]) && letter[1] == ':')
            return char.ToUpperInvariant(letter[0]) + ":";
        throw new UsageException("--letter should look like X:");
    }
}
