namespace FreeXplorer.Fatx;

// XTAF is the 360 flavor (big endian). The original Xbox wrote FATX, little endian.
public enum FatxFlavor
{
    Xbox360,
    Xbox,
}

[Flags]
public enum FatxAttributes : byte
{
    None = 0,
    ReadOnly = 0x01,
    Hidden = 0x02,
    System = 0x04,
    Volume = 0x08,
    Directory = 0x10,
    Archive = 0x20,
}

public enum FatxError
{
    NotFatx,
    Corrupt,
    NotFound,
    AlreadyExists,
    NotADirectory,
    IsADirectory,
    DirectoryNotEmpty,
    InvalidName,
    PathTooLong,
    DirectoryFull,
    DiskFull,
    FileTooLarge,
    ReadOnly,
    InvalidOperation,
}

public sealed class FatxException : IOException
{
    public FatxError Error { get; }

    public FatxException(FatxError error, string message) : base(message)
    {
        Error = error;
    }
}
