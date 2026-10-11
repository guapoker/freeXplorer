namespace FreeXplorer.Fatx;

// Stream view of a file so it works with CopyTo and friends
public sealed class FatxStream : Stream
{
    readonly FatxVolume volume;
    readonly FatxNode node;
    readonly bool writable;
    long position;

    public FatxStream(FatxVolume volume, FatxNode node, bool writable = false)
    {
        this.volume = volume;
        this.node = node;
        this.writable = writable;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => writable;
    public override long Length => node.Size;

    public override long Position
    {
        get => position;
        set => position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = volume.Read(node, position, buffer);
        position += n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!writable)
            throw new NotSupportedException("stream was opened for reading");
        volume.Write(node, position, buffer);
        position += buffer.Length;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            _ => node.Size + offset,
        };
        Position = target;
        return position;
    }

    public override void SetLength(long value)
    {
        if (!writable)
            throw new NotSupportedException("stream was opened for reading");
        volume.SetLength(node, value);
    }

    public override void Flush()
    {
        if (writable)
            volume.FlushNode(node);
    }
}
