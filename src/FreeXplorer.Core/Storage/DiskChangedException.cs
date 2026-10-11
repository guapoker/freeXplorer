namespace FreeXplorer.Storage;

// The number on screen doesn't point at the drive that was listed any more
public sealed class DiskChangedException : IOException
{
    public DiskChangedException(string message) : base(message)
    {
    }
}
