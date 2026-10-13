using System.Reflection;
using FreeXplorer.Fatx;

namespace FreeXplorer.Cli;

sealed class CliContext
{
    public CliContext(TextWriter output, TextWriter error, TextReader input, CancellationToken cancel = default)
    {
        Out = output;
        Err = error;
        In = input;
        Cancel = cancel;
    }

    public TextWriter Out { get; }
    public TextWriter Err { get; }
    public TextReader In { get; }
    public CancellationToken Cancel { get; }
}

static class Cli
{
    public static int Run(string[] args, CliContext ctx)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            ctx.Out.Write(HelpText);
            return args.Length == 0 ? 2 : 0;
        }

        string command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        try
        {
            return command switch
            {
                "version" or "--version" => Version(ctx),
                "list" => Commands.List(rest, ctx),
                "info" => Commands.Info(rest, ctx),
                "fsck" => Commands.Fsck(rest, ctx),
                "ls" or "dir" => Commands.Ls(rest, ctx),
                "get" => Commands.Get(rest, ctx),
                "put" => Commands.Put(rest, ctx),
                "mkdir" => Commands.Mkdir(rest, ctx),
                "rm" => Commands.Rm(rest, ctx),
                "mv" => Commands.Mv(rest, ctx),
                "format" => Commands.Format(rest, ctx),
                "new-image" => Commands.NewImage(rest, ctx),
                "backup" => Commands.Backup(rest, ctx),
                "restore" => Commands.Restore(rest, ctx),
                "mount" => Commands.Mount(rest, ctx),
                _ => throw new UsageException($"unknown command \"{args[0]}\", try: help"),
            };
        }
        catch (UsageException ex)
        {
            ctx.Err.WriteLine($"freexplorer: {ex.Message}");
            return 2;
        }
        catch (FatxException ex)
        {
            ctx.Err.WriteLine($"freexplorer: {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            ctx.Err.WriteLine("freexplorer: cancelled");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException or PlatformNotSupportedException)
        {
            ctx.Err.WriteLine($"freexplorer: {ex.Message}");
            return 1;
        }
    }

    static int Version(CliContext ctx)
    {
        var asm = typeof(Cli).Assembly;
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString() ?? "unknown";
        ctx.Out.WriteLine($"freeXplorer {version.Split('+')[0]}");
        return 0;
    }

    const string HelpText = """
        freeXplorer command line tool

        usage: freexplorer-cli <command> [options]

        Targets are a physical disk (disk:3, or /dev/sdb on Linux) or a raw image file.
        Physical disks need an administrator prompt, or sudo on Linux. Partitions are
        content, compat, sysext and sysext2.

        looking around
          list [--scan]                         show the disks connected to this PC
          info <target> [--usage]               show the partitions on a drive or image
          fsck <target> [partition] [--fix]     check a partition for damage (--fix frees leaked clusters)
          ls <target> [partition] [path] [-l]   list a directory

        moving files
          get <target> [partition] <path> <dest>        copy a file or folder to this PC
          put <target> [partition] <source> <path>      copy a file or folder onto the drive
          mkdir <target> [partition] <path>
          rm <target> [partition] <path> [-r]
          mv <target> [partition] <from> <to>

        formatting
          format <target> (--all | --partitions a,b | --whole) [--label NAME] [--cluster-size 16K]
                 [--content-size 100G] [--dry-run] [--yes] [--confirm "disk 3"]
          new-image <file> --size 8G [--partition]     make an empty drive image to practice on

        backups (a backup is an exact copy of the whole drive, every partition included)
          backup <target> <file> [--no-verify] [--no-sparse]
          restore <file> <target> [--keep-header] [--no-verify] [--confirm "disk 3"] [--yes]
                 The target has to be at least as big as the drive the backup came from.
                 A .sha256 file is saved beside the backup and checked before a restore.
                 --keep-header leaves the target's own first 512 KB, its security sector, as it
                 is. Only for a drive the console sees at the same size as the original.

        mounting (needs WinFsp on Windows and FUSE on Linux, see the readme)
          mount <target> [partition|all] [--rw] [--letter X: | --folder PATH] [--hold SECONDS]

        Everything is read-only unless the command is one that changes things. Mounts are
        read-only unless you pass --rw. Without --letter or --folder a mount takes a free
        drive letter on Windows, counting down from Z, and shows up in This PC. On Linux it
        goes in /media/<user>/<name> under sudo and in ~/FATX/<name> otherwise.

        Mounting uses WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos
        https://github.com/winfsp/winfsp

        """;
}
