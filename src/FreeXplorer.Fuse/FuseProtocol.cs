using System.Runtime.InteropServices;

namespace FreeXplorer.Mount;

// The parts of the kernel's FUSE protocol (include/uapi/linux/fuse.h) this file system speaks.
// The layouts are a stable ABI, so talking to /dev/fuse directly avoids depending on a
// particular libfuse version.
static class Fuse
{
    public const uint KernelVersion = 7;
    public const uint KernelMinorVersion = 31;
    public const uint MaxWrite = 128 * 1024;
    public const int BufferSize = (int)MaxWrite + 4096;

    public const ulong RootId = 1;

    public enum Opcode : uint
    {
        Lookup = 1,
        Forget = 2,
        GetAttr = 3,
        SetAttr = 4,
        ReadLink = 5,
        Symlink = 6,
        MkNod = 8,
        MkDir = 9,
        Unlink = 10,
        RmDir = 11,
        Rename = 12,
        Link = 13,
        Open = 14,
        Read = 15,
        Write = 16,
        StatFs = 17,
        Release = 18,
        FSync = 20,
        SetXAttr = 21,
        GetXAttr = 22,
        ListXAttr = 23,
        RemoveXAttr = 24,
        Flush = 25,
        Init = 26,
        OpenDir = 27,
        ReadDir = 28,
        ReleaseDir = 29,
        FSyncDir = 30,
        Access = 34,
        Create = 35,
        Interrupt = 36,
        Destroy = 38,
        BatchForget = 42,
        Rename2 = 45,
    }

    // init flags
    public const uint AsyncRead = 1 << 0;
    public const uint BigWrites = 1 << 5;
    public const uint MaxPages = 1 << 22;

    // setattr valid bits
    public const uint SetMode = 1 << 0;
    public const uint SetSize = 1 << 3;
    public const uint SetATime = 1 << 4;
    public const uint SetMTime = 1 << 5;
    public const uint SetATimeNow = 1 << 7;
    public const uint SetMTimeNow = 1 << 8;

    public const uint RenameNoReplace = 1;
    public const uint RenameExchange = 2;

    public const uint ModeDirectory = 0x4000;   // S_IFDIR
    public const uint ModeFile = 0x8000;        // S_IFREG
    public const uint ModeTypeMask = 0xF000;
    public const uint TypeDirectory = 4;        // DT_DIR
    public const uint TypeFile = 8;             // DT_REG

    public const int OAccessMode = 3;
    public const int OReadOnly = 0;
}

static class Errno
{
    public const int EPERM = 1;
    public const int ENOENT = 2;
    public const int EINTR = 4;
    public const int EIO = 5;
    public const int EBADF = 9;
    public const int EAGAIN = 11;
    public const int EACCES = 13;
    public const int EEXIST = 17;
    public const int ENODEV = 19;
    public const int ENOTDIR = 20;
    public const int EISDIR = 21;
    public const int EINVAL = 22;
    public const int EFBIG = 27;
    public const int ENOSPC = 28;
    public const int EROFS = 30;
    public const int ERANGE = 34;
    public const int ENAMETOOLONG = 36;
    public const int ENOSYS = 38;
    public const int ENOTEMPTY = 39;
    public const int ENODATA = 61;
    public const int EOPNOTSUPP = 95;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseInHeader
{
    public uint Length;
    public Fuse.Opcode Opcode;
    public ulong Unique;
    public ulong NodeId;
    public uint Uid;
    public uint Gid;
    public uint Pid;
    public uint Padding;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseOutHeader
{
    public uint Length;
    public int Error;
    public ulong Unique;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseAttr
{
    public ulong Ino;
    public ulong Size;
    public ulong Blocks;
    public ulong ATime;
    public ulong MTime;
    public ulong CTime;
    public uint ATimeNsec;
    public uint MTimeNsec;
    public uint CTimeNsec;
    public uint Mode;
    public uint NLink;
    public uint Uid;
    public uint Gid;
    public uint RDev;
    public uint BlkSize;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseEntryOut
{
    public ulong NodeId;
    public ulong Generation;
    public ulong EntryValid;
    public ulong AttrValid;
    public uint EntryValidNsec;
    public uint AttrValidNsec;
    public FuseAttr Attr;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseAttrOut
{
    public ulong AttrValid;
    public uint AttrValidNsec;
    public uint Dummy;
    public FuseAttr Attr;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseInitIn
{
    public uint Major;
    public uint Minor;
    public uint MaxReadahead;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct FuseInitOut
{
    public uint Major;
    public uint Minor;
    public uint MaxReadahead;
    public uint Flags;
    public ushort MaxBackground;
    public ushort CongestionThreshold;
    public uint MaxWrite;
    public uint TimeGran;
    public ushort MaxPages;
    public ushort MapAlignment;
    public fixed uint Unused[8];
}

[StructLayout(LayoutKind.Sequential)]
struct FuseForgetIn
{
    public ulong NLookup;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseBatchForgetIn
{
    public uint Count;
    public uint Dummy;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseForgetOne
{
    public ulong NodeId;
    public ulong NLookup;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseSetAttrIn
{
    public uint Valid;
    public uint Padding;
    public ulong Fh;
    public ulong Size;
    public ulong LockOwner;
    public ulong ATime;
    public ulong MTime;
    public ulong CTime;
    public uint ATimeNsec;
    public uint MTimeNsec;
    public uint CTimeNsec;
    public uint Mode;
    public uint Unused4;
    public uint Uid;
    public uint Gid;
    public uint Unused5;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseMkNodIn
{
    public uint Mode;
    public uint RDev;
    public uint Umask;
    public uint Padding;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseMkDirIn
{
    public uint Mode;
    public uint Umask;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseRenameIn
{
    public ulong NewDir;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseRename2In
{
    public ulong NewDir;
    public uint Flags;
    public uint Padding;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseOpenIn
{
    public int Flags;
    public uint OpenFlags;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseCreateIn
{
    public int Flags;
    public uint Mode;
    public uint Umask;
    public uint OpenFlags;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseOpenOut
{
    public ulong Fh;
    public uint OpenFlags;
    public uint Padding;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseReadIn
{
    public ulong Fh;
    public ulong Offset;
    public uint Size;
    public uint ReadFlags;
    public ulong LockOwner;
    public uint Flags;
    public uint Padding;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseWriteIn
{
    public ulong Fh;
    public ulong Offset;
    public uint Size;
    public uint WriteFlags;
    public ulong LockOwner;
    public uint Flags;
    public uint Padding;
}

[StructLayout(LayoutKind.Sequential)]
struct FuseWriteOut
{
    public uint Size;
    public uint Padding;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct FuseStatFsOut
{
    public ulong Blocks;
    public ulong BFree;
    public ulong BAvail;
    public ulong Files;
    public ulong FFree;
    public uint BSize;
    public uint NameLen;
    public uint FrSize;
    public uint Padding;
    public fixed uint Spare[6];
}

[StructLayout(LayoutKind.Sequential)]
struct FuseDirent
{
    public ulong Ino;
    public ulong Off;
    public uint NameLen;
    public uint Type;
}
