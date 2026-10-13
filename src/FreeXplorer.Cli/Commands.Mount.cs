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

#if WINDOWS
        if (!WinFspMount.IsInstalled)
        {
            ctx.Err.WriteLine("freexplorer: WinFsp isn't installed. It's the free driver that lets Windows show a FATX partition as a drive.");
            ctx.Err.WriteLine($"Install it from {WinFspMount.DownloadPage} (or: winget install WinFsp.WinFsp, or the Install driver button in the freeXplorer window) and run this again.");
            return 1;
        }
#else
        if (!FuseMount.IsInstalled)
        {
            ctx.Err.WriteLine("freexplorer: FUSE isn't available. It's what lets Linux show a FATX partition as a folder.");
            ctx.Err.WriteLine("Install it (on Ubuntu or Debian: sudo apt install fuse3) and run this again.");
            return 1;
        }
        if (a.Has("letter"))
            throw new UsageException("drive letters are a Windows thing, use --folder PATH instead");
#endif

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
#if WINDOWS
                    string? point = a.Get("folder") ?? NormalizeLetter(a.Get("letter"));
                    var m = WinFspMount.Mount(volume, label, point);
#else
                    var m = FuseMount.Mount(volume, label, a.Get("folder"));
#endif
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
#if WINDOWS
                Task.WaitAny(new[] { waiting }, ctx.Cancel);
#else
                // an umount from outside ends it too
                Task.WaitAny(mounted.Select(m => m.Ended).Prepend(waiting).ToArray(), ctx.Cancel);
#endif
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

#if WINDOWS
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
#endif
}
