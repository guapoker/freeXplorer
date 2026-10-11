using FreeXplorer.Fatx;
using FreeXplorer.Storage;

namespace FreeXplorer.Tests;

// Keeps every write in order so it can be replayed up to any point
sealed class JournalDevice : IBlockDevice
{
    readonly IBlockDevice inner;
    public JournalDevice(IBlockDevice inner) => this.inner = inner;

    public List<(long Offset, byte[] Data)> Writes { get; } = new();

    public string Name => inner.Name;
    public long Length => inner.Length;
    public int SectorSize => inner.SectorSize;
    public bool CanWrite => true;

    public void Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        Writes.Add((offset, buffer.ToArray()));
        inner.Write(offset, buffer);
    }

    public void Flush() => inner.Flush();
    public void Dispose() { }
}

// Power cut simulation. Replay the first N writes of a busy session onto a fresh image
// for lots of values of N and make sure the result is always a volume that opens, lists,
// and has no damage beyond some leaked clusters.
public class CrashTests
{
    const long Size = 24L << 20;

    static MemoryBlockDevice FreshImage()
    {
        var dev = new MemoryBlockDevice(Size);
        FatxFormatter.Format(dev, 0, Size, new FatxFormatOptions { VolumeId = 1 });
        return dev;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_prefix_of_the_writes_is_a_usable_volume(int seed)
    {
        var live = new JournalDevice(FreshImage());
        using (var vol = FatxVolume.Open(live, writable: true))
        {
            var model = new FuzzModel();
            var rng = new Random(seed);
            for (int i = 0; i < 120; i++)
                FuzzDriver.Step(vol, model, rng, allowMoves: false, maxSize: 200_000);
            vol.Flush();
        }

        var replica = FreshImage();
        int checkedStates = 0;
        for (int n = 0; n <= live.Writes.Count; n++)
        {
            if (n > 0)
                replica.Write(live.Writes[n - 1].Offset, live.Writes[n - 1].Data);

            // look at every state early on, then every few writes
            if (n > 80 && n % 5 != 0 && n != live.Writes.Count)
                continue;

            using var seen = FatxVolume.Open(replica, writable: false);
            var report = seen.Check();
            foreach (var issue in report.Issues)
            {
                bool leak = issue.Message.Contains("belong to nothing") || issue.Message.Contains("more clusters than the size needs");
                Assert.True(issue.Severity != FsckSeverity.Error && leak,
                    $"after {n} of {live.Writes.Count} writes: {issue.Severity}: {issue.Message}");
            }
            checkedStates++;
        }
        Assert.True(checkedStates > 50, "test didn't look at enough states to mean anything");
    }

    // the finished image must be exactly what a replay of the whole journal produces
    [Fact]
    public void The_journal_covers_every_write()
    {
        var inner = FreshImage();
        var live = new JournalDevice(inner);
        using (var vol = FatxVolume.Open(live, writable: true))
        {
            var model = new FuzzModel();
            var rng = new Random(77);
            for (int i = 0; i < 60; i++)
                FuzzDriver.Step(vol, model, rng, allowMoves: false, maxSize: 100_000);
        }

        var replica = FreshImage();
        foreach (var (offset, data) in live.Writes)
            replica.Write(offset, data);

        var a = new byte[1 << 20];
        var b = new byte[1 << 20];
        for (long pos = 0; pos < Size; pos += a.Length)
        {
            inner.Read(pos, a);
            replica.Read(pos, b);
            Assert.True(a.AsSpan().SequenceEqual(b), $"images differ in the megabyte at {pos}");
        }
    }
}
