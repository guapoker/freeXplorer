using Microsoft.Win32.SafeHandles;

namespace FreeXplorer.Storage;

// A raw disk image on disk. The file never grows, writes past the end are rejected.
public sealed class FileBlockDevice : IBlockDevice
{
    readonly SafeFileHandle handle;
    readonly long length;

    public FileBlockDevice(string path, bool writable)
    {
        Name = Path.GetFullPath(path);
        CanWrite = writable;
        handle = File.OpenHandle(
            Name,
            FileMode.Open,
            writable ? FileAccess.ReadWrite : FileAccess.Read,
            writable ? FileShare.Read : FileShare.ReadWrite,
            FileOptions.RandomAccess);
        length = RandomAccess.GetLength(handle);
    }

    public string Name { get; }
    public long Length => length;
    public int SectorSize => 512;
    public bool CanWrite { get; }

    public static FileBlockDevice CreateImage(string path, long length, bool sparse = true)
    {
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            if (sparse)
                SparseFile.TryMark(fs.SafeFileHandle);
            fs.SetLength(length);
        }
        FileOwner.GiveToSudoUser(path);
        return new FileBlockDevice(path, true);
    }

    public void Read(long offset, Span<byte> buffer)
    {
        BlockDeviceExtensions.CheckRange(this, offset, buffer.Length);
        int done = 0;
        while (done < buffer.Length)
        {
            int n = RandomAccess.Read(handle, buffer[done..], offset + done);
            if (n <= 0)
                throw new EndOfStreamException($"unexpected end of {Name} at {offset + done}");
            done += n;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite)
            throw new NotSupportedException("image was opened read-only");
        BlockDeviceExtensions.CheckRange(this, offset, buffer.Length);
        RandomAccess.Write(handle, buffer, offset);
    }

    public void Flush()
    {
        if (CanWrite)
            RandomAccess.FlushToDisk(handle);
    }

    public void Dispose() => handle.Dispose();
}
