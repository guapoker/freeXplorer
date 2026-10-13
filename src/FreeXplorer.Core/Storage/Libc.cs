using System.Runtime.InteropServices;

namespace FreeXplorer.Storage;

// The few libc calls the Linux side needs. Errors come back as errno through
// Marshal.GetLastPInvokeError.
static unsafe class Libc
{
    public const int ORdOnly = 0;
    public const int ORdWr = 2;
    public const int OExcl = 0x80;
    public const int OCloExec = 0x80000;

    public const int EPerm = 1;
    public const int ENoEnt = 2;
    public const int EIntr = 4;
    public const int EAcces = 13;
    public const int EBusy = 16;

    // _IOR(0x12, 114, size_t) and _IO(0x12, 104)
    public const ulong BlkGetSize64 = 0x80081272;
    public const ulong BlkSszGet = 0x1268;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    public static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    public static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "pread", SetLastError = true)]
    public static extern nint PRead(int fd, byte* buffer, nuint count, long offset);

    [DllImport("libc", EntryPoint = "pwrite", SetLastError = true)]
    public static extern nint PWrite(int fd, byte* buffer, nuint count, long offset);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    public static extern int FSync(int fd);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static extern int Ioctl(int fd, ulong request, long* value);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static extern int Ioctl(int fd, ulong request, int* value);

    [DllImport("libc", EntryPoint = "lchown", SetLastError = true)]
    public static extern int LChown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buffer);

    public static int Errno => Marshal.GetLastPInvokeError();

    public static string Message(int errno) => Marshal.GetPInvokeErrorMessage(errno);

    // st_blocks sits at byte 64 of struct stat on both x64 and arm64 glibc
    public static long? AllocatedBytes(string path)
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            return null;
        var buffer = stackalloc byte[256];
        if (Stat(path, buffer) != 0)
            return null;
        return *(long*)(buffer + 64) * 512;
    }
}
