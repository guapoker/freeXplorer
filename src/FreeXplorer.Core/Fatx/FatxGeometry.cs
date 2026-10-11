namespace FreeXplorer.Fatx;

// Where everything lives inside a partition. The console works all of this out from the
// partition size alone, there's nothing on disk that says how big the FAT is, so the
// maths here has to match it exactly or every cluster ends up in the wrong place.
public sealed class FatxGeometry
{
    public const int SectorSize = 512;
    public const int HeaderSize = 0x1000;
    public const int MaxFileNameLength = 42;
    public const long MaxDataClusters = 0x10000000;

    // 16 bit entries below this many clusters, 32 bit from here up
    const long Fat16Limit = 0xFFF0;

    public long PartitionLength { get; }
    public int SectorsPerCluster { get; }
    public int ClusterSize { get; }
    public int FatEntrySize { get; }
    public long FatOffset => HeaderSize;
    public long FatSize { get; }
    public long DataOffset { get; }
    public uint ClusterCount { get; }

    public bool IsFat32 => FatEntrySize == 4;
    public uint EndOfChain => IsFat32 ? 0xFFFFFFFFu : 0xFFFFu;
    public uint MediaEntry => IsFat32 ? 0xFFFFFFF8u : 0xFFF8u;
    public uint BadCluster => IsFat32 ? 0xFFFFFFF7u : 0xFFF7u;
    // anything at or above this ends a chain
    public uint EndThreshold => IsFat32 ? 0xFFFFFFF8u : 0xFFF8u;

    FatxGeometry(long partitionLength, int sectorsPerCluster, int entrySize, long fatSize, long dataOffset, long clusters)
    {
        PartitionLength = partitionLength;
        SectorsPerCluster = sectorsPerCluster;
        ClusterSize = sectorsPerCluster * SectorSize;
        FatEntrySize = entrySize;
        FatSize = fatSize;
        DataOffset = dataOffset;
        ClusterCount = (uint)Math.Min(clusters, uint.MaxValue - 16);
    }

    public static bool IsValidSectorsPerCluster(int spc) => spc is >= 1 and <= 128 && (spc & (spc - 1)) == 0;

    public static FatxGeometry Compute(long partitionLength, int sectorsPerCluster)
    {
        if (!IsValidSectorsPerCluster(sectorsPerCluster))
            throw new FatxException(FatxError.NotFatx, $"{sectorsPerCluster} sectors per cluster doesn't make sense");

        long sectors = partitionLength / SectorSize;
        long maxClusters = sectors / sectorsPerCluster + 1;
        int entry = maxClusters < Fat16Limit ? 2 : 4;
        long fatSize = RoundUp(maxClusters * entry, HeaderSize);
        long dataOffset = HeaderSize + fatSize;
        long usable = (sectors * SectorSize - dataOffset) / (sectorsPerCluster * SectorSize);
        if (usable < 2)
            throw new FatxException(FatxError.NotFatx, "partition is too small to hold a FATX volume");
        return new FatxGeometry(sectors * SectorSize, sectorsPerCluster, entry, fatSize, dataOffset, usable);
    }

    // Smallest standard cluster size (16K and up) that keeps the volume under the console's
    // cluster limit. Anything the 360 formats itself uses 16K, bigger drives need more.
    public static int PickSectorsPerCluster(long partitionLength)
    {
        for (int spc = 0x20; spc <= 0x80; spc *= 2)
        {
            long sectors = partitionLength / SectorSize;
            long maxClusters = sectors / spc + 1;
            if (maxClusters - 1 <= MaxDataClusters)
                return spc;
        }
        return 0x80;
    }

    public long ClusterOffset(uint cluster) => DataOffset + (long)(cluster - 1) * ClusterSize;

    static long RoundUp(long value, long multiple) => (value + multiple - 1) / multiple * multiple;
}
