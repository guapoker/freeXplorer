using System.Diagnostics;
using System.Security.Cryptography;

namespace FreeXplorer.Storage;

// How far a long copy has got
public sealed record CopyProgress(string Phase, long Done, long Total, double BytesPerSecond)
{
    public double Fraction => Total <= 0 ? 1 : Math.Min(1.0, (double)Done / Total);

    public TimeSpan? Remaining => BytesPerSecond >= 1 && Done < Total
        ? TimeSpan.FromSeconds((Total - Done) / BytesPerSecond)
        : null;

    // "12.3 GB of 55.9 GB, 38 MB/s, about 19 min left"
    public string Summary
    {
        get
        {
            string text = $"{Size.Format(Done)} of {Size.Format(Total)}";
            if (BytesPerSecond >= 1)
                text += $", {Size.Format((long)BytesPerSecond)}/s";
            if (Remaining is { } left)
                text += $", {HowLong(left)} left";
            return text;
        }
    }

    public static string HowLong(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"about {(int)time.TotalHours} h {time.Minutes} min";
        if (time.TotalMinutes >= 1.5)
            return $"about {Math.Round(time.TotalMinutes)} min";
        return $"{Math.Max(1, (int)Math.Round(time.TotalSeconds))} s";
    }
}

public sealed class CopyOptions
{
    // Leave blocks that are all zeros unwritten. Only right when the destination is a brand new
    // sparse file, anywhere else the zeros have to land on top of whatever is there.
    public bool SkipZeroBlocks { get; init; }

    // flush the destination every so many bytes, 0 means only at the end
    public long FlushEvery { get; init; }
}

// Sequential whole-device copy and read-back, both hashing what goes through them
public static class DriveCopy
{
    public const int ChunkSize = 4 << 20;
    const int ZeroBlock = 64 << 10;

    // Copies the bytes from `start` up to `length` and returns their SHA-256 as lowercase hex.
    // Nothing before `start` is read or written.
    public static string Copy(IBlockDevice from, IBlockDevice to, long length, CopyOptions options,
        Action<CopyProgress>? progress, CancellationToken cancel, string phase = "Copying", long start = 0)
    {
        if (length < 0 || length > from.Length)
            throw new ArgumentOutOfRangeException(nameof(length), $"{from.Name} is only {from.Length} bytes");
        if (length > to.Length)
            throw new ArgumentOutOfRangeException(nameof(length), $"{to.Name} is only {to.Length} bytes");
        if (start < 0 || start > length)
            throw new ArgumentOutOfRangeException(nameof(start), "the start is outside the copy");
        if (!to.CanWrite)
            throw new NotSupportedException($"{to.Name} was opened read-only");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        var meter = new Meter(phase, length - start, progress);
        long done = start, unflushed = 0;

        while (done < length)
        {
            cancel.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buffer.Length, length - done);
            var chunk = buffer.AsSpan(0, n);

            from.Read(done, chunk);
            hash.AppendData(chunk);
            if (options.SkipZeroBlocks)
                WriteNonZero(to, done, chunk);
            else
                to.Write(done, chunk);

            done += n;
            unflushed += n;
            if (options.FlushEvery > 0 && unflushed >= options.FlushEvery)
            {
                to.Flush();
                unflushed = 0;
            }
            meter.Report(done - start);
        }
        to.Flush();
        meter.Report(done - start, force: true);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // Reads the bytes from `start` up to `length` and returns their SHA-256 as lowercase hex
    public static string Hash(IBlockDevice device, long length, Action<CopyProgress>? progress,
        CancellationToken cancel, string phase = "Checking", long start = 0)
    {
        if (length < 0 || length > device.Length)
            throw new ArgumentOutOfRangeException(nameof(length), $"{device.Name} is only {device.Length} bytes");
        if (start < 0 || start > length)
            throw new ArgumentOutOfRangeException(nameof(start), "the start is outside the range to read");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        var meter = new Meter(phase, length - start, progress);
        long done = start;

        while (done < length)
        {
            cancel.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buffer.Length, length - done);
            device.Read(done, buffer.AsSpan(0, n));
            hash.AppendData(buffer.AsSpan(0, n));
            done += n;
            meter.Report(done - start);
        }
        meter.Report(done - start, force: true);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // writes only the stretches that have something in them, so the holes of a sparse file stay holes
    static void WriteNonZero(IBlockDevice to, long offset, ReadOnlySpan<byte> chunk)
    {
        int pos = 0;
        while (pos < chunk.Length)
        {
            while (pos < chunk.Length && IsEmpty(chunk, pos))
                pos += ZeroBlock;
            if (pos >= chunk.Length)
                break;

            int start = pos;
            while (pos < chunk.Length && !IsEmpty(chunk, pos))
                pos += ZeroBlock;
            int end = Math.Min(pos, chunk.Length);
            to.Write(offset + start, chunk[start..end]);
        }
    }

    static bool IsEmpty(ReadOnlySpan<byte> chunk, int pos) =>
        chunk.Slice(pos, Math.Min(ZeroBlock, chunk.Length - pos)).IndexOfAnyExcept((byte)0) < 0;

    // speed over the last few seconds, and no more than a handful of reports a second
    sealed class Meter
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Queue<(double Seconds, long Done)> samples = new();
        readonly string phase;
        readonly long total;
        readonly Action<CopyProgress>? sink;
        double lastReport = double.NegativeInfinity;

        public Meter(string phase, long total, Action<CopyProgress>? sink)
        {
            this.phase = phase;
            this.total = total;
            this.sink = sink;
            samples.Enqueue((0, 0));
            sink?.Invoke(new CopyProgress(phase, 0, total, 0));
        }

        public void Report(long done, bool force = false)
        {
            if (sink is null)
                return;
            double now = clock.Elapsed.TotalSeconds;
            samples.Enqueue((now, done));
            while (samples.Count > 2 && now - samples.Peek().Seconds > 8)
                samples.Dequeue();
            if (!force && now - lastReport < 0.2)
                return;
            lastReport = now;

            var (then, before) = samples.Peek();
            double speed = now - then > 0.5 ? (done - before) / (now - then) : 0;
            sink(new CopyProgress(phase, done, total, speed));
        }
    }
}
