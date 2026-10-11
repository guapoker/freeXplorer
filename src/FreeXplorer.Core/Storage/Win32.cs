using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FreeXplorer.Storage;

static unsafe class Win32
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint ShareRead = 1;
    public const uint ShareWrite = 2;
    public const uint OpenExisting = 3;

    public const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    public const uint IoctlDiskGetLengthInfo = 0x0007405C;

    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize,
        byte[]? output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long newPosition, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle file, byte* buffer, uint count, out uint read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle file, byte* buffer, uint count, out uint written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll")]
    public static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetDriveTypeW(string root);

    public static SafeFileHandle Open(string path, uint access, uint share = ShareRead | ShareWrite)
    {
        var handle = CreateFileW(path, access, share, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }

    public static bool TryOpen(string path, uint access, out SafeFileHandle handle, out int error)
    {
        handle = CreateFileW(path, access, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return !handle.IsInvalid;
    }

    public static byte[]? Ioctl(SafeFileHandle handle, uint code, byte[]? input, int outputSize)
    {
        var output = new byte[outputSize];
        if (!DeviceIoControl(handle, code, input, input?.Length ?? 0, output, outputSize, out int returned, IntPtr.Zero))
            return null;
        return output.AsSpan(0, returned).ToArray();
    }
}
