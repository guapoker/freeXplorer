using System.Runtime.InteropServices;
using System.Text;
using FreeXplorer.Fatx;

namespace FreeXplorer.Mount;

// Answers FUSE requests from a FatxVolume. Each request is one buffer in and at most one
// reply out, which keeps this free of any device or mount so the tests can drive it directly.
// The volume locks itself, so several threads may call Handle at once.
sealed class FatxFuse
{
    const int HeaderSize = 16;
    const int InHeaderSize = 40;

    // how long the kernel may trust what we told it, in seconds
    const ulong CacheSeconds = 1;

    readonly FatxVolume volume;
    readonly uint owner;
    readonly uint group;
    readonly object gate = new();
    readonly Dictionary<ulong, (FatxNode Node, ulong Lookups)> nodes = new();

    public FatxFuse(FatxVolume volume, uint owner, uint group)
    {
        this.volume = volume;
        this.owner = owner;
        this.group = group;
    }

    public bool ReadOnly => volume.IsReadOnly;

    // Fills reply and returns its length, or 0 when the request takes no answer
    public int Handle(ReadOnlySpan<byte> request, Span<byte> reply)
    {
        var header = MemoryMarshal.Read<FuseInHeader>(request);
        var body = request[InHeaderSize..(int)Math.Min(header.Length, (uint)request.Length)];
        int length;
        try
        {
            length = Dispatch(header, body, reply);
        }
        catch (FatxException ex)
        {
            length = -ToErrno(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            length = -Errno.EIO;
        }

        if (length == NoReply)
            return 0;
        int error = length < 0 ? length : 0;
        int total = HeaderSize + Math.Max(length, 0);
        var outHeader = new FuseOutHeader { Length = (uint)total, Error = error, Unique = header.Unique };
        MemoryMarshal.Write(reply, in outHeader);
        return total;
    }

    const int NoReply = int.MinValue;

    // returns the payload length, or -errno
    int Dispatch(FuseInHeader header, ReadOnlySpan<byte> body, Span<byte> reply)
    {
        var payload = reply[HeaderSize..];
        switch (header.Opcode)
        {
            case Fuse.Opcode.Init:
                return Init(MemoryMarshal.Read<FuseInitIn>(body), payload);

            case Fuse.Opcode.Lookup:
            {
                var child = volume.FindChild(Get(header.NodeId), Name(body));
                return child is null ? -Errno.ENOENT : Entry(child, payload);
            }

            case Fuse.Opcode.Forget:
                Forget(header.NodeId, MemoryMarshal.Read<FuseForgetIn>(body).NLookup);
                return NoReply;

            case Fuse.Opcode.BatchForget:
            {
                var batch = MemoryMarshal.Read<FuseBatchForgetIn>(body);
                var items = MemoryMarshal.Cast<byte, FuseForgetOne>(body[8..]);
                for (int i = 0; i < batch.Count && i < items.Length; i++)
                    Forget(items[i].NodeId, items[i].NLookup);
                return NoReply;
            }

            case Fuse.Opcode.GetAttr:
                return AttrReply(Get(header.NodeId), payload);

            case Fuse.Opcode.SetAttr:
                return SetAttr(Get(header.NodeId), MemoryMarshal.Read<FuseSetAttrIn>(body), payload);

            case Fuse.Opcode.MkNod:
            {
                var input = MemoryMarshal.Read<FuseMkNodIn>(body);
                if ((input.Mode & Fuse.ModeTypeMask) != Fuse.ModeFile)
                    return -Errno.EPERM;
                return Entry(volume.CreateFile(Get(header.NodeId), CheckedName(body[16..])), payload);
            }

            case Fuse.Opcode.MkDir:
                return Entry(volume.CreateDirectory(Get(header.NodeId), CheckedName(body[8..])), payload);

            case Fuse.Opcode.Create:
            {
                var node = volume.CreateFile(Get(header.NodeId), CheckedName(body[16..]));
                int n = Entry(node, payload);
                var open = new FuseOpenOut();
                MemoryMarshal.Write(payload[n..], in open);
                return n + Marshal.SizeOf<FuseOpenOut>();
            }

            case Fuse.Opcode.Unlink:
            {
                var child = volume.FindChild(Get(header.NodeId), Name(body));
                if (child is null)
                    return -Errno.ENOENT;
                if (child.IsDirectory)
                    return -Errno.EISDIR;
                volume.Delete(child);
                return 0;
            }

            case Fuse.Opcode.RmDir:
            {
                var child = volume.FindChild(Get(header.NodeId), Name(body));
                if (child is null)
                    return -Errno.ENOENT;
                if (!child.IsDirectory)
                    return -Errno.ENOTDIR;
                volume.Delete(child);
                return 0;
            }

            case Fuse.Opcode.Rename:
            {
                var input = MemoryMarshal.Read<FuseRenameIn>(body);
                return Rename(header.NodeId, input.NewDir, 0, body[8..]);
            }

            case Fuse.Opcode.Rename2:
            {
                var input = MemoryMarshal.Read<FuseRename2In>(body);
                return Rename(header.NodeId, input.NewDir, input.Flags, body[16..]);
            }

            case Fuse.Opcode.Open:
            {
                var node = Get(header.NodeId);
                if (node.IsDirectory)
                    return -Errno.EISDIR;
                var input = MemoryMarshal.Read<FuseOpenIn>(body);
                if (ReadOnly && (input.Flags & Fuse.OAccessMode) != Fuse.OReadOnly)
                    return -Errno.EROFS;
                return OpenReply(payload);
            }

            case Fuse.Opcode.OpenDir:
                return Get(header.NodeId).IsDirectory ? OpenReply(payload) : -Errno.ENOTDIR;

            case Fuse.Opcode.Read:
            {
                var node = Get(header.NodeId);
                if (node.IsDirectory)
                    return -Errno.EISDIR;
                var input = MemoryMarshal.Read<FuseReadIn>(body);
                int size = (int)Math.Min(input.Size, (uint)payload.Length);
                if (input.Offset >= (ulong)node.Size)
                    return 0;
                return volume.Read(node, (long)input.Offset, payload[..size]);
            }

            case Fuse.Opcode.Write:
            {
                var node = Get(header.NodeId);
                if (node.IsDirectory)
                    return -Errno.EISDIR;
                var input = MemoryMarshal.Read<FuseWriteIn>(body);
                var data = body.Slice(Marshal.SizeOf<FuseWriteIn>(), (int)input.Size);
                volume.Write(node, (long)input.Offset, data);
                var output = new FuseWriteOut { Size = input.Size };
                MemoryMarshal.Write(payload, in output);
                return Marshal.SizeOf<FuseWriteOut>();
            }

            case Fuse.Opcode.ReadDir:
                return ReadDir(Get(header.NodeId), MemoryMarshal.Read<FuseReadIn>(body), payload);

            case Fuse.Opcode.StatFs:
                return StatFs(payload);

            case Fuse.Opcode.Flush:
            case Fuse.Opcode.Release:
            {
                var node = Get(header.NodeId);
                if (!ReadOnly && !node.IsDeleted)
                    volume.FlushNode(node);
                return 0;
            }

            case Fuse.Opcode.FSync:
            {
                var node = Get(header.NodeId);
                if (!ReadOnly && !node.IsDeleted)
                {
                    volume.FlushNode(node);
                    volume.Flush();
                }
                return 0;
            }

            case Fuse.Opcode.FSyncDir:
                if (!ReadOnly)
                    volume.Flush();
                return 0;

            case Fuse.Opcode.ReleaseDir:
                return 0;

            case Fuse.Opcode.Interrupt:
            case Fuse.Opcode.Destroy:
                return NoReply;

            // FATX has no links, permissions to check or extended attributes. ENOSYS makes the
            // kernel stop asking, and default_permissions does the checks from the modes we report.
            default:
                return -Errno.ENOSYS;
        }
    }

    // ---- requests ----

    int Init(FuseInitIn input, Span<byte> payload)
    {
        var output = new FuseInitOut
        {
            Major = Fuse.KernelVersion,
            Minor = Math.Min(input.Minor, Fuse.KernelMinorVersion),
            MaxReadahead = input.MaxReadahead,
            Flags = input.Flags & (Fuse.AsyncRead | Fuse.BigWrites | Fuse.MaxPages),
            MaxBackground = 16,
            CongestionThreshold = 12,
            MaxWrite = Fuse.MaxWrite,
            TimeGran = 1_000_000_000,   // FATX keeps times to 2 seconds, this is the coarsest the kernel takes
            MaxPages = (ushort)(Fuse.MaxWrite / 4096),
        };
        // an older major version makes the kernel retry with ours, a newer one isn't something we can talk to
        if (input.Major < Fuse.KernelVersion)
            return -Errno.EIO;
        MemoryMarshal.Write(payload, in output);
        return Marshal.SizeOf<FuseInitOut>();
    }

    int SetAttr(FatxNode node, FuseSetAttrIn input, Span<byte> payload)
    {
        const uint changes = Fuse.SetMode | Fuse.SetSize | Fuse.SetATime | Fuse.SetMTime;
        if ((input.Valid & changes) != 0 && ReadOnly)
            return -Errno.EROFS;

        if ((input.Valid & Fuse.SetSize) != 0)
        {
            if (node.IsDirectory)
                return -Errno.EISDIR;
            volume.SetLength(node, (long)input.Size);
        }

        if (node.IsRoot)
            return AttrReply(node, payload);

        // only the owner's write bit means anything, it's the read-only attribute
        if ((input.Valid & Fuse.SetMode) != 0)
        {
            bool readOnly = (input.Mode & 0x80) == 0;
            var attributes = readOnly
                ? node.Attributes | FatxAttributes.ReadOnly
                : node.Attributes & ~FatxAttributes.ReadOnly;
            if (attributes != node.Attributes)
                volume.SetAttributes(node, attributes);
        }

        DateTime? modified = TimeFrom(input.Valid, Fuse.SetMTime, Fuse.SetMTimeNow, input.MTime);
        DateTime? accessed = TimeFrom(input.Valid, Fuse.SetATime, Fuse.SetATimeNow, input.ATime);
        if (modified is not null || accessed is not null)
            volume.SetTimes(node, null, modified, accessed);

        // uid and gid can't be stored, so a chown is quietly accepted like on a FAT stick
        return AttrReply(node, payload);
    }

    int Rename(ulong oldDirId, ulong newDirId, uint flags, ReadOnlySpan<byte> names)
    {
        if ((flags & Fuse.RenameExchange) != 0)
            return -Errno.EINVAL;

        int split = names.IndexOf((byte)0);
        if (split < 0)
            return -Errno.EINVAL;
        string oldName = Encoding.UTF8.GetString(names[..split]);
        string newName = CheckedName(names[(split + 1)..]);

        var oldDir = Get(oldDirId);
        var newDir = Get(newDirId);
        var node = volume.FindChild(oldDir, oldName);
        if (node is null)
            return -Errno.ENOENT;

        var existing = volume.FindChild(newDir, newName);
        if (existing is not null && existing != node)
        {
            if ((flags & Fuse.RenameNoReplace) != 0)
                return -Errno.EEXIST;
            if (existing.IsDirectory && !node.IsDirectory)
                return -Errno.EISDIR;
            if (!existing.IsDirectory && node.IsDirectory)
                return -Errno.ENOTDIR;
            if (existing.IsDirectory)
            {
                // an empty directory may be replaced by another one
                if (volume.GetChildren(existing).Count > 0)
                    return -Errno.ENOTEMPTY;
                volume.Delete(existing);
            }
        }

        volume.Rename(node, newDir, newName, replaceExisting: true);
        return 0;
    }

    int ReadDir(FatxNode dir, FuseReadIn input, Span<byte> payload)
    {
        if (!dir.IsDirectory)
            return -Errno.ENOTDIR;

        var entries = new List<(string Name, FatxNode Node)> { (".", dir), ("..", dir.Parent ?? dir) };
        foreach (var child in volume.GetChildren(dir))
            entries.Add((child.Name, child));

        int size = (int)Math.Min(input.Size, (uint)payload.Length);
        int used = 0;
        int direntSize = Marshal.SizeOf<FuseDirent>();
        for (int i = (int)Math.Min(input.Offset, (ulong)entries.Count); i < entries.Count; i++)
        {
            var (name, node) = entries[i];
            int nameLength = Encoding.UTF8.GetByteCount(name);
            int record = (direntSize + nameLength + 7) & ~7;
            if (used + record > size)
                break;

            var dirent = new FuseDirent
            {
                Ino = IdOf(node),
                Off = (ulong)(i + 1),
                NameLen = (uint)nameLength,
                Type = node.IsDirectory ? Fuse.TypeDirectory : Fuse.TypeFile,
            };
            var slot = payload.Slice(used, record);
            slot.Clear();
            MemoryMarshal.Write(slot, in dirent);
            Encoding.UTF8.GetBytes(name, slot[direntSize..]);
            used += record;
        }
        return used;
    }

    int StatFs(Span<byte> payload)
    {
        ulong cluster = (ulong)volume.ClusterSize;
        ulong free = volume.FreeSpaceKnown ? (ulong)volume.FreeBytes / cluster : 0;
        var output = new FuseStatFsOut
        {
            Blocks = (ulong)volume.TotalBytes / cluster,
            BFree = free,
            BAvail = free,
            BSize = (uint)cluster,
            FrSize = (uint)cluster,
            NameLen = FatxName.MaxLength,
        };
        MemoryMarshal.Write(payload, in output);
        return Marshal.SizeOf<FuseStatFsOut>();
    }

    // ---- replies ----

    int Entry(FatxNode node, Span<byte> payload)
    {
        ulong id = Remember(node);
        var entry = new FuseEntryOut
        {
            NodeId = id,
            EntryValid = CacheSeconds,
            AttrValid = CacheSeconds,
            Attr = AttrOf(node),
        };
        MemoryMarshal.Write(payload, in entry);
        return Marshal.SizeOf<FuseEntryOut>();
    }

    int AttrReply(FatxNode node, Span<byte> payload)
    {
        var output = new FuseAttrOut { AttrValid = CacheSeconds, Attr = AttrOf(node) };
        MemoryMarshal.Write(payload, in output);
        return Marshal.SizeOf<FuseAttrOut>();
    }

    static int OpenReply(Span<byte> payload)
    {
        var output = new FuseOpenOut();
        MemoryMarshal.Write(payload, in output);
        return Marshal.SizeOf<FuseOpenOut>();
    }

    internal FuseAttr AttrOf(FatxNode node)
    {
        ulong cluster = (ulong)volume.ClusterSize;
        ulong size = node.IsDirectory ? cluster : (ulong)node.Size;
        ulong allocated = (size + cluster - 1) / cluster * cluster;

        uint permissions = node.IsDirectory ? 0x1EDu : 0x1A4u;   // 0755 and 0644
        if (ReadOnly || (node.Attributes & FatxAttributes.ReadOnly) != 0)
            permissions &= ~0x92u;   // no write bits

        ulong modified = UnixTime(node.Modified);
        return new FuseAttr
        {
            Ino = IdOf(node),
            Size = size,
            Blocks = allocated / 512,
            ATime = UnixTime(node.Accessed),
            MTime = modified,
            CTime = modified,
            Mode = (node.IsDirectory ? Fuse.ModeDirectory : Fuse.ModeFile) | permissions,
            NLink = node.IsDirectory ? 2u : 1u,
            Uid = owner,
            Gid = group,
            BlkSize = (uint)cluster,
        };
    }

    // ---- node ids ----

    // Node ids are handed to the kernel on lookup and given back with forget. FatxNode.Id
    // never repeats, and the root is always 1.
    static ulong IdOf(FatxNode node) => node.IsRoot ? Fuse.RootId : (ulong)node.Id + 1;

    ulong Remember(FatxNode node)
    {
        ulong id = IdOf(node);
        if (id == Fuse.RootId)
            return id;
        lock (gate)
        {
            nodes[id] = nodes.TryGetValue(id, out var known) ? (node, known.Lookups + 1) : (node, 1);
        }
        return id;
    }

    void Forget(ulong id, ulong count)
    {
        lock (gate)
        {
            if (!nodes.TryGetValue(id, out var known))
                return;
            if (known.Lookups <= count)
                nodes.Remove(id);
            else
                nodes[id] = (known.Node, known.Lookups - count);
        }
    }

    internal int KnownNodes { get { lock (gate) return nodes.Count; } }

    FatxNode Get(ulong id)
    {
        if (id == Fuse.RootId)
            return volume.Root;
        lock (gate)
        {
            if (nodes.TryGetValue(id, out var known))
                return known.Node;
        }
        throw new FatxException(FatxError.NotFound, "the kernel asked about a node it was never given");
    }

    // ---- helpers ----

    static string Name(ReadOnlySpan<byte> data)
    {
        int end = data.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? data : data[..end]);
    }

