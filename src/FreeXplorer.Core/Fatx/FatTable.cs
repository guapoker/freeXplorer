using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FreeXplorer.Storage;

namespace FreeXplorer.Fatx;

// The allocation table, read in 4K pages as needed. On a big drive the FAT is hundreds of
// MB so we can't just load it. Writes stay in memory until Flush().
// Not thread safe, the volume holds a lock around every call.
sealed class FatTable
{
    const int PageSize = 4096;
    const int ReadAhead = 16;

    readonly IBlockDevice device;
    readonly long start;
    readonly int entrySize;
    readonly bool bigEndian;
    readonly long pageCount;
    readonly int maxCached;

    readonly Dictionary<long, Page> cache = new();
    readonly LinkedList<Page> recent = new();

    sealed class Page
    {
        public long Number;
        public byte[] Data = new byte[PageSize];
        public bool Dirty;
        public LinkedListNode<Page>? Node;
    }

    public FatTable(IBlockDevice device, FatxGeometry geometry, bool bigEndian, int maxCachedPages = 16384)
    {
        this.device = device;
        start = geometry.FatOffset;
        entrySize = geometry.FatEntrySize;
        this.bigEndian = bigEndian;
        pageCount = geometry.FatSize / PageSize;
        maxCached = maxCachedPages;
        Capacity = geometry.FatSize / entrySize;
    }

    public long Capacity { get; }

    public uint Get(uint index)
    {
        long at = (long)index * entrySize;
        var page = GetPage(at / PageSize);
        var span = page.Data.AsSpan((int)(at % PageSize), entrySize);
        if (entrySize == 2)
            return bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);
        return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
    }

    public void Set(uint index, uint value)
    {
        long at = (long)index * entrySize;
        var page = GetPage(at / PageSize);
        var span = page.Data.AsSpan((int)(at % PageSize), entrySize);
        if (entrySize == 2)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)value);
            else BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)value);
        }
        else
        {
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(span, value);
            else BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        }
        page.Dirty = true;
    }

    public void Flush()
    {
        var dirty = cache.Values.Where(p => p.Dirty).OrderBy(p => p.Number).ToList();
        if (dirty.Count == 0)
            return;

        // write neighbouring pages in one go
        int i = 0;
        while (i < dirty.Count)
        {
            int j = i;
            while (j + 1 < dirty.Count && dirty[j + 1].Number == dirty[j].Number + 1 && j - i < 255)
                j++;
            var buffer = new byte[(j - i + 1) * PageSize];
            for (int k = i; k <= j; k++)
                dirty[k].Data.CopyTo(buffer, (k - i) * PageSize);
            device.Write(start + dirty[i].Number * PageSize, buffer);
            for (int k = i; k <= j; k++)
                dirty[k].Dirty = false;
            i = j + 1;
        }
    }

    // Counts entries equal to zero for clusters 1..last by reading the table straight off the
    // device in big chunks.
    public long CountFree(uint last, Action<double>? progress = null)
    {
        Flush();
        const int chunk = 1 << 20;
        long total = Math.Min((long)last + 1, Capacity) * entrySize;
        long free = 0;
        var buffer = new byte[chunk];
        long pos = 0;
        while (pos < total)
        {
            int n = (int)Math.Min(chunk, total - pos);
            device.Read(start + pos, buffer.AsSpan(0, n));
            int skip = pos == 0 ? entrySize : 0; // entry 0 is the media byte, not a cluster
            if (entrySize == 4)
            {
                var words = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, n / 4 * 4));
                for (int k = skip / 4; k < words.Length; k++)
                    if (words[k] == 0) free++;
            }
            else
            {
                var words = MemoryMarshal.Cast<byte, ushort>(buffer.AsSpan(0, n / 2 * 2));
                for (int k = skip / 2; k < words.Length; k++)
                    if (words[k] == 0) free++;
            }
            pos += n;
            progress?.Invoke((double)pos / total);
        }
        return free;
    }

    Page GetPage(long number)
    {
        if (cache.TryGetValue(number, out var hit))
        {
            Touch(hit);
            return hit;
        }
        if (number < 0 || number >= pageCount)
            throw new FatxException(FatxError.Corrupt, $"FAT page {number} is outside the table");

        long first = number - number % ReadAhead;
        int count = (int)Math.Min(ReadAhead, pageCount - first);
        var raw = new byte[count * PageSize];
        device.Read(start + first * PageSize, raw);
        for (int i = 0; i < count; i++)
        {
            long n = first + i;
            if (cache.ContainsKey(n))
                continue; // might hold edits that aren't on disk yet
            var page = new Page { Number = n };
            Buffer.BlockCopy(raw, i * PageSize, page.Data, 0, PageSize);
            page.Node = recent.AddFirst(page);
            cache[n] = page;
        }
        var wanted = cache[number];
        Touch(wanted);
        Trim(wanted);
        return wanted;
    }

    void Touch(Page page)
    {
        if (page.Node != recent.First)
        {
            recent.Remove(page.Node!);
            recent.AddFirst(page.Node!);
        }
    }

    void Trim(Page keep)
    {
        while (cache.Count > maxCached)
        {
            var victim = recent.Last!.Value;
            if (victim == keep)
                break;
            if (victim.Dirty)
            {
                device.Write(start + victim.Number * PageSize, victim.Data);
                victim.Dirty = false;
            }
            recent.RemoveLast();
            cache.Remove(victim.Number);
        }
    }
}
