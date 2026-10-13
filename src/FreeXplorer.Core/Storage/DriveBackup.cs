using System.Text.RegularExpressions;

namespace FreeXplorer.Storage;

// A backup or restore that can't go ahead, with a message meant to be shown to the user
public sealed class BackupException : IOException
{
    public BackupException(string message) : base(message)
    {
    }
}

public sealed record BackupOptions(bool Verify = true, bool Sparse = true);

public sealed record BackupResult(string Path, long Length, string Sha256, bool Verified, long SizeOnDisk);

public sealed record RestoreOptions(bool Verify = true)
{
    // called once, right before the first byte goes to the drive. Until then a failure leaves it exactly as it was.
    public Action? WritingStarted { get; init; }

    // How much of the start of the drive to leave as it is. The backup's own copy of that part is
    // never written, and never read back either.
    public long SkipFirstBytes { get; init; }
}

// Sha256 is of what was written, which is everything after SkipFirstBytes
public sealed record RestoreResult(long Length, string Sha256, bool Verified, bool ChecksumFileUsed, long Skipped = 0);

// Whether a backup can be written to the place that was picked
public sealed record DestinationCheck(string? Problem, string? Warning, long FreeBytes, bool Sparse);

// A backup is an exact copy of a whole drive in one file, every sector from the first to the last,
// so the security sector, the cache partitions and everything else comes along. Next to it goes a
// small .sha256 file, which is how a damaged backup is spotted before it overwrites a drive.
public static partial class DriveBackup
{
    // a restore writes in long runs, give the drive a chance to say it has caught up now and then
    const long FlushEvery = 128L << 20;

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex HashPattern();

    public static string ChecksumPath(string imagePath) => imagePath + ".sha256";

    // ---- where a backup can go ----