    static string CheckedName(ReadOnlySpan<byte> data)
    {
        string name = Name(data);
        if (name.Length > FatxName.MaxLength)
            throw new FatxException(FatxError.PathTooLong, $"{name} is longer than {FatxName.MaxLength} characters");
        if (!FatxName.IsValid(name))
            throw new FatxException(FatxError.InvalidName, $"{name} isn't a name FATX can store");
        return name;
    }

    static DateTime? TimeFrom(uint valid, uint setBit, uint nowBit, ulong seconds)
    {
        if ((valid & nowBit) != 0)
            return FatxTime.FromUtc(DateTime.UtcNow);
        if ((valid & setBit) == 0)
            return null;
        return FatxTime.FromUtc(DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime);
    }

    static ulong UnixTime(DateTime fatxLocal)
    {
        long seconds = new DateTimeOffset(FatxTime.ToUtc(fatxLocal)).ToUnixTimeSeconds();
        return (ulong)Math.Max(seconds, 0);
    }

    internal static int ToErrno(FatxError error) => error switch
    {
        FatxError.NotFound => Errno.ENOENT,
        FatxError.AlreadyExists => Errno.EEXIST,
        FatxError.NotADirectory => Errno.ENOTDIR,
        FatxError.IsADirectory => Errno.EISDIR,
        FatxError.DirectoryNotEmpty => Errno.ENOTEMPTY,
        FatxError.InvalidName => Errno.EINVAL,
        FatxError.PathTooLong => Errno.ENAMETOOLONG,
        FatxError.DirectoryFull => Errno.ENOSPC,
        FatxError.DiskFull => Errno.ENOSPC,
        FatxError.FileTooLarge => Errno.EFBIG,
        FatxError.ReadOnly => Errno.EROFS,
        FatxError.InvalidOperation => Errno.EPERM,
        _ => Errno.EIO,
    };
}
