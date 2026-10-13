using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FreeXplorer.Storage;

// \\.\PhysicalDriveN on Windows, /dev/sdX on Linux. Needs an elevated process. Opening one for writing is the only
// thing in here that can damage a disk, and nothing calls it without the safety checks.
public static class PhysicalDiskDevice
{
    public static IBlockDevice Open(int number, bool writable)
    {
        if (OperatingSystem.IsLinux())
        {
            var disk = PhysicalDisks.List().FirstOrDefault(d => d.Number == number)
                ?? throw new FileNotFoundException($"there is no disk {number}.");
            return OpenLinux(disk, writable);
        }
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("physical disks are only supported on Windows and Linux");

        string path = PhysicalDisks.PathFor(number);
        uint access = Win32.GenericRead | (writable ? Win32.GenericWrite : 0);
        SafeFileHandle handle;
        try
        {
            handle = Win32.Open(path, access);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Win32.ErrorAccessDenied)
        {
            throw new UnauthorizedAccessException(
                $"Windows won't let this program open disk {number}. Run it as administrator.", ex);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Win32.ErrorSharingViolation)
        {
            throw new IOException($"disk {number} is in use by another program.", ex);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is Win32.ErrorFileNotFound or Win32.ErrorPathNotFound)
        {
            throw new FileNotFoundException($"there is no disk {number}.", ex);
        }

        try
        {
            var geometry = Win32.Ioctl(handle, Win32.IoctlDiskGetDriveGeometryEx, null, 128)
                ?? throw new IOException($"couldn't read the size of disk {number}.");
            int sector = BitConverter.ToInt32(geometry, 20);
            long length = BitConverter.ToInt64(geometry, 24);
            if (sector <= 0 || length <= 0)
                throw new IOException($"disk {number} reports a size of zero.");
            return new SectorAlignedDevice(new RawDiskIo(handle, path, sector, length, writable));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static IBlockDevice Open(PhysicalDiskInfo disk, bool writable) =>
        OperatingSystem.IsLinux() ? OpenLinux(disk, writable) : Open(disk.Number, writable);

    // O_EXCL on a block device makes the kernel refuse it while any partition of it is mounted
    static unsafe IBlockDevice OpenLinux(PhysicalDiskInfo disk, bool writable)
    {
        string path = disk.Path;
        int flags = Libc.OCloExec | (writable ? Libc.ORdWr | Libc.OExcl : Libc.ORdOnly);
        int fd = Libc.Open(path, flags);
        if (fd < 0)
        {
            int errno = Libc.Errno;
            throw errno switch
            {
                Libc.EAcces or Libc.EPerm => new UnauthorizedAccessException(
                    $"Linux won't let this program open {path}. Run it with sudo."),
                Libc.EBusy => new IOException($"{path} is in use. Unmount its partitions first."),
                Libc.ENoEnt => new FileNotFoundException($"there is no {path}."),
                _ => new IOException($"couldn't open {path}: {Libc.Message(errno)}"),
            };
        }

        long length;
        int sector;
        if (Libc.Ioctl(fd, Libc.BlkGetSize64, &length) != 0 || Libc.Ioctl(fd, Libc.BlkSszGet, &sector) != 0
            || length <= 0 || sector <= 0)
        {
            Libc.Close(fd);
            throw new IOException($"couldn't read the size of {path}.");
        }
        return new SectorAlignedDevice(new LinuxDiskIo(fd, path, sector, length, writable));
    }

    sealed unsafe class LinuxDiskIo : IAlignedIo
    {
        int fd;

        public LinuxDiskIo(int fd, string name, int sectorSize, long length, bool writable)
        {
            this.fd = fd;
            Name = name;
            SectorSize = sectorSize;
            Length = length;
            CanWrite = writable;
        }

        public string Name { get; }
        public long Length { get; }
        public int SectorSize { get; }
        public bool CanWrite { get; }

        public void ReadAligned(long offset, Span<byte> buffer)
        {
            fixed (byte* p = buffer)
            {
                int done = 0;
                while (done < buffer.Length)
                {
                    nint n = Libc.PRead(fd, p + done, (nuint)(buffer.Length - done), offset + done);
                    if (n < 0 && Libc.Errno == Libc.EIntr)
                        continue;
                    if (n < 0)
                        throw Fail("reading", offset + done);
                    if (n == 0)
                        throw new EndOfStreamException($"{Name} ended at {offset + done}");
                    done += (int)n;
                }
            }
        }

        public void WriteAligned(long offset, ReadOnlySpan<byte> buffer)
        {
            fixed (byte* p = buffer)
            {
                int done = 0;
                while (done < buffer.Length)
                {
                    nint n = Libc.PWrite(fd, p + done, (nuint)(buffer.Length - done), offset + done);
                    if (n < 0 && Libc.Errno == Libc.EIntr)
                        continue;
                    if (n < 0)
                        throw Fail("writing", offset + done);
                    if (n == 0)
                        throw new IOException($"{Name} accepted no data at {offset + done}");
                    done += (int)n;
                }
            }
        }

        public void Flush()
        {
            if (CanWrite && Libc.FSync(fd) != 0)
                throw Fail("flushing", 0);
        }

        public void Dispose()
        {
            int handle = Interlocked.Exchange(ref fd, -1);
            if (handle >= 0)
                Libc.Close(handle);
        }

        IOException Fail(string what, long where) =>
            new($"error {what} {Name} at byte {where}: {Libc.Message(Libc.Errno)}");
    }

    sealed unsafe class RawDiskIo : IAlignedIo
    {
        readonly SafeFileHandle handle;
        readonly object gate = new();

        public RawDiskIo(SafeFileHandle handle, string name, int sectorSize, long length, bool writable)
        {
            this.handle = handle;
            Name = name;
            SectorSize = sectorSize;
            Length = length;
            CanWrite = writable;
        }

        public string Name { get; }
        public long Length { get; }
        public int SectorSize { get; }
        public bool CanWrite { get; }

        public void ReadAligned(long offset, Span<byte> buffer)
        {
            lock (gate)
            {
                Seek(offset);
                fixed (byte* p = buffer)
                {
                    int done = 0;
                    while (done < buffer.Length)
                    {
                        if (!Win32.ReadFile(handle, p + done, (uint)(buffer.Length - done), out uint n, IntPtr.Zero))
                            throw Fail("reading", offset + done);
                        if (n == 0)
                            throw new EndOfStreamException($"{Name} ended at {offset + done}");
                        done += (int)n;
                    }
                }
            }
        }

        public void WriteAligned(long offset, ReadOnlySpan<byte> buffer)
        {
            lock (gate)
            {
                Seek(offset);
                fixed (byte* p = buffer)
                {
                    int done = 0;
                    while (done < buffer.Length)
                    {
                        if (!Win32.WriteFile(handle, p + done, (uint)(buffer.Length - done), out uint n, IntPtr.Zero))
                            throw Fail("writing", offset + done);
                        if (n == 0)
                            throw new IOException($"{Name} accepted no data at {offset + done}");
                        done += (int)n;
                    }
                }
            }
        }

        public void Flush()
        {
            if (CanWrite)
                Win32.FlushFileBuffers(handle);
        }

        public void Dispose() => handle.Dispose();

        void Seek(long offset)
        {
            if (!Win32.SetFilePointerEx(handle, offset, out _, 0))
                throw Fail("seeking", offset);
        }

        IOException Fail(string what, long where) =>
            new($"error {what} {Name} at byte {where}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }
}
