using System.Diagnostics;
using System.Runtime.InteropServices;
using FreeXplorer.Fatx;
using FreeXplorer.Mount;
using FreeXplorer.Storage;

namespace FreeXplorer.App.Services;

public sealed class WindowsPlatform : IPlatform
{
    public string? DriverVersion => WinFspMount.InstalledVersion();

    public Task<DriverInstallResult> InstallDriverAsync(Action<string>? status, Action<double>? progress) =>
        new WinFspInstaller().InstallAsync(status, progress);

    public IReadOnlyList<PhysicalDiskInfo> ListDisks() => PhysicalDisks.List();

    public IBlockDevice OpenDisk(PhysicalDiskInfo disk, bool writable)
    {
        // The list on screen can be old. Disk numbers get reshuffled when something is plugged in or
        // pulled out, so look again before opening anything. A USB adapter keeps its name and serial
        // whichever drive is inside it, so the size is what tells the drives apart.
        var now = PhysicalDisks.List().FirstOrDefault(d => d.Number == disk.Number)
            ?? throw new DiskChangedException($"Disk {disk.Number} isn't connected any more.");
        if (now.Length != disk.Length || now.SectorSize != disk.SectorSize)
            throw new DiskChangedException(
                $"Disk {disk.Number} is a different drive now. It was {ExactSize(disk.Length, now.Length)} when the list was loaded and it is {ExactSize(now.Length, disk.Length)} now.");

        if (writable)
        {
            var verdict = DiskSafety.CanWrite(now);
            if (!verdict.Allowed)
                throw new InvalidOperationException(verdict.Reason);
        }
        return PhysicalDiskDevice.Open(disk.Number, writable);
    }

    // "55.9 GB" is enough unless the two sizes would read the same
    static string ExactSize(long bytes, long other) =>
        Size.Format(bytes) == Size.Format(other) ? $"{bytes:N0} bytes" : Size.Format(bytes);

    public IMountedVolume Mount(FatxVolume volume, string label) =>
        new Mounted(WinFspMount.Mount(volume, label));

    public void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    public IDisposable KeepAwake(string reason) => new PowerRequest(reason);

    // A power request, which unlike SetThreadExecutionState doesn't belong to one thread, and the
    // work being protected hops between threads all the time.
    sealed class PowerRequest : IDisposable
    {
        const uint PowerRequestVersion = 0;
        const uint ReasonSimpleString = 0x00000001;
        const int SystemRequired = 1;

        // the string shares its place with a longer "detailed" form, so the struct is as big as that one
        [StructLayout(LayoutKind.Explicit, Size = 32)]
        struct ReasonContext
        {
            [FieldOffset(0)] public uint Version;
            [FieldOffset(4)] public uint Flags;
            [FieldOffset(8)] public IntPtr SimpleReasonString;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr PowerCreateRequest(ref ReasonContext context);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool PowerSetRequest(IntPtr request, int type);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool PowerClearRequest(IntPtr request, int type);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        IntPtr handle;
        IntPtr text;

        public PowerRequest(string reason)
        {
            text = Marshal.StringToHGlobalUni(reason);
            var context = new ReasonContext { Version = PowerRequestVersion, Flags = ReasonSimpleString, SimpleReasonString = text };
            handle = PowerCreateRequest(ref context);
            // if Windows won't give us one the work just goes ahead without it
            if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !PowerSetRequest(handle, SystemRequired))
                Release();
        }

        public void Dispose() => Release();

        void Release()
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                PowerClearRequest(handle, SystemRequired);
                CloseHandle(handle);
            }
            handle = IntPtr.Zero;
            if (text != IntPtr.Zero)
                Marshal.FreeHGlobal(text);
            text = IntPtr.Zero;
        }
    }

    sealed class Mounted : IMountedVolume
    {
        readonly MountedVolume inner;
        public Mounted(MountedVolume inner) => this.inner = inner;
        public string MountPoint => inner.MountPoint;
        public bool ReadOnly => inner.ReadOnly;
        public void Unmount() => inner.Unmount();
        public void Dispose() => inner.Dispose();
    }
}
