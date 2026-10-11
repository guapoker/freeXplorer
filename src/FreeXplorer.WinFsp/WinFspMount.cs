using System.Diagnostics;
using Fsp;
using FreeXplorer.Fatx;
using Microsoft.Win32;

namespace FreeXplorer.Mount;

public sealed class MountException : Exception
{
    public MountException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

// One FATX partition showing up as a drive. Dispose (or Unmount) to take it away again.
public sealed class MountedVolume : IDisposable
{
    readonly FileSystemHost host;
    readonly object gate = new();
    bool unmounted;

    internal MountedVolume(FileSystemHost host, FatxVolume volume, string mountPoint, string label)
    {
        this.host = host;
        Volume = volume;
        MountPoint = WinFspMount.DisplayName(mountPoint);
        Label = label;
    }

    public FatxVolume Volume { get; }

    // "X:" for a drive letter, the folder's path otherwise
    public string MountPoint { get; }
    public string Label { get; }
    public bool ReadOnly => Volume.IsReadOnly;
    public bool IsMounted { get { lock (gate) return !unmounted; } }

    public void Unmount()
    {
        lock (gate)
        {
            if (unmounted)
                return;
            unmounted = true;
        }
        try
        {
            host.Unmount();
        }
        finally
        {
            host.Dispose();
            Volume.Flush();
        }
    }

    public void Dispose() => Unmount();
}

public static class WinFspMount
{
    public const string DownloadPage = "https://winfsp.dev/rel/";

    const uint NameCollision = 0xC0000035;
    const uint AccessDenied = 0xC0000022;
    const uint DriverNotRunning = 0xC000000F;

    public static string? InstallDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(@"SOFTWARE\WinFsp");
            if (key?.GetValue("InstallDir") is string dir && File.Exists(Path.Combine(dir, "bin", "winfsp-x64.dll")))
                return dir;
        }
        return null;
    }

    public static bool IsInstalled => InstallDirectory() is not null;

    // "2.1.25156", or null when WinFsp isn't installed
    public static string? InstalledVersion()
    {
        string? dir = InstallDirectory();
        if (dir is null)
            return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(dir, "bin", "winfsp-x64.dll"));
            return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        }
        catch (IOException)
        {
            return "unknown";
        }
    }

    // mountPoint: null takes a free drive letter, "X:" asks for that one, anything longer is an empty folder.
    // Drive letters go through the mount manager, which makes a real disk that This PC and every program
    // sees. A plain letter made by an administrator is private to that one elevated session instead.
    public static MountedVolume Mount(FatxVolume volume, string label, string? mountPoint = null)
    {
        if (!IsInstalled)
            throw new MountException(
                "WinFsp isn't installed. It's the free driver that lets Windows show a FATX partition as a drive. " +
                $"Get it from {DownloadPage} and run this again.");

        var (letter, folder) = ParseMountPoint(mountPoint);
        if (folder is not null)
        {
            var inFolder = TryMount(volume, label, folder, out int folderStatus);
            return inFolder ?? throw new MountException(Describe(folderStatus, folder));
        }

        bool automatic = letter is null;
        var candidates = automatic ? FreeDriveLetters().Take(4).ToList() : new List<char> { letter!.Value };
        if (candidates.Count == 0)
            throw new MountException("Every drive letter is taken. Unmount something, or mount into a folder.");

        int status = 0;
        foreach (char c in candidates)
        {
            var mounted = TryMount(volume, label, $@"\\.\{c}:", out status);
            if (mounted is null && status == unchecked((int)AccessDenied))
                mounted = TryMount(volume, label, $"{c}:", out status);   // not elevated, so this is as good as it gets
            if (mounted is not null)
                return mounted;
            // someone else grabbed the letter between looking and mounting, so try the next one
            if (!automatic || status != unchecked((int)NameCollision))
                break;
        }
        throw new MountException(Describe(status, automatic ? null : $"{letter}:"));
    }

    static MountedVolume? TryMount(FatxVolume volume, string label, string point, out int status)
    {
        FileSystemHost? host = null;
        try
        {
            host = new FileSystemHost(new FatxFileSystem(volume, label));
            status = host.Mount(point, null, false, 0);
            if (status < 0)
                return null;
            var mounted = new MountedVolume(host, volume, host.MountPoint(), label);
            host = null;
            return mounted;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or FileNotFoundException)
        {
            throw new MountException("Windows couldn't load the WinFsp driver library. Try reinstalling WinFsp.", ex);
        }
        finally
        {
            host?.Dispose();
        }
    }

    // null or "*" means any free letter, "X", "X:" and \\.\X: name one, the rest is a folder
    internal static (char? Letter, string? Folder) ParseMountPoint(string? mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint) || mountPoint.Trim() == "*")
            return (null, null);
        string p = mountPoint.Trim();
        if (p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"\\?\", StringComparison.Ordinal))
            p = p[4..];
        if (p.Length > 1 && p.EndsWith('\\'))
            p = p[..^1];
        if ((p.Length == 1 || (p.Length == 2 && p[1] == ':')) && char.IsAsciiLetter(p[0]))
            return (char.ToUpperInvariant(p[0]), null);
        return (null, mountPoint);
    }

    // what to show for a mount point: "X:" for any of the spellings of a drive letter
    public static string DisplayName(string mountPoint) =>
        ParseMountPoint(mountPoint).Letter is { } c ? $"{c}:" : mountPoint;

    // From Z down, which keeps clear of the letters USB sticks and card readers like to take
    public static IReadOnlyList<char> FreeDriveLetters() => FreeDriveLetters(TakenLetters());

    public static IReadOnlyList<char> FreeDriveLetters(IEnumerable<char> taken)
    {
        var used = new HashSet<char>(taken.Select(char.ToUpperInvariant));
        return "ZYXWVUTSRQPONMLKJIHGFED".Where(c => !used.Contains(c)).ToList();
    }

    static HashSet<char> TakenLetters()
    {
        var taken = new HashSet<char>();
        foreach (string drive in Directory.GetLogicalDrives())
            if (drive.Length > 0 && char.IsAsciiLetter(drive[0]))
                taken.Add(char.ToUpperInvariant(drive[0]));

        // letters Windows keeps for disks that just aren't plugged in at the moment (only administrators can read this)
        foreach (string name in RegistryNames(Registry.LocalMachine, @"SYSTEM\MountedDevices", values: true))
            if (name.Length == 14 && name.StartsWith(@"\DosDevices\", StringComparison.OrdinalIgnoreCase)
                && name[13] == ':' && char.IsAsciiLetter(name[12]))
                taken.Add(char.ToUpperInvariant(name[12]));

        // network drives that come back at every logon. An elevated program can't see those through
        // GetLogicalDrives, they belong to the other half of the user's session.
        foreach (string name in RegistryNames(Registry.CurrentUser, "Network", values: false))
            if (name.Length == 1 && char.IsAsciiLetter(name[0]))
                taken.Add(char.ToUpperInvariant(name[0]));

        return taken;
    }

    static string[] RegistryNames(RegistryKey root, string path, bool values)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null)
                return Array.Empty<string>();
            return values ? key.GetValueNames() : key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    static string Describe(int status, string? where)
    {
        where ??= "a drive letter";
        return unchecked((uint)status) switch
        {
            NameCollision => $"{where} is already in use.",
            AccessDenied => $"Windows refused to mount on {where}. Try another letter, or run as administrator.",
            DriverNotRunning => "WinFsp's driver isn't running. Reboot, or reinstall WinFsp.",
            _ => $"WinFsp couldn't mount the volume (error 0x{status:X8}).",
        };
    }
}
