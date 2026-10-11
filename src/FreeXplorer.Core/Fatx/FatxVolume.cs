using System.Buffers.Binary;
using System.Text;
using FreeXplorer.Storage;

namespace FreeXplorer.Fatx;

// A mounted FATX partition. Every public method takes the volume lock, so one volume
// can be shared between threads (WinFsp calls us from a pool).
//
// FAT edits are held in memory and flushed before the matching directory entry is
// written, so a crash can leave lost clusters behind (fsck finds those) but should
// never leave an entry pointing at a chain that isn't on disk.
public sealed partial class FatxVolume : IDisposable
{
    public const int MaxEntriesPerDirectory = 0x1000;
    public const long MaxFileSize = uint.MaxValue;
    const int EntrySize = 64;

    readonly IBlockDevice device;
    readonly bool ownsDevice;
    readonly FatTable fat;
    readonly bool bigEndian;
    readonly int entriesPerCluster;
    readonly HashSet<FatxNode> dirty = new();
    readonly object sync = new();

    uint hint = 1;
    long freeClusters;
    bool freeKnown;
    bool disposed;

    FatxVolume(IBlockDevice device, bool ownsDevice, bool writable, FatxFlavor flavor,
        FatxGeometry geometry, uint volumeId, uint rootCluster)
    {
        this.device = device;
        this.ownsDevice = ownsDevice;
        Flavor = flavor;
        bigEndian = flavor == FatxFlavor.Xbox360;
        Geometry = geometry;
        VolumeId = volumeId;
        IsReadOnly = !(writable && device.CanWrite);
        entriesPerCluster = geometry.ClusterSize / EntrySize;
        fat = new FatTable(device, geometry, bigEndian);
        freeClusters = geometry.ClusterCount;

        Root = new FatxNode(flavor, "", FatxAttributes.Directory) { FirstCluster = rootCluster };
    }

    public FatxFlavor Flavor { get; }
    public FatxGeometry Geometry { get; }
    public uint VolumeId { get; }
    public bool IsReadOnly { get; }
    public FatxNode Root { get; }

    public int ClusterSize => Geometry.ClusterSize;
    public long TotalBytes => (long)Geometry.ClusterCount * Geometry.ClusterSize;
    public bool FreeSpaceKnown => freeKnown;

    public long FreeBytes
    {
        get { lock (sync) return freeClusters * Geometry.ClusterSize; }
    }

    public static bool HasSignature(IBlockDevice partition)
    {
        if (partition.Length < FatxGeometry.HeaderSize)
            return false;
        Span<byte> head = stackalloc byte[4];
        partition.Read(0, head);
        return head.SequenceEqual("XTAF"u8) || head.SequenceEqual("FATX"u8);
    }

