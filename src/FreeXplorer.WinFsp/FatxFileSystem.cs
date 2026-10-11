using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;
using Fsp.Interop;
using FreeXplorer.Fatx;
using FileInfo = Fsp.Interop.FileInfo;

namespace FreeXplorer.Mount;

// Presents a FatxVolume to WinFsp, which makes it show up as a drive in Explorer.
// Every callback maps straight onto a FatxVolume call. Anything FATX can't express
// (streams, reparse points, ACLs) is left out and WinFsp is told so in Init.
public sealed unsafe class FatxFileSystem : FileSystemBase
{
    const uint AttrReadOnly = 0x01;
    const uint AttrHidden = 0x02;
    const uint AttrSystem = 0x04;
    const uint AttrDirectory = 0x10;
    const uint AttrArchive = 0x20;
    const uint AttrNormal = 0x80;
    const uint AttrInvalid = 0xFFFFFFFF;

    // write data, append data and delete: what a read-only volume must not be opened for
    const uint WriteAccess = 0x0002 | 0x0004 | 0x00010000;

    const string RecycleBinName = "$RECYCLE.BIN";

    // everyone gets full control, FATX has no owners
    const string OpenSecurity = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)";

    readonly FatxVolume volume;
    readonly string label;
    readonly byte[] security;

    public FatxFileSystem(FatxVolume volume, string label)
    {
        this.volume = volume;
        this.label = label.Length > 32 ? label[..32] : label;
        var sd = new RawSecurityDescriptor(OpenSecurity);
        security = new byte[sd.BinaryLength];
        sd.GetBinaryForm(security, 0);
    }

    public bool ReadOnly => volume.IsReadOnly;

    // ---- setup ----

    public override int Init(object host)
    {
        var h = (FileSystemHost)host;
        h.SectorSize = 512;
        h.SectorsPerAllocationUnit = (ushort)(volume.ClusterSize / 512);
        h.MaxComponentLength = FatxName.MaxLength;
        h.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        h.VolumeSerialNumber = volume.VolumeId;
        h.FileInfoTimeout = 1000;
        h.CaseSensitiveSearch = false;
        h.CasePreservedNames = true;
        h.UnicodeOnDisk = false;
        h.PersistentAcls = false;
        h.ReparsePoints = false;
        h.NamedStreams = false;
        h.PostCleanupWhenModifiedOnly = true;
        h.PassQueryDirectoryPattern = false;
        h.FlushAndPurgeOnCleanup = false;
        h.FileSystemName = "FATX";
        return STATUS_SUCCESS;
    }

    public override int ExceptionHandler(Exception ex) => ToStatus(ex);

    public static int ToStatus(Exception ex) => ex switch
    {
        FatxException f => f.Error switch
        {
            FatxError.NotFound => STATUS_OBJECT_NAME_NOT_FOUND,
            FatxError.AlreadyExists => STATUS_OBJECT_NAME_COLLISION,
            FatxError.NotADirectory => STATUS_NOT_A_DIRECTORY,
            FatxError.IsADirectory => STATUS_FILE_IS_A_DIRECTORY,
            FatxError.DirectoryNotEmpty => STATUS_DIRECTORY_NOT_EMPTY,
            FatxError.InvalidName => STATUS_OBJECT_NAME_INVALID,
            FatxError.PathTooLong => STATUS_NAME_TOO_LONG,
            FatxError.DirectoryFull => STATUS_CANNOT_MAKE,
            FatxError.DiskFull => STATUS_DISK_FULL,
            FatxError.FileTooLarge => STATUS_FILE_TOO_LARGE,
            FatxError.ReadOnly => STATUS_MEDIA_WRITE_PROTECTED,
            FatxError.Corrupt => STATUS_FILE_CORRUPT_ERROR,
            _ => STATUS_INVALID_PARAMETER,
        },
        UnauthorizedAccessException => STATUS_ACCESS_DENIED,
        IOException => STATUS_IO_DEVICE_ERROR,
        _ => STATUS_UNEXPECTED_IO_ERROR,
    };

    // ---- volume ----

    public override int GetVolumeInfo(out VolumeInfo info)
    {
        info = default;
        info.TotalSize = (ulong)volume.TotalBytes;
        info.FreeSize = (ulong)volume.FreeBytes;
        info.SetVolumeLabel(label);
        return STATUS_SUCCESS;
    }

    public override int SetVolumeLabel(string newLabel, out VolumeInfo info)
    {
        info = default;
        if (ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;
        volume.Label = newLabel;
        return GetVolumeInfo(out info);
    }

    // ---- opening and creating ----

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        var node = volume.Find(fileName);
        if (node is null)
        {
            fileAttributes = 0;
            return MissingStatus(fileName);
        }
        fileAttributes = AttributesOf(node);
        if (securityDescriptor is not null)
            securityDescriptor = security;
        return STATUS_SUCCESS;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = default!;
        fileDesc = default!;
        fileInfo = default;
        normalizedName = default!;

        var node = volume.Find(fileName);
        if (node is null)
            return MissingStatus(fileName);

        if ((createOptions & FILE_DIRECTORY_FILE) != 0 && !node.IsDirectory)
            return STATUS_NOT_A_DIRECTORY;
        if ((createOptions & FILE_NON_DIRECTORY_FILE) != 0 && node.IsDirectory)
            return STATUS_FILE_IS_A_DIRECTORY;

        // Same as a CD: say no when the file is opened, not when it's written. Otherwise Windows
        // takes the write into its cache, tells the program it worked, and only fails when it
        // flushes, long after anyone can do anything about it.
        if (ReadOnly && (grantedAccess & WriteAccess) != 0)
            return STATUS_MEDIA_WRITE_PROTECTED;

        fileNode = node;
        fileInfo = InfoOf(node);
        normalizedName = node.Path;
        return STATUS_SUCCESS;
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[] securityDescriptor, ulong allocationSize,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = default!;
        fileDesc = default!;
        fileInfo = default;
        normalizedName = default!;

        if (ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        var (parentPath, name) = Split(fileName);
        var parent = volume.Find(parentPath);
        if (parent is null)
            return STATUS_OBJECT_PATH_NOT_FOUND;
        if (!parent.IsDirectory)
            return STATUS_NOT_A_DIRECTORY;
        if (!FatxName.IsValid(name))
            return name.Length > FatxName.MaxLength ? STATUS_NAME_TOO_LONG : STATUS_OBJECT_NAME_INVALID;
        if (volume.FindChild(parent, name) is not null)
            return STATUS_OBJECT_NAME_COLLISION;

        // Explorer wants to park deleted files in a Recycle Bin folder on the drive itself, where they'd keep
        // using up space on a console partition. Saying no makes it delete for real, like it does on a USB stick.
        if (parent == volume.Root && name.Equals(RecycleBinName, StringComparison.OrdinalIgnoreCase))
            return STATUS_ACCESS_DENIED;

        var node = (createOptions & FILE_DIRECTORY_FILE) != 0
            ? volume.CreateDirectory(parent, name)
            : volume.CreateFile(parent, name);

        var wanted = FromWindows(fileAttributes);
        if (wanted != FatxAttributes.None)
            volume.SetAttributes(node, node.Attributes | wanted);

        fileNode = node;
        fileInfo = InfoOf(node);
        normalizedName = node.Path;
        return STATUS_SUCCESS;
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes,
        ulong allocationSize, out FileInfo fileInfo)
    {
        var node = (FatxNode)fileNode;
        fileInfo = default;
        if (ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        volume.SetLength(node, 0);
        var wanted = FromWindows(fileAttributes);
        var keep = replaceFileAttributes ? FatxAttributes.None : node.Attributes;
        volume.SetAttributes(node, keep | wanted);
        fileInfo = InfoOf(node);
        return STATUS_SUCCESS;
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        var node = (FatxNode)fileNode;
        try
        {
            if ((flags & CleanupDelete) != 0)
            {
                if (!ReadOnly && !node.IsDeleted && !node.IsRoot)
                    volume.Delete(node);
                return;
            }
            volume.FlushNode(node);
        }
        catch (IOException)
        {
            // nothing to report this to, cleanup has no return value. The next access sees the real state.
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
    }

    // ---- data ----

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        var node = (FatxNode)fileNode;
        bytesTransferred = 0;
        if (offset >= (ulong)node.Size)
            return STATUS_END_OF_FILE;

        int n = volume.Read(node, (long)offset, new Span<byte>((void*)buffer, (int)length));
        bytesTransferred = (uint)n;
        return STATUS_SUCCESS;
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo)
    {
        var node = (FatxNode)fileNode;
        bytesTransferred = 0;
        fileInfo = InfoOf(node);
        if (ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        ulong size = (ulong)node.Size;
        if (constrainedIo)
        {
            // writes caused by the cache manager never grow the file
            if (offset >= size)
                return STATUS_SUCCESS;
            length = (uint)Math.Min(length, size - offset);
        }
        else if (writeToEndOfFile)
        {
            offset = size;
        }

        if (length > 0)
            volume.Write(node, (long)offset, new ReadOnlySpan<byte>((void*)buffer, (int)length));
        bytesTransferred = length;
        fileInfo = InfoOf(node);
        return STATUS_SUCCESS;
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (fileNode is FatxNode node)
        {
            volume.FlushNode(node);
            fileInfo = InfoOf(node);
        }
        else
        {
            volume.Flush();
        }
        return STATUS_SUCCESS;
    }

    // ---- attributes ----

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = InfoOf((FatxNode)fileNode);
        return STATUS_SUCCESS;
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes,
        ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
    {
        var node = (FatxNode)fileNode;
        fileInfo = InfoOf(node);

        bool wantsAttributes = fileAttributes != AttrInvalid;
        bool wantsTimes = creationTime != 0 || lastAccessTime != 0 || lastWriteTime != 0;
        if (!wantsAttributes && !wantsTimes)
            return STATUS_SUCCESS;
        if (ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;
        if (node.IsRoot)
            return STATUS_SUCCESS;

        if (wantsAttributes)
            volume.SetAttributes(node, FromWindows(fileAttributes));
        if (wantsTimes)
            volume.SetTimes(node, ToLocal(creationTime), ToLocal(lastWriteTime), ToLocal(lastAccessTime));

        fileInfo = InfoOf(node);
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        var node = (FatxNode)fileNode;
        fileInfo = InfoOf(node);
        if (ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        if (setAllocationSize)
        {
            // clusters are handed out as the file grows, so a bigger allocation is just a promise.
            // A smaller one cuts the file.
            if (newSize < (ulong)node.Size)
                volume.SetLength(node, (long)newSize);
        }
        else
        {
            volume.SetLength(node, (long)newSize);
        }
        fileInfo = InfoOf(node);
        return STATUS_SUCCESS;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        var node = (FatxNode)fileNode;
        if (ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;
        if (node.IsRoot)
            return STATUS_ACCESS_DENIED;
        if (node.IsDirectory && volume.GetChildren(node).Count > 0)
            return STATUS_DIRECTORY_NOT_EMPTY;
        return STATUS_SUCCESS;
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        var node = (FatxNode)fileNode;
        if (ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        var (parentPath, name) = Split(newFileName);
        var parent = volume.Find(parentPath);
        if (parent is null)
            return STATUS_OBJECT_PATH_NOT_FOUND;

        if (!FatxName.IsValid(name))
            return name.Length > FatxName.MaxLength ? STATUS_NAME_TOO_LONG : STATUS_OBJECT_NAME_INVALID;

        var existing = volume.FindChild(parent, name);
        if (existing is not null && existing != node)
        {
            if (!replaceIfExists)
                return STATUS_OBJECT_NAME_COLLISION;
            if (existing.IsDirectory || node.IsDirectory)
                return STATUS_ACCESS_DENIED;
        }

        volume.Rename(node, parent, name, replaceIfExists);
        return STATUS_SUCCESS;
    }

    // ---- security: accepted and ignored, there's nowhere to keep it ----

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = security;
        return STATUS_SUCCESS;
    }

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor)
        => STATUS_SUCCESS;

    // ---- directory listing ----

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FileInfo fileInfo)
    {
        var dir = (FatxNode)fileNode;
        if (context is not IEnumerator<(string Name, FatxNode Node)> entries)
        {
            var list = new List<(string, FatxNode)>();
            if (!dir.IsRoot)
            {
                if (marker is null)
                    list.Add((".", dir));
                if (marker is null || marker == ".")
                    list.Add(("..", dir.Parent ?? dir));
            }
            var children = volume.GetChildren(dir).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
            {
                if (marker is not null && marker is not "." and not ".."
                    && string.Compare(child.Name, marker, StringComparison.OrdinalIgnoreCase) <= 0)
                    continue;
                list.Add((child.Name, child));
            }
            context = entries = list.GetEnumerator();
        }

        if (entries.MoveNext())
        {
            fileName = entries.Current.Name;
            fileInfo = InfoOf(entries.Current.Node);
            return true;
        }
        fileName = default!;
        fileInfo = default;
        return false;
    }

    public override int GetDirInfoByName(object fileNode, object fileDesc, string fileName,
        out string normalizedName, out FileInfo fileInfo)
    {
        normalizedName = default!;
        fileInfo = default;
        var child = volume.FindChild((FatxNode)fileNode, fileName);
        if (child is null)
            return STATUS_OBJECT_NAME_NOT_FOUND;
        normalizedName = child.Name;
        fileInfo = InfoOf(child);
        return STATUS_SUCCESS;
    }

    // ---- translation helpers ----

    int MissingStatus(string fileName)
    {
        var (parentPath, _) = Split(fileName);
        return volume.Find(parentPath) is { IsDirectory: true }
            ? STATUS_OBJECT_NAME_NOT_FOUND
            : STATUS_OBJECT_PATH_NOT_FOUND;
    }

    static (string Parent, string Name) Split(string path)
    {
        int cut = path.LastIndexOf('\\');
        if (cut < 0)
            return ("\\", path);
        return (cut == 0 ? "\\" : path[..cut], path[(cut + 1)..]);
    }

    uint AttributesOf(FatxNode node)
    {
        uint result = 0;
        if (node.IsDirectory) result |= AttrDirectory;
        if ((node.Attributes & FatxAttributes.ReadOnly) != 0) result |= AttrReadOnly;
        if ((node.Attributes & FatxAttributes.Hidden) != 0) result |= AttrHidden;
        if ((node.Attributes & FatxAttributes.System) != 0) result |= AttrSystem;
        if ((node.Attributes & FatxAttributes.Archive) != 0 && !node.IsDirectory) result |= AttrArchive;
        return result == 0 ? AttrNormal : result;
    }

    static FatxAttributes FromWindows(uint attributes)
    {
        var result = FatxAttributes.None;
        if ((attributes & AttrReadOnly) != 0) result |= FatxAttributes.ReadOnly;
        if ((attributes & AttrHidden) != 0) result |= FatxAttributes.Hidden;
        if ((attributes & AttrSystem) != 0) result |= FatxAttributes.System;
        if ((attributes & AttrArchive) != 0) result |= FatxAttributes.Archive;
        return result;
    }

    FileInfo InfoOf(FatxNode node)
    {
        int cluster = volume.ClusterSize;
        ulong size = node.IsDirectory ? 0UL : (ulong)node.Size;
        ulong write = ToFileTime(node.Modified);
        return new FileInfo
        {
            FileAttributes = AttributesOf(node),
            FileSize = size,
            AllocationSize = (size + (ulong)cluster - 1) / (ulong)cluster * (ulong)cluster,
            CreationTime = ToFileTime(node.Created),
            LastAccessTime = ToFileTime(node.Accessed),
            LastWriteTime = write,
            ChangeTime = write,
            IndexNumber = (ulong)node.Id,
            HardLinks = 0,
        };
    }

    static ulong ToFileTime(DateTime fatxLocal) => (ulong)FatxTime.ToUtc(fatxLocal).ToFileTimeUtc();

    static DateTime? ToLocal(ulong fileTime)
    {
        if (fileTime == 0 || fileTime == ulong.MaxValue)
            return null;
        return FatxTime.FromUtc(DateTime.FromFileTimeUtc((long)fileTime));
    }
}
