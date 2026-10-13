using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FreeXplorer.Storage;

public sealed record PhysicalDiskInfo(
    int Number,
    long Length,
    int SectorSize,
    string Model,
    string Serial,
    string Bus,
    bool Removable,
    bool HasVolumes,
    bool IsSystem)
{
    // \\.\PhysicalDriveN on Windows, /dev/sdX on Linux
    public string Path { get; init; } = PhysicalDisks.PathFor(Number);

    public string Display => $"Disk {Number}: {(string.IsNullOrWhiteSpace(Model) ? "Unknown drive" : Model)} ({Size.Format(Length)}, {Bus}{(Path.StartsWith("/dev/") ? ", " + Path : "")})";
}

public static class Size
{
    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}

public static class PhysicalDisks
{
    public static string PathFor(int number) => $@"\\.\PhysicalDrive{number}";

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return Environment.IsPrivilegedProcess;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Looks at every disk without reading from it, so it works without elevation
    public static IReadOnlyList<PhysicalDiskInfo> List()
    {
        if (OperatingSystem.IsLinux())
            return LinuxDisks.List();
        var disks = new List<PhysicalDiskInfo>();
        if (!OperatingSystem.IsWindows())
            return disks;

        var withVolumes = DisksWithVolumes();
        var system = DisksOfVolume(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");

        for (int n = 0; n < 32; n++)
        {
            if (!Win32.TryOpen(PathFor(n), 0, out var handle, out _))
                continue;
            using (handle)
            {
                var geometry = Win32.Ioctl(handle, Win32.IoctlDiskGetDriveGeometryEx, null, 128);
                if (geometry is null || geometry.Length < 32)
                    continue;

                var query = new byte[12];
                var descriptor = Win32.Ioctl(handle, Win32.IoctlStorageQueryProperty, query, 1024);

                string model = "", serial = "", bus = "Unknown";
                bool removable = false;
                if (descriptor is { Length: >= 36 })
                {
                    removable = descriptor[10] != 0;
                    string vendor = ReadString(descriptor, BitConverter.ToInt32(descriptor, 12));
                    string product = ReadString(descriptor, BitConverter.ToInt32(descriptor, 16));
                    model = $"{vendor} {product}".Trim();
                    serial = ReadString(descriptor, BitConverter.ToInt32(descriptor, 24));
                    bus = BusName(BitConverter.ToInt32(descriptor, 28));
                }

                disks.Add(new PhysicalDiskInfo(
                    n,
                    BitConverter.ToInt64(geometry, 24),
                    BitConverter.ToInt32(geometry, 20),
                    model,
                    serial,
                    bus,
                    removable,
                    withVolumes.Contains(n),
                    system.Contains(n)));
            }
        }
        return disks;
    }

    static string ReadString(byte[] data, int offset)
    {
        if (offset <= 0 || offset >= data.Length)
            return "";
        int end = Array.IndexOf(data, (byte)0, offset);
        if (end < 0)
            end = data.Length;
        return Encoding.ASCII.GetString(data, offset, end - offset).Trim();
    }

    static string BusName(int bus) => bus switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        _ => "Unknown",
    };

    static HashSet<int> DisksWithVolumes()
    {
        var found = new HashSet<int>();
        uint mask = Win32.GetLogicalDrives();
        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0)
                continue;
            string root = $"{(char)('A' + i)}:\\";
            uint type = Win32.GetDriveTypeW(root);
            if (type != 2 && type != 3)   // removable or fixed
                continue;
            found.UnionWith(DisksOfVolume(root));
        }
        return found;
    }

    static HashSet<int> DisksOfVolume(string root)
    {
        var result = new HashSet<int>();
        string path = $@"\\.\{root.TrimEnd('\\')}";
        if (!Win32.TryOpen(path, 0, out var handle, out _))
            return result;
        using (handle)
        {
            var extents = Win32.Ioctl(handle, Win32.IoctlVolumeGetVolumeDiskExtents, null, 1024);
            if (extents is { Length: >= 8 })
            {
                int count = BitConverter.ToInt32(extents, 0);
                for (int i = 0; i < count && 8 + i * 24 + 4 <= extents.Length; i++)
                    result.Add(BitConverter.ToInt32(extents, 8 + i * 24));
            }
        }
        return result;
    }
}
