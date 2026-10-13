using System.Diagnostics;
using System.Runtime.InteropServices;
using FreeXplorer.Fatx;

namespace FreeXplorer.Mount;

public sealed class MountException : Exception
{
    public MountException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

// One FATX partition showing up as a folder. Dispose (or Unmount) to take it away again.
public sealed class MountedVolume : IDisposable
{
    readonly int fd;
    readonly int commFd;
    readonly Thread[] workers;
    readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly bool removeFolder;
    readonly object gate = new();
    bool unmounted;

    internal MountedVolume(FatxVolume volume, FatxFuse fuse, int fd, int commFd, string mountPoint, string label, bool removeFolder)
    {
        Volume = volume;
        this.fd = fd;
        this.commFd = commFd;
        MountPoint = mountPoint;
        Label = label;
        this.removeFolder = removeFolder;

        int running = Workers;
        workers = new Thread[Workers];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = new Thread(() =>
            {
                Serve(fuse);
                if (Interlocked.Decrement(ref running) == 0)
                    ended.TrySetResult();
            })
            { IsBackground = true, Name = $"fuse {label} {i}" };
            workers[i].Start();
        }
    }

    const int Workers = 4;

    public FatxVolume Volume { get; }
    public string MountPoint { get; }
    public string Label { get; }
    public bool ReadOnly => Volume.IsReadOnly;
    public bool IsMounted { get { lock (gate) return !unmounted && !ended.Task.IsCompleted; } }

    // finishes when the kernel lets go, also when someone runs umount from outside
    public Task Ended => ended.Task;

    unsafe void Serve(FatxFuse fuse)
    {
        var request = new byte[Fuse.BufferSize];
        var reply = new byte[Fuse.BufferSize];
        fixed (byte* input = request)
        fixed (byte* output = reply)
        {
            while (true)
            {
                nint n = Native.Read(fd, input, (nuint)request.Length);
                if (n < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    // ENOENT: the request was interrupted before we got it. EINTR and EAGAIN: try again.
                    if (errno is Errno.ENOENT or Errno.EINTR or Errno.EAGAIN)
                        continue;
                    return;   // ENODEV once unmounted
                }
                if (n == 0)
                    return;

                int length = fuse.Handle(request.AsSpan(0, (int)n), reply);
                if (length > 0)
                    Native.Write(fd, output, (nuint)length);   // ENOENT means the caller gave up, nothing to do
            }
        }
    }

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
            if (!ended.Task.IsCompleted)
                FuseMount.RunFusermount("-u", "-z", "--", MountPoint);
            ended.Task.Wait(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Native.Close(fd);
            Native.Close(commFd);
            Volume.Flush();
            if (removeFolder)
            {
                try
                {
                    Directory.Delete(MountPoint);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    public void Dispose() => Unmount();
}

public static class FuseMount
{
    const string Fusermount = "fusermount3";

    public static bool IsInstalled => FindFusermount() is not null && File.Exists("/dev/fuse");

    // Where a partition goes when no folder is given: /media/<user>/<label> when running as
    // root, like the desktop does for USB drives, and ~/FATX/<label> otherwise
    public static string DefaultMountPoint(string label)
    {
        string safe = string.Concat(label.Select(c => c is '/' or '\0' ? '_' : c)).Trim();
        if (safe.Length == 0 || safe is "." or "..")
            safe = "FATX";
        if (Environment.IsPrivilegedProcess)
        {
            string user = Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 } u ? u : "root";
            return Path.Combine("/media", user, safe);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FATX", safe);
    }

    public static MountedVolume Mount(FatxVolume volume, string label, string? folder = null)
    {
        if (!IsInstalled)
            throw new MountException("FUSE isn't available. Install it with: sudo apt install fuse3");

        string point = Path.GetFullPath(folder ?? DefaultMountPoint(label));
        ClearStaleMount(point);
        bool created = false;
        if (!Directory.Exists(point))
        {
            string parent = Path.GetDirectoryName(point)!;
            bool parentExisted = Directory.Exists(parent);
            Directory.CreateDirectory(point);
            created = true;
            if (!parentExisted)
                GiveToUser(parent);
            GiveToUser(point);
        }
        else if (Directory.EnumerateFileSystemEntries(point).Any())
        {
            throw new MountException($"{point} isn't empty. Pick an empty folder.");
        }

        var (owner, group) = Owner();
        var options = new List<string>
        {
            volume.IsReadOnly ? "ro" : "rw",
            "nosuid",
            "nodev",
            "noexec",
            "default_permissions",
            "auto_unmount",
            $"fsname={Sanitize(label)}",
            "subtype=fatx",
        };
        // as root the files belong to the sudo user, who has to be let in explicitly
        if (Environment.IsPrivilegedProcess)
            options.Add("allow_other");

        try
        {
            var (fd, commFd) = Open(point, string.Join(',', options));
            var fuse = new FatxFuse(volume, owner, group);
            return new MountedVolume(volume, fuse, fd, commFd, point, label, created);
        }
        catch
        {
            if (created)
                TryDelete(point);
            throw;
        }
    }

    // A mount left behind by a freeXplorer that was killed answers every access with "transport
    // endpoint is not connected". auto_unmount should clean that up, but Ubuntu's AppArmor profile
    // for fusermount3 stops it, so a leftover FATX mount on the same folder is taken away here.
    static void ClearStaleMount(string point)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines("/proc/self/mounts");
        }
        catch (IOException)
        {
            return;
        }
        foreach (string line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length < 3 || parts[2] != "fuse.fatx")
                continue;
            if (parts[1].Replace("\\040", " ").Replace("\\011", "\t").Replace("\\134", "\\") != point)
                continue;
            try
            {
                Directory.EnumerateFileSystemEntries(point).Any();
                throw new MountException($"{point} already has a FATX partition mounted on it.");
            }
            catch (IOException)
            {
                RunFusermount("-u", "-z", "--", point);   // nobody is answering for it any more
            }
        }
    }

