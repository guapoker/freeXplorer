using System.Text;
using FreeXplorer.Storage;
using FreeXplorer.Xbox360;

namespace FreeXplorer.Tests;

public class SecuritySectorTests
{
    const long Gib = 1L << 30;

    // what a console drive has at 0x2000: the serial number as text, and at +0x58 the sectors the console may use
    internal static MemoryBlockDevice ConsoleDrive(long length, string serial, long claimedLength)
    {
        var device = new MemoryBlockDevice(length);
        device.Write(0x2000, Encoding.ASCII.GetBytes(serial.PadRight(0x14)));
        device.Write(0x2000 + 0x58, BitConverter.GetBytes((uint)(claimedLength / 512)));
        return device;
    }

    [Fact]
    public void Serial_and_size_are_read()
    {
        using var drive = ConsoleDrive(6 * Gib, "WD-WXQ1A12B3456", 6 * Gib);
        var sector = SecuritySectors.Read(drive)!;
        Assert.Equal("WD-WXQ1A12B3456", sector.Serial);
        Assert.Equal(6 * Gib, sector.ClaimedLength);
    }

    [Fact]
    public void A_drive_without_one_has_none()
    {
        using var blank = new MemoryBlockDevice(6 * Gib);
        Assert.Null(SecuritySectors.Read(blank));

        using var junk = new MemoryBlockDevice(6 * Gib);
        junk.Write(0x2000, Enumerable.Repeat((byte)0x01, 0x20).ToArray());
        Assert.Null(SecuritySectors.Read(junk));

        using var tiny = new MemoryBlockDevice(4096);
        Assert.Null(SecuritySectors.Read(tiny));
    }

    [Fact]
    public void The_part_a_drive_keeps_is_the_first_512_kb()
    {
        Assert.Equal(0x80000, SecuritySectors.HeaderLength);
        Assert.Equal(HddLayout.SystemCacheOffset, SecuritySectors.HeaderLength);
    }

    [Fact]
    public void Drives_of_the_same_size_can_keep_their_own()
    {
        using var backup = ConsoleDrive(6 * Gib, "AAAA", 6 * Gib);
        using var target = ConsoleDrive(6 * Gib, "BBBB", 6 * Gib);
        Assert.Null(SecuritySectors.KeepProblem(backup, target));
    }

    [Fact]
    public void Bigger_drive_cant_keep_its_own()
    {
        using var backup = ConsoleDrive(6 * Gib, "AAAA", 6 * Gib);
        using var target = ConsoleDrive(7 * Gib, "BBBB", 7 * Gib);

        string problem = SecuritySectors.KeepProblem(backup, target)!;
        Assert.Contains("7 GB", problem);
        Assert.Contains("6 GB", problem);
        Assert.Contains("same size", problem);
    }

    [Fact]
    public void A_drive_with_no_security_sector_has_nothing_to_keep()
    {
        using var backup = ConsoleDrive(6 * Gib, "AAAA", 6 * Gib);
        using var target = new MemoryBlockDevice(6 * Gib);
        Assert.Contains("nothing to keep", SecuritySectors.KeepProblem(backup, target));
        Assert.Contains("nothing to keep", SecuritySectors.KeepProblem(6 * Gib, null));
    }

    [Fact]
    public void A_backup_without_a_security_sector_is_judged_by_its_length()
    {
        using var plain = new MemoryBlockDevice(6 * Gib);
        using var same = ConsoleDrive(7 * Gib, "SAME", 6 * Gib);   // the console is told 6 GB although the disk is bigger
        using var other = ConsoleDrive(6 * Gib, "OTHER", 5 * Gib);

        Assert.Null(SecuritySectors.KeepProblem(plain, same));
        Assert.NotNull(SecuritySectors.KeepProblem(plain, other));
    }

    [Fact]
    public void Claimed_size_counts_not_disk_size()
    {
        // a 120 GB disk the console only uses 60 GB of: same as a 60 GB drive as far as the layout goes
        using var backup = ConsoleDrive(6 * Gib, "AAAA", 6 * Gib);
        using var roomy = ConsoleDrive(8 * Gib, "BBBB", 6 * Gib);
        Assert.Null(SecuritySectors.KeepProblem(backup, roomy));
    }
}