    public static DestinationCheck CheckDestination(string path, long length)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new DestinationCheck("That isn't a usable file name.", null, 0, false);
        }

        string? folder = Path.GetDirectoryName(full);
        if (folder is null || !Directory.Exists(folder))
            return new DestinationCheck("That folder doesn't exist.", null, 0, false);
        if (File.Exists(full) || Directory.Exists(full))
            return new DestinationCheck("There's already something with that name.", null, 0, false);

        string? root = OperatingSystem.IsWindows() ? Path.GetPathRoot(full) : MountPointOf(full);
        if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal))
            return new DestinationCheck(null, "This is a network location, so there's no way to tell how much room it has.", 0, false);

        try
        {
            var drive = new DriveInfo(root);
            string format = drive.DriveFormat;
            bool sparse = format.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                || format.Equals("ReFS", StringComparison.OrdinalIgnoreCase)
                || (!OperatingSystem.IsWindows() && UnixSparseFormats.Contains(format));
            if (format is "vfat" or "msdos")
                format = "FAT32";
            long free = drive.AvailableFreeSpace;

            if (length > 0xFFFFFFFFL && format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase))
                return new DestinationCheck($"{root} is formatted as {format}, which can't hold a file bigger than 4 GB. Pick another drive.", null, free, false);

            if (!sparse)
            {
                return free < length
                    ? new DestinationCheck($"{root} has {Size.Format(free)} free and a backup needs {Size.Format(length)}. This drive can't skip the empty parts of the backup.", null, free, false)
                    : new DestinationCheck(null, null, free, false);
            }
            if (free < (64L << 20))
                return new DestinationCheck($"{root} is almost full.", null, free, true);
            string? warning = free < length
                ? $"The backup needs room for everything on the drive that isn't empty, up to {Size.Format(length)}. {root} has {Size.Format(free)} free. If it runs out, the backup stops and the partial file is deleted."
                : null;
            return new DestinationCheck(null, warning, free, true);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new DestinationCheck(null, "Couldn't tell how much room there is.", 0, false);
        }
    }

    static readonly HashSet<string> UnixSparseFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "ext2", "ext3", "ext4", "btrfs", "xfs", "zfs", "f2fs", "tmpfs", "ntfs", "ntfs3", "bcachefs", "jfs", "reiserfs",
    };

    // The folder a path's file system is mounted on, the longest mount point that contains it
    static string MountPointOf(string full)
    {
        string best = "/";
        foreach (var drive in DriveInfo.GetDrives())
        {
            string point = drive.RootDirectory.FullName;
            string prefix = point.EndsWith('/') ? point : point + "/";
            if ((full == point || full.StartsWith(prefix, StringComparison.Ordinal)) && point.Length > best.Length)
                best = point;
        }
        return best;
    }

    // ---- making a backup ----

    public static BackupResult Create(IBlockDevice source, string path, BackupOptions options,
        Action<CopyProgress>? progress, CancellationToken cancel)
    {
        long length = source.Length;
        string checksum = ChecksumPath(path);
        FileBlockDevice? image = null;
        bool created = false, done = false;
        try
        {
            // CreateNew, so a file that's already there is never touched
            image = FileBlockDevice.CreateImage(path, length, options.Sparse);
            created = true;
            string hash = DriveCopy.Copy(source, image, length,
                new CopyOptions { SkipZeroBlocks = options.Sparse }, progress, cancel, "Copying the drive");
            image.Dispose();
            image = null;

            if (options.Verify)
            {
                using var again = new FileBlockDevice(path, writable: false);
                string back = DriveCopy.Hash(again, length, progress, cancel, "Checking the backup");
                if (back != hash)
                    throw new BackupException("The backup file doesn't read back the same as the drive did, so it was deleted. " +
                        "The disk it was saved on may be failing, try saving it somewhere else.");
            }

            File.WriteAllText(checksum, $"{hash}  {Path.GetFileName(path)}\n");
            FileOwner.GiveToSudoUser(checksum);
            done = true;
            return new BackupResult(Path.GetFullPath(path), length, hash, options.Verify, SparseFile.SizeOnDisk(path));
        }
        finally
        {
            image?.Dispose();
            if (created && !done)
            {
                TryDelete(path);
                TryDelete(checksum);
            }
        }
    }

    // ---- putting one back ----

    public static RestoreResult Restore(string path, IBlockDevice target, RestoreOptions options,
        Action<CopyProgress>? progress, CancellationToken cancel)
    {
        if (!target.CanWrite)
            throw new NotSupportedException($"{target.Name} was opened read-only");

        using var image = new FileBlockDevice(path, writable: false);
        long length = image.Length;
        if (length == 0)
            throw new BackupException("That backup file is empty.");
        if (length > target.Length)
            throw new InvalidOperationException(
                $"The backup is {Size.Format(length)} and the drive is only {Size.Format(target.Length)}. A drive has to be at least as big as the one that was backed up.");

        long skip = options.SkipFirstBytes;
        if (skip < 0 || skip >= length)
            throw new InvalidOperationException($"The backup is only {Size.Format(length)}, there's nothing left to write after the first {Size.Format(skip)}.");

        // a damaged backup is found out before the drive is touched
        string? expected = ReadChecksum(path);
        if (expected is not null)
        {
            string before = DriveCopy.Hash(image, length, progress, cancel, "Checking the backup");
            if (before != expected)
                throw new BackupException("The backup file doesn't match its checksum file, so it has been damaged since it was made. Nothing was written to the drive.");
        }

        options.WritingStarted?.Invoke();
        string hash = DriveCopy.Copy(image, target, length, new CopyOptions { FlushEvery = FlushEvery },
            progress, cancel, "Writing the drive", skip);

        if (options.Verify)
        {
            string back = DriveCopy.Hash(target, length, progress, cancel, "Checking the drive", skip);
            if (back != hash)
                throw new BackupException("The drive doesn't read back the same as the backup. It was not copied correctly, " +
                    "so don't use it. Try again, and if it keeps happening the drive or its cable may be failing.");
        }
        return new RestoreResult(length, hash, options.Verify, expected is not null, skip);
    }

    // the hash from the .sha256 file next to a backup, or null when there isn't one that makes sense
    public static string? ReadChecksum(string imagePath)
    {
        try
        {
            string file = ChecksumPath(imagePath);
            if (!File.Exists(file))
                return null;
            foreach (string line in File.ReadLines(file))
            {
                string text = line.Trim();
                if (text.Length >= 64 && HashPattern().IsMatch(text[..64]))
                    return text[..64].ToLowerInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