    public static FatxVolume Open(IBlockDevice partition, bool writable = false, bool ownsDevice = false)
    {
        try
        {
            if (partition.Length < FatxGeometry.HeaderSize + FatxGeometry.SectorSize)
                throw new FatxException(FatxError.NotFatx, "too small to be a FATX partition");

            Span<byte> head = stackalloc byte[16];
            partition.Read(0, head);

            FatxFlavor flavor;
            if (head[..4].SequenceEqual("XTAF"u8))
                flavor = FatxFlavor.Xbox360;
            else if (head[..4].SequenceEqual("FATX"u8))
                flavor = FatxFlavor.Xbox;
            else
                throw new FatxException(FatxError.NotFatx, "no XTAF or FATX signature at the start of the partition");

            bool big = flavor == FatxFlavor.Xbox360;
            uint id = big ? BinaryPrimitives.ReadUInt32BigEndian(head[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            uint spc = big ? BinaryPrimitives.ReadUInt32BigEndian(head[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
            uint root = big ? BinaryPrimitives.ReadUInt32BigEndian(head[12..]) : BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);

            if (spc > 128 || !FatxGeometry.IsValidSectorsPerCluster((int)spc))
                throw new FatxException(FatxError.NotFatx, $"header says {spc} sectors per cluster, that can't be right");

            var geometry = FatxGeometry.Compute(partition.Length, (int)spc);
            if (root < 1 || root > geometry.ClusterCount)
                throw new FatxException(FatxError.Corrupt, $"root directory cluster {root} is out of range");

            return new FatxVolume(partition, ownsDevice, writable, flavor, geometry, id, root);
        }
        catch
        {
            if (ownsDevice)
                partition.Dispose();
            throw;
        }
    }

    // Walks the whole FAT once to count free clusters. Until this runs FreeBytes is just
    // the size of the volume. Cheap on small drives, a few seconds on a multi-TB one.
    public void ScanFreeSpace(Action<double>? progress = null)
    {
        lock (sync)
        {
            freeClusters = fat.CountFree(Geometry.ClusterCount, progress);
            freeKnown = true;
        }
    }

    public void Flush()
    {
        lock (sync)
        {
            foreach (var node in dirty.ToList())
            {
                if (!node.IsDeleted)
                    WriteEntry(node);
            }
            dirty.Clear();
            fat.Flush();
            device.Flush();
        }
    }

    // Writes just this node's directory entry if it has pending changes
    public void FlushNode(FatxNode node)
    {
        lock (sync)
        {
            if (dirty.Remove(node) && !node.IsDeleted)
                WriteEntry(node);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                if (!IsReadOnly)
                    Flush();
            }
            finally
            {
                if (ownsDevice)
                    device.Dispose();
            }
        }
    }

    // ---- small helpers shared by the partial files ----

    FatxException Fail(FatxError error, string message) => new(error, message);

    void RequireWritable()
    {
        if (IsReadOnly)
            throw Fail(FatxError.ReadOnly, "the volume is mounted read-only");
    }

    static void RequireDirectory(FatxNode node)
    {
        if (!node.IsDirectory)
            throw new FatxException(FatxError.NotADirectory, $"{node.Path} is not a directory");
    }

    static void RequireFile(FatxNode node)
    {
        if (node.IsDirectory)
            throw new FatxException(FatxError.IsADirectory, $"{node.Path} is a directory");
    }

    void RequireLive(FatxNode node)
    {
        if (node.IsDeleted)
            throw Fail(FatxError.NotFound, $"{node.Name} was deleted");
    }

    uint ReadU32(ReadOnlySpan<byte> b) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);

    void WriteU32(Span<byte> b, uint v)
    {
        if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, v);
        else BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    }

    uint NowRaw() => FatxTime.Encode(DateTime.Now, Flavor);

    // The volume label lives in a file called name.txt in the root. UTF-16BE, with a BOM.
    public string? Label
    {
        get
        {
            lock (sync)
            {
                var file = GetListing(Root).ByName.GetValueOrDefault("name.txt");
                if (file is null || file.IsDirectory || file.Size < 2 || file.Size > 128)
                    return null;
                var raw = new byte[file.Size];
                Read(file, 0, raw);
                int skip = raw[0] == 0xFE && raw[1] == 0xFF ? 2 : 0;
                return Encoding.BigEndianUnicode.GetString(raw, skip, raw.Length - skip).TrimEnd('\0');
            }
        }
        set
        {
            lock (sync)
            {
                RequireWritable();
                var listing = GetListing(Root);
                if (string.IsNullOrEmpty(value))
                {
                    if (listing.ByName.TryGetValue("name.txt", out var old))
                        Delete(old);
                    return;
                }
                if (value.Length > 15)
                    throw Fail(FatxError.InvalidName, "volume labels can be 15 characters at most");
                var bytes = new byte[2 + value.Length * 2];
                bytes[0] = 0xFE;
                bytes[1] = 0xFF;
                Encoding.BigEndianUnicode.GetBytes(value, 0, value.Length, bytes, 2);
                var file = listing.ByName.GetValueOrDefault("name.txt") ?? CreateFile(Root, "name.txt");
                SetLength(file, 0);
                Write(file, 0, bytes);
            }
        }
    }
}