    // fusermount3 does the mount and hands back the /dev/fuse descriptor over a socket.
    // With auto_unmount it stays around and unmounts by itself if this process dies.
    static unsafe (int Fd, int CommFd) Open(string point, string options)
    {
        string tool = FindFusermount() ?? throw new MountException("fusermount3 wasn't found. Install it with: sudo apt install fuse3");

        var pair = stackalloc int[2];
        if (Native.SocketPair(Native.AfUnix, Native.SockStream, 0, pair) != 0)
            throw new MountException($"couldn't make a socket for fusermount3: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        int ours = pair[0], theirs = pair[1];
        Native.SetCloseOnExec(ours);

        Process process;
        try
        {
            // stderr is left alone on purpose: with auto_unmount fusermount3 outlives us, and a pipe
            // nobody reads any more would kill it before it gets to unmount
            var start = new ProcessStartInfo(tool) { UseShellExecute = false };
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add(options);
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(point);
            start.Environment["_FUSE_COMMFD"] = theirs.ToString();
            process = Process.Start(start) ?? throw new MountException("couldn't start fusermount3");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            Native.Close(ours);
            Native.Close(theirs);
            throw new MountException($"couldn't start fusermount3: {ex.Message}", ex);
        }
        Native.Close(theirs);   // the child has its own copy now

        int fd = ReceiveFd(ours);
        if (fd < 0)
        {
            // fusermount3 has already said why on stderr
            process.WaitForExit(5000);
            Native.Close(ours);
            throw new MountException($"couldn't mount on {point}, see the message from fusermount3 above.");
        }
        Native.SetCloseOnExec(fd);
        return (fd, ours);
    }

    static unsafe int ReceiveFd(int socket)
    {
        byte data = 0;
        var iov = new Native.IoVec { Base = &data, Length = 1 };
        var control = stackalloc byte[64];
        var message = new Native.MsgHdr
        {
            Iov = &iov,
            IovLength = 1,
            Control = control,
            ControlLength = 64,
        };

        nint n;
        do
        {
            n = Native.RecvMsg(socket, &message, 0);
        } while (n < 0 && Marshal.GetLastPInvokeError() == Errno.EINTR);
        if (n <= 0 || message.ControlLength < (nuint)sizeof(Native.CmsgHdr) + sizeof(int))
            return -1;

        var header = (Native.CmsgHdr*)control;
        if (header->Level != Native.SolSocket || header->Type != Native.ScmRights)
            return -1;
        return *(int*)(control + sizeof(Native.CmsgHdr));
    }

    internal static void RunFusermount(params string[] args)
    {
        string? tool = FindFusermount();
        if (tool is null)
            return;
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardError = true };
        foreach (string a in args)
            start.ArgumentList.Add(a);
        using var process = Process.Start(start);
        process?.WaitForExit(10_000);
    }

    static string? FindFusermount()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':'))
        {
            if (dir.Length == 0)
                continue;
            string candidate = Path.Combine(dir, Fusermount);
            if (File.Exists(candidate))
                return candidate;
        }
        return File.Exists("/usr/bin/" + Fusermount) ? "/usr/bin/" + Fusermount : null;
    }

    // whoever ran sudo, otherwise ourselves
    static (uint Owner, uint Group) Owner()
    {
        if (Environment.IsPrivilegedProcess
            && uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), out uint uid)
            && uint.TryParse(Environment.GetEnvironmentVariable("SUDO_GID"), out uint gid))
            return (uid, gid);
        return (Native.GetUid(), Native.GetGid());
    }

    static void GiveToUser(string path)
    {
        var (owner, group) = Owner();
        if (Environment.IsPrivilegedProcess && owner != 0)
            Native.LChown(path, owner, group);
    }

    // mount options are separated by commas, so the name can't have any
    static string Sanitize(string label)
    {
        string clean = string.Concat(label.Select(c => c is ',' or '\\' or '\0' || char.IsWhiteSpace(c) ? '_' : c));
        return clean.Length == 0 ? "fatx" : clean;
    }

    static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder);
        }
        catch (IOException)
        {
        }
    }
}

static unsafe class Native
{
    public const int AfUnix = 1;
    public const int SockStream = 1;
    public const int SolSocket = 1;
    public const int ScmRights = 1;
    const int FSetFd = 2;
    const int FdCloExec = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct IoVec
    {
        public void* Base;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MsgHdr
    {
        public void* Name;
        public uint NameLength;
        public IoVec* Iov;
        public nuint IovLength;
        public void* Control;
        public nuint ControlLength;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CmsgHdr
    {
        public nuint Length;
        public int Level;
        public int Type;
    }

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    public static extern nint Read(int fd, byte* buffer, nuint count);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    public static extern nint Write(int fd, byte* buffer, nuint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    public static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "socketpair", SetLastError = true)]
    public static extern int SocketPair(int domain, int type, int protocol, int* fds);

    [DllImport("libc", EntryPoint = "recvmsg", SetLastError = true)]
    public static extern nint RecvMsg(int socket, MsgHdr* message, int flags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    static extern int Fcntl(int fd, int command, int argument);

    [DllImport("libc", EntryPoint = "getuid")]
    public static extern uint GetUid();

    [DllImport("libc", EntryPoint = "getgid")]
    public static extern uint GetGid();

    [DllImport("libc", EntryPoint = "lchown", SetLastError = true)]
    public static extern int LChown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);

    public static void SetCloseOnExec(int fd) => Fcntl(fd, FSetFd, FdCloExec);
}
