using System.Buffers.Binary;
using FreeXplorer.Storage;

namespace FreeXplorer.Xbox360;

public enum PartitionFileSystem
{
    Fatx,
    Sfcx,
}

public sealed record PartitionSpec(
    string Id,
    string Name,
    long Offset,
    long Length,
    PartitionFileSystem FileSystem,
    string Description)
{
    public bool IsFatx => FileSystem == PartitionFileSystem.Fatx;
    public long End => Offset + Length;
}

// Retail 360 drives have no partition table. The console just knows where everything
// is, so this is the same table written down.
public static class HddLayout
{
    public const long SecuritySectorOffset = 0x2000;
    public const long SystemCacheOffset = 0x80000;
    public const long SystemCacheLength = 0x80000000;
    public const long GameCacheOffset = 0x80080000;
    public const long GameCacheLength = 0xA0E30000;
    public const long SystemExtOffset = 0x10C080000;
    public const long SystemExtLength = 0xCE30000;
    public const long SystemExt2Offset = 0x118EB0000;
    public const long SystemExt2Length = 0x8000000;
    public const long CompatOffset = 0x120EB0000;
    public const long CompatLength = 0x10000000;
    public const long ContentOffset = 0x130EB0000;

    // smallest drive that has any room for a Content partition
    public const long MinimumDriveLength = ContentOffset + 0x10000000;

    public const string SystemCacheId = "syscache";
    public const string GameCacheId = "gamecache";
    public const string SystemExtId = "sysext";
    public const string SystemExt2Id = "sysext2";
    public const string CompatId = "compat";
    public const string ContentId = "content";

    // The console only sees as many sectors as the security sector claims, and that
    // count is a 32 bit number, so nothing past 2 TiB exists as far as it's concerned.
    public static long VisibleLength(IBlockDevice device)
    {
        long length = device.Length / 512 * 512;
        long visible = Math.Min(length, 0xFFFFFFFFL * 512);

        if (length >= SecuritySectorOffset + 0x5C)
        {
            Span<byte> raw = stackalloc byte[4];
            device.Read(SecuritySectorOffset + 0x58, raw);
            long claimed = BinaryPrimitives.ReadUInt32LittleEndian(raw) * 512L;
            if (claimed > ContentOffset + CompatLength && claimed <= length)
                visible = claimed;
        }
        return visible;
    }

    public static bool LooksLikeSecuritySector(IBlockDevice device)
    {
        if (device.Length < SecuritySectorOffset + 0x20)
            return false;
        var serial = device.ReadBytes(SecuritySectorOffset, 0x14);
        bool anyText = false;
        foreach (byte b in serial)
        {
            if (b == 0 || b == 0x20)
                continue;
            if (b is < 0x21 or > 0x7E)
                return false;
            anyText = true;
        }
        return anyText;
    }

    // Every partition the console knows about, with Content stretched to the end of what's visible
    public static IReadOnlyList<PartitionSpec> Partitions(long visibleLength)
    {
        long contentLength = Math.Max(0, visibleLength - ContentOffset);
        return new[]
        {
            new PartitionSpec(SystemCacheId, "System Cache", SystemCacheOffset, SystemCacheLength,
                PartitionFileSystem.Sfcx, "Console cache. Not FATX, can't be browsed."),
            new PartitionSpec(GameCacheId, "Game Cache", GameCacheOffset, GameCacheLength - SysExtRegionLength,
                PartitionFileSystem.Sfcx, "Console cache. Not FATX, can't be browsed."),
            new PartitionSpec(SystemExtId, "System Extended", SystemExtOffset, SystemExtLength,
                PartitionFileSystem.Fatx, "Kinect and Avatar system files."),
            new PartitionSpec(SystemExt2Id, "System Extended 2", SystemExt2Offset, SystemExt2Length,
                PartitionFileSystem.Fatx, "Second system extended partition."),
            new PartitionSpec(CompatId, "Xbox 1 Compatibility", CompatOffset, CompatLength,
                PartitionFileSystem.Fatx, "Original Xbox backwards compatibility."),
            new PartitionSpec(ContentId, "Content", ContentOffset, contentLength,
                PartitionFileSystem.Fatx, "Games, DLC, saves, profiles."),
        };
    }

    // the game cache region as the public docs list it also covers the two sysext partitions,
    // so the part that's really cache stops where SysExt starts
    const long SysExtRegionLength = SystemExtLength + SystemExt2Length;

    public static PartitionSpec? Find(string idOrAlias, long visibleLength)
    {
        string id = Normalize(idOrAlias);
        return Partitions(visibleLength).FirstOrDefault(p => p.Id == id);
    }

    public static string Normalize(string idOrAlias)
    {
        string s = idOrAlias.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
        return s switch
        {
            "content" or "data" or "hdd1" or "partition3" => ContentId,
            "compat" or "compatibility" or "xbox1" or "backcompat" or "backwardscompatibility" or "hddx" or "partition2" => CompatId,
            "sysext" or "systemextended" => SystemExtId,
            "sysext2" or "systemextended2" => SystemExt2Id,
            "syscache" or "systemcache" or "cache0" => SystemCacheId,
            "gamecache" or "cache1" => GameCacheId,
            _ => s,
        };
    }
}
