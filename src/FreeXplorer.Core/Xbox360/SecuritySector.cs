using System.Buffers.Binary;
using System.Text;
using FreeXplorer.Storage;

namespace FreeXplorer.Xbox360;

// What a console drive says about itself in its security sector
public sealed record SecuritySector(string Serial, long ClaimedLength);

public static class SecuritySectors
{
    // The start of a drive that belongs to the drive itself. The security sector is in here and the
    // first partition begins right after it.
    public const long HeaderLength = HddLayout.SystemCacheOffset;

    // the serial number is the first 0x14 bytes, and the number of sectors the console may use is at +0x58
    public static SecuritySector? Read(IBlockDevice device)
    {
        if (device.Length < HddLayout.SecuritySectorOffset + 0x5C || !HddLayout.LooksLikeSecuritySector(device))
            return null;

        string serial = Encoding.ASCII.GetString(device.ReadBytes(HddLayout.SecuritySectorOffset, 0x14)).Trim('\0', ' ');
        var count = device.ReadBytes(HddLayout.SecuritySectorOffset + 0x58, 4);
        return new SecuritySector(serial, BinaryPrimitives.ReadUInt32LittleEndian(count) * 512L);
    }

    // Why a restore can't leave the target's own start in place, or null when it can. The Content
    // partition in a backup is laid out for the size the console saw back then, and the console goes by
    // the target's own security sector, so the two have to agree.
    public static string? KeepProblem(long backupVisibleLength, SecuritySector? target)
    {
        if (target is null)
            return "This drive has no security sector of its own, so there's nothing to keep. Leave this off and it gets the backup's.";
        if (target.ClaimedLength != backupVisibleLength)
            return $"The console sees this drive as {Size.Format(target.ClaimedLength)}, and the backup was made from a drive it saw as {Size.Format(backupVisibleLength)}. " +
                "The Content partition in the backup is laid out for that size, so it wouldn't read properly here. Keeping a drive's own security sector only works between drives of the same size.";
        return null;
    }

    public static string? KeepProblem(IBlockDevice backup, IBlockDevice target) =>
        KeepProblem(HddLayout.VisibleLength(backup), Read(target));
}
