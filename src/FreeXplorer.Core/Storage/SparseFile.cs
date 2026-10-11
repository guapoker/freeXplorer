using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FreeXplorer.Storage;

public static class SparseFile
{
    const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetCompressedFileSizeW(string path, out uint high);

    // What the file really takes on disk, which for a sparse file is far less than its length
    public static long SizeOnDisk(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            uint low = GetCompressedFileSizeW(path, out uint high);
            if (low != 0xFFFFFFFF || Marshal.GetLastWin32Error() == 0)
                return ((long)high << 32) | low;
        }
        return new FileInfo(path).Length;
    }

    // Tells NTFS not to allocate the zeros of a big empty image. Quietly does nothing
    // on file systems that can't do it, the image just takes real space there.
    public static bool TryMark(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        return DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }
}
