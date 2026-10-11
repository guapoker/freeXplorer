using System.Buffers.Binary;
using System.Security.Cryptography;
using FreeXplorer.Storage;

namespace FreeXplorer.Fatx;

public sealed class FatxFormatOptions
{
    public FatxFlavor Flavor { get; init; } = FatxFlavor.Xbox360;

    // 0 means pick one from the partition size
    public int SectorsPerCluster { get; init; }

    public uint? VolumeId { get; init; }
    public string? Label { get; init; }
}

public static class FatxFormatter
{
    // Writes an empty FATX volume into [offset, offset + length) of the device and nothing
    // outside of it. Whatever was in the data area stays there, it's just unreferenced.
    public static FatxGeometry Format(IBlockDevice device, long offset, long length,
        FatxFormatOptions? options = null, Action<double>? progress = null)
    {
        options ??= new FatxFormatOptions();
        if (!device.CanWrite)
            throw new FatxException(FatxError.ReadOnly, "the device is read-only");
        if (offset < 0 || length <= 0 || offset > device.Length - length)
            throw new ArgumentOutOfRangeException(nameof(length), "partition doesn't fit on the device");

        int spc = options.SectorsPerCluster != 0
            ? options.SectorsPerCluster
            : FatxGeometry.PickSectorsPerCluster(length);
        var geometry = FatxGeometry.Compute(length, spc);
        if (geometry.ClusterCount > FatxGeometry.MaxDataClusters)
            throw new FatxException(FatxError.InvalidOperation,
                $"{geometry.ClusterCount} clusters is more than the console accepts, use a bigger cluster size");

        bool big = options.Flavor == FatxFlavor.Xbox360;
        uint volumeId = options.VolumeId ?? (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);

        // header: signature, id, sectors per cluster, root cluster, two zero bytes, then 0xFF to the end of the
        // page. That's what a console-formatted drive has.
        var header = new byte[FatxGeometry.HeaderSize];
        Array.Fill(header, (byte)0xFF);
        (big ? "XTAF"u8 : "FATX"u8).CopyTo(header);
        WriteU32(header.AsSpan(4), volumeId, big);
        WriteU32(header.AsSpan(8), (uint)spc, big);
        WriteU32(header.AsSpan(12), 1, big);
        header[0x10] = 0;
        header[0x11] = 0;
        device.Write(offset, header);

        // fat: everything free apart from entry 0 (media) and entry 1 (the root directory's single cluster)
        long fatStart = offset + geometry.FatOffset;
        const int chunk = 1 << 20;
        var zeros = new byte[(int)Math.Min(chunk, geometry.FatSize)];
        for (long written = 0; written < geometry.FatSize;)
        {
            int n = (int)Math.Min(zeros.Length, geometry.FatSize - written);
            if (written == 0)
            {
                var first = new byte[n];
                SetEntry(first, geometry, 0, geometry.MediaEntry, big);
                SetEntry(first, geometry, 1, geometry.EndOfChain, big);
                device.Write(fatStart, first);
            }
            else
            {
                device.Write(fatStart + written, zeros.AsSpan(0, n));
            }
            written += n;
            progress?.Invoke((double)written / geometry.FatSize);
        }

        // empty root directory: a cluster of 0xFF is "no entries"
        var root = new byte[geometry.ClusterSize];
        Array.Fill(root, (byte)0xFF);
        device.Write(offset + geometry.ClusterOffset(1), root);
        device.Flush();

        if (!string.IsNullOrEmpty(options.Label))
        {
            using var slice = new SliceDevice(device, offset, length, writable: true);
            using var volume = FatxVolume.Open(slice, writable: true);
            volume.Label = options.Label;
        }
        return geometry;
    }

    static void SetEntry(byte[] fat, FatxGeometry g, int index, uint value, bool big)
    {
        var span = fat.AsSpan(index * g.FatEntrySize, g.FatEntrySize);
        if (g.FatEntrySize == 2)
        {
            if (big) BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)value);
            else BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)value);
        }
        else
        {
            WriteU32(span, value, big);
        }
    }

    static void WriteU32(Span<byte> span, uint value, bool big)
    {
        if (big) BinaryPrimitives.WriteUInt32BigEndian(span, value);
        else BinaryPrimitives.WriteUInt32LittleEndian(span, value);
    }
}
