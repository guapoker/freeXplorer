using System.Diagnostics;
using FreeXplorer.Fatx;
using FreeXplorer.Storage;

namespace FreeXplorer.Cli;

static partial class Commands
{
    public static int Ls(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, NoValues);
        a.RejectUnknown("long");
        string spec = a.Require(0, "target");
        var (partition, rest) = Session.SplitPartition(spec, a.Positional.Skip(1).ToList());
        string path = rest.FirstOrDefault() ?? "\\";

        using var session = Session.Open(spec, partition, write: false);
        var node = session.Volume.Find(path) ?? throw new FatxException(FatxError.NotFound, $"{path} doesn't exist");
        var vol = session.Volume;

        if (!node.IsDirectory)
        {
            PrintEntry(ctx, node, a.Has("long"));
            return 0;
        }
        var children = vol.GetChildren(node).OrderByDescending(n => n.IsDirectory).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
            PrintEntry(ctx, child, a.Has("long"));
        return 0;
    }

    static void PrintEntry(CliContext ctx, FatxNode node, bool detailed)
    {
        string name = node.IsDirectory ? node.Name + "\\" : node.Name;
        if (!detailed)
        {
            ctx.Out.WriteLine(name);
            return;
        }
        string size = node.IsDirectory ? "<dir>" : node.Size.ToString();
        string flags = (node.Attributes.HasFlag(FatxAttributes.ReadOnly) ? "r" : "-")
            + (node.Attributes.HasFlag(FatxAttributes.Hidden) ? "h" : "-")
            + (node.Attributes.HasFlag(FatxAttributes.System) ? "s" : "-");
        ctx.Out.WriteLine($"{flags} {size,12} {node.Modified:yyyy-MM-dd HH:mm}  {name}");
    }

    // get <target> [partition] <path> <dest>
    public static int Get(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, NoValues);
        a.RejectUnknown("force", "quiet");
        string spec = a.Require(0, "target");
        var (partition, rest) = Session.SplitPartition(spec, a.Positional.Skip(1).ToList());
        if (rest.Count != 2)
            throw new UsageException("usage: get <target> [partition] <path on drive> <destination>");

        using var session = Session.Open(spec, partition, write: false);
        var source = session.Volume.Find(rest[0]) ?? throw new FatxException(FatxError.NotFound, $"{rest[0]} doesn't exist");
        string destination = rest[1];
        if (Directory.Exists(destination) && !source.IsRoot)
            destination = Path.Combine(destination, source.Name);

        var stats = new CopyStats(ctx, a.Has("quiet"));
        CopyOut(session.Volume, source, destination, a.Has("force"), stats);
        stats.Finish("copied to this PC");
        return stats.Failures > 0 ? 1 : 0;
    }

    // put <target> [partition] <source> <path>
    public static int Put(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, NoValues);
        a.RejectUnknown("force", "quiet", "allow-pc-disk");
        string spec = a.Require(0, "target");
        var (partition, rest) = Session.SplitPartition(spec, a.Positional.Skip(1).ToList());
        if (rest.Count != 2)
            throw new UsageException("usage: put <target> [partition] <file or folder on this PC> <folder on drive>");

        string source = rest[0];
        if (!File.Exists(source) && !Directory.Exists(source))
            throw new FileNotFoundException($"can't find \"{source}\"");

        using var session = Session.Open(spec, partition, write: true, allowPcDisk: a.Has("allow-pc-disk"));
        var vol = session.Volume;
        var existing = vol.Find(rest[1]);

        FatxNode parent;
        string name;
        if (existing is { IsDirectory: true })
        {
            parent = existing;
            name = Path.GetFileName(source.TrimEnd('\\', '/'));
        }
        else
        {
            var parts = FatxName.SplitPath(rest[1]);
            if (parts.Length == 0)
                throw new UsageException("give a destination folder on the drive");
            parent = vol.CreateDirectories(string.Join('\\', parts[..^1]));
            name = parts[^1];
        }

        var stats = new CopyStats(ctx, a.Has("quiet"));
        CopyIn(vol, parent, source, name, a.Has("force"), stats);
        vol.Flush();
        stats.Finish("copied to the drive");
        return stats.Failures > 0 ? 1 : 0;
    }

    public static int Mkdir(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, NoValues);
        a.RejectUnknown("allow-pc-disk");
        string spec = a.Require(0, "target");
        var (partition, rest) = Session.SplitPartition(spec, a.Positional.Skip(1).ToList());
        if (rest.Count != 1)
            throw new UsageException("usage: mkdir <target> [partition] <path>");

        using var session = Session.Open(spec, partition, write: true, allowPcDisk: a.Has("allow-pc-disk"));
        session.Volume.CreateDirectories(rest[0]);
        session.Volume.Flush();
        return 0;
    }

    public static int Rm(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, NoValues);
        a.RejectUnknown("recursive", "allow-pc-disk");
        string spec = a.Require(0, "target");
        var (partition, rest) = Session.SplitPartition(spec, a.Positional.Skip(1).ToList());
        if (rest.Count != 1)
            throw new UsageException("usage: rm <target> [partition] <path> [-r]");

        using var session = Session.Open(spec, partition, write: true, allowPcDisk: a.Has("allow-pc-disk"));
        var vol = session.Volume;
        var node = vol.Find(rest[0]) ?? throw new FatxException(FatxError.NotFound, $"{rest[0]} doesn't exist");
        if (node.IsRoot)
            throw new UsageException("that's the whole partition, use format if you want to wipe it");
        if (node.IsDirectory && !a.Has("recursive") && vol.GetChildren(node).Count > 0)
            throw new UsageException($"{node.Path} isn't empty, add -r to remove it and everything in it");

        int count = DeleteTree(vol, node);
        vol.Flush();
        ctx.Out.WriteLine($"removed {count} item{(count == 1 ? "" : "s")}");
        return 0;
    }

    public static int Mv(string[] args, CliContext ctx)
    {
        var a = ParsedArgs.Parse(args, NoValues);
        a.RejectUnknown("force", "allow-pc-disk");
        string spec = a.Require(0, "target");
        var (partition, rest) = Session.SplitPartition(spec, a.Positional.Skip(1).ToList());
        if (rest.Count != 2)
            throw new UsageException("usage: mv <target> [partition] <from> <to>");

        using var session = Session.Open(spec, partition, write: true, allowPcDisk: a.Has("allow-pc-disk"));
        var vol = session.Volume;
        var node = vol.Find(rest[0]) ?? throw new FatxException(FatxError.NotFound, $"{rest[0]} doesn't exist");

        var target = vol.Find(rest[1]);
        if (target is { IsDirectory: true } && target != node)
        {
            vol.Rename(node, target, node.Name, a.Has("force"));
        }
        else
        {
            var parts = FatxName.SplitPath(rest[1]);
            if (parts.Length == 0)
                throw new UsageException("give a new name or folder");
            var parent = vol.Find(string.Join('\\', parts[..^1]))
                ?? throw new FatxException(FatxError.NotFound, "the destination folder doesn't exist");
            vol.Rename(node, parent, parts[^1], a.Has("force"));
        }
        vol.Flush();
        return 0;
    }

    // ---- copy helpers ----

    sealed class CopyStats
    {
        readonly CliContext ctx;
        readonly bool quiet;
        readonly Stopwatch clock = Stopwatch.StartNew();

        public CopyStats(CliContext ctx, bool quiet)
        {
            this.ctx = ctx;
            this.quiet = quiet;
        }

        public int Files;
        public int Failures;
        public long Bytes;

        public void Note(string line)
        {
            if (!quiet)
                ctx.Out.WriteLine(line);
        }

        public void Fail(string line)
        {
            Failures++;
            ctx.Err.WriteLine(line);
        }

        public void Finish(string what)
        {
            double seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
            ctx.Out.WriteLine($"{Files} file{(Files == 1 ? "" : "s")}, {Size.Format(Bytes)} {what} ({Size.Format((long)(Bytes / seconds))}/s)" +
                (Failures > 0 ? $", {Failures} failed" : ""));
        }
    }

    static void CopyOut(FatxVolume vol, FatxNode node, string destination, bool force, CopyStats stats)
    {
        if (node.IsDirectory)
        {
            Directory.CreateDirectory(destination);
            foreach (var child in vol.GetChildren(node))
                CopyOut(vol, child, Path.Combine(destination, child.Name), force, stats);
            return;
        }

        if (File.Exists(destination) && !force)
        {
            stats.Fail($"skipped {destination}, it already exists (use --force to overwrite)");
            return;
        }
        try
        {
            using (var output = File.Create(destination))
            using (var input = new FatxStream(vol, node))
                input.CopyTo(output, 1 << 20);
            File.SetLastWriteTime(destination, node.Modified);
            stats.Files++;
            stats.Bytes += node.Size;
            stats.Note($"  {node.Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            stats.Fail($"couldn't copy {node.Path}: {ex.Message}");
        }
    }

    static void CopyIn(FatxVolume vol, FatxNode parent, string source, string name, bool force, CopyStats stats)
    {
        if (Directory.Exists(source))
        {
            if (!FatxName.IsValid(name))
            {
                stats.Fail($"skipped folder {source}, \"{name}\" isn't a name FATX can store");
                return;
            }
            var dir = vol.FindChild(parent, name) ?? vol.CreateDirectory(parent, name);
            if (!dir.IsDirectory)
            {
                stats.Fail($"skipped {source}, a file with that name is in the way");
                return;
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
                CopyIn(vol, dir, entry, Path.GetFileName(entry), force, stats);
            return;
        }

        if (!FatxName.IsValid(name))
        {
            stats.Fail($"skipped {source}, \"{name}\" isn't a name FATX can store (42 characters, letters, digits and basic punctuation)");
            return;
        }
        var info = new System.IO.FileInfo(source);
        if (info.Length > FatxVolume.MaxFileSize)
        {
            stats.Fail($"skipped {source}, FATX files can't be bigger than 4 GB");
            return;
        }

        try
        {
            var node = vol.FindChild(parent, name);
            if (node is not null)
            {
                if (node.IsDirectory || !force)
                {
                    stats.Fail($"skipped {source}, {node.Path} already exists (use --force to overwrite files)");
                    return;
                }
                vol.SetLength(node, 0);
            }
            else
            {
                node = vol.CreateFile(parent, name);
            }

            using (var input = File.OpenRead(source))
            using (var output = new FatxStream(vol, node, writable: true))
                input.CopyTo(output, 1 << 20);
            vol.SetTimes(node, null, info.LastWriteTime, null);
            vol.FlushNode(node);
            stats.Files++;
            stats.Bytes += info.Length;
            stats.Note($"  {node.Path}");
        }
        catch (FatxException ex) when (ex.Error is FatxError.DiskFull or FatxError.DirectoryFull or FatxError.PathTooLong)
        {
            stats.Fail($"couldn't copy {source}: {ex.Message}");
        }
    }

    static int DeleteTree(FatxVolume vol, FatxNode node)
    {
        int count = 0;
        if (node.IsDirectory)
            foreach (var child in vol.GetChildren(node).ToList())
                count += DeleteTree(vol, child);
        vol.Delete(node);
        return count + 1;
    }
}
