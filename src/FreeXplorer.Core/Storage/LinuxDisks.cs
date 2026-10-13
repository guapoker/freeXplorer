namespace FreeXplorer.Storage;

// Whole disks as Linux shows them in /sys/block. Nothing here opens a disk, so it works
// without root. Disks are numbered in name order (sda, sdb, ..., nvme0n1), which can change
// when drives are plugged in or out, so a write always looks the disk up again.
static class LinuxDisks
{
    const string SysBlock = "/sys/block";

    // mount points that make a disk the one the system runs from
    static readonly string[] SystemMounts = { "/", "/boot", "/boot/efi", "/efi", "/usr", "/var", "/home" };

    static readonly string[] Skipped = { "loop", "ram", "zram", "sr", "fd", "dm-", "md", "nbd", "zd" };

    public static IReadOnlyList<PhysicalDiskInfo> List()
    {
        var disks = new List<PhysicalDiskInfo>();
        if (!Directory.Exists(SysBlock))
            return disks;

        var names = Directory.GetDirectories(SysBlock)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => !Skipped.Any(n.StartsWith) && !n.Contains("boot") && !n.Contains("rpmb"))
            .OrderBy(n => n.Length)
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();

        var (inUse, system) = DisksInUse();

        int number = 0;
        foreach (string name in names)
        {
            string dir = Path.Combine(SysBlock, name);
            long sectors = ReadLong(Path.Combine(dir, "size"));
            if (sectors <= 0)
                continue;

            int sectorSize = (int)ReadLong(Path.Combine(dir, "queue", "logical_block_size"));
            string vendor = ReadText(Path.Combine(dir, "device", "vendor"));
            string model = ReadText(Path.Combine(dir, "device", "model"));
            string serial = ReadText(Path.Combine(dir, "device", "serial"));
            if (serial.Length == 0)
                serial = ReadText(Path.Combine(dir, "serial"));

            disks.Add(new PhysicalDiskInfo(
                number++,
                sectors * 512,   // /sys always counts 512-byte sectors
                sectorSize > 0 ? sectorSize : 512,
                $"{vendor} {model}".Trim(),
                serial,
                BusName(name, dir),
                ReadText(Path.Combine(dir, "removable")) == "1",
                inUse.Contains(name),
                system.Contains(name))
            {
                Path = "/dev/" + name,
            });
        }
        return disks;
    }

    static string BusName(string name, string dir)
    {
        if (name.StartsWith("nvme"))
            return "NVMe";
        if (name.StartsWith("mmcblk"))
            return "SD";
        if (name.StartsWith("vd") || name.StartsWith("xvd"))
            return "Virtual";

        string real = Resolve(dir);
        if (real.Contains("/usb"))
            return "USB";
        if (real.Contains("/ata"))
            return "SATA";
        if (real.Contains("/virtio"))
            return "Virtual";
        return name.StartsWith("sd") ? "SCSI" : "Unknown";
    }

    // Every disk that has something mounted, used as swap or sitting under LVM/dm-crypt,
    // and the ones that hold the running system
    static (HashSet<string> InUse, HashSet<string> System) DisksInUse()
    {
        var inUse = new HashSet<string>(StringComparer.Ordinal);
        var system = new HashSet<string>(StringComparer.Ordinal);

        foreach (string line in ReadLines("/proc/self/mounts"))
        {
            var parts = line.Split(' ');
            if (parts.Length < 2 || !parts[0].StartsWith("/dev/"))
                continue;
            var disks = DisksBehind(parts[0]);
            inUse.UnionWith(disks);
            string point = parts[1].Replace("\\040", " ");
            if (SystemMounts.Contains(point))
                system.UnionWith(disks);
        }

        foreach (string line in ReadLines("/proc/swaps").Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0].StartsWith("/dev/"))
                inUse.UnionWith(DisksBehind(parts[0]));
        }

        // partitions held by device mapper or md count as in use even when nothing is mounted
        if (Directory.Exists(SysBlock))
        {
            foreach (string disk in Directory.GetDirectories(SysBlock))
            {
                string name = Path.GetFileName(disk);
                var partitions = Directory.GetDirectories(disk).Where(p => File.Exists(Path.Combine(p, "partition")));
                if (HasHolders(disk) || partitions.Any(HasHolders))
                    inUse.Add(name);
            }
        }
        return (inUse, system);
    }

    // The whole disks a /dev node lives on, following /dev/mapper and dm-N down to the disks
    static HashSet<string> DisksBehind(string device)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        string name = Path.GetFileName(Resolve(device));
        Walk(name, result, 0);
        return result;
    }

    static void Walk(string name, HashSet<string> result, int depth)
    {
        string node = Path.Combine("/sys/class/block", name);
        if (depth > 8 || !Directory.Exists(node))
            return;

        string slaves = Path.Combine(node, "slaves");
        if (Directory.Exists(slaves) && Directory.EnumerateFileSystemEntries(slaves).Any())
        {
            foreach (string slave in Directory.EnumerateFileSystemEntries(slaves))
                Walk(Path.GetFileName(slave), result, depth + 1);
            return;
        }

        if (File.Exists(Path.Combine(node, "partition")))
        {
            string? parent = Path.GetFileName(Path.GetDirectoryName(Resolve(node)));
            if (parent is not null)
                result.Add(parent);
            return;
        }
        result.Add(name);
    }

    static bool HasHolders(string dir)
    {
        string holders = Path.Combine(dir, "holders");
        return Directory.Exists(holders) && Directory.EnumerateFileSystemEntries(holders).Any();
    }

    static string Resolve(string path)
    {
        try
        {
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }

    static IEnumerable<string> ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    static string ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    static long ReadLong(string path) => long.TryParse(ReadText(path), out long value) ? value : 0;
}
