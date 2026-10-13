using System.Runtime.InteropServices;
using System.Text;
using FreeXplorer.Fatx;
using FreeXplorer.Mount;

namespace FreeXplorer.Tests;

// Drives FatxFuse with requests laid out the way the kernel sends them, no mount involved
public class FuseTests
{
    const uint Uid = 1000;
    const uint Gid = 1000;

    sealed class Driver
    {
        readonly FatxFuse fuse;
        ulong unique;

        public Driver(FatxVolume volume) => fuse = new FatxFuse(volume, Uid, Gid);

        public FatxFuse Fuse => fuse;

        // returns the error (0 or -errno) and the payload
        public (int Error, byte[] Payload) Send(Mount.Fuse.Opcode opcode, ulong node, params object[] parts)
        {
            var body = new List<byte>();
            foreach (var part in parts)
            {
                switch (part)
                {
                    case string name:
                        body.AddRange(Encoding.UTF8.GetBytes(name));
                        body.Add(0);
                        break;
                    case byte[] bytes:
                        body.AddRange(bytes);
                        break;
                    default:
                        body.AddRange(StructBytes(part));
                        break;
                }
            }

            var header = new FuseInHeader
            {
                Length = (uint)(40 + body.Count),
                Opcode = opcode,
                Unique = ++unique,
                NodeId = node,
                Uid = Uid,
                Gid = Gid,
            };
            var request = StructBytes(header).Concat(body).ToArray();
            var reply = new byte[Mount.Fuse.BufferSize];
            int length = fuse.Handle(request, reply);
            if (length == 0)
                return (0, Array.Empty<byte>());

            var outHeader = MemoryMarshal.Read<FuseOutHeader>(reply);
            Assert.Equal(header.Unique, outHeader.Unique);
            Assert.Equal((uint)length, outHeader.Length);
            return (outHeader.Error, reply[16..length]);
        }

        public FuseEntryOut Lookup(ulong parent, string name)
        {
            var (error, payload) = Send(Mount.Fuse.Opcode.Lookup, parent, name);
            Assert.Equal(0, error);
            return MemoryMarshal.Read<FuseEntryOut>(payload);
        }

        public FuseEntryOut Create(ulong parent, string name)
        {
            var (error, payload) = Send(Mount.Fuse.Opcode.Create, parent, new FuseCreateIn { Flags = 1, Mode = 0x81A4 }, name);
            Assert.Equal(0, error);
            return MemoryMarshal.Read<FuseEntryOut>(payload);
        }

        public FuseEntryOut MkDir(ulong parent, string name)
        {
            var (error, payload) = Send(Mount.Fuse.Opcode.MkDir, parent, new FuseMkDirIn { Mode = 0x1ED }, name);
            Assert.Equal(0, error);
            return MemoryMarshal.Read<FuseEntryOut>(payload);
        }

        public int Write(ulong node, long offset, byte[] data) =>
            Send(Mount.Fuse.Opcode.Write, node, new FuseWriteIn { Offset = (ulong)offset, Size = (uint)data.Length }, data).Error;

        public byte[] Read(ulong node, long offset, int size)
        {
            var (error, payload) = Send(Mount.Fuse.Opcode.Read, node, new FuseReadIn { Offset = (ulong)offset, Size = (uint)size });
            Assert.Equal(0, error);
            return payload;
        }

        public List<(string Name, ulong Off, uint Type)> ReadDir(ulong node, ulong offset = 0, uint size = 4096)
        {
            var (error, payload) = Send(Mount.Fuse.Opcode.ReadDir, node, new FuseReadIn { Offset = offset, Size = size });
            Assert.Equal(0, error);
            var result = new List<(string, ulong, uint)>();
            int at = 0;
            while (at < payload.Length)
            {
                var dirent = MemoryMarshal.Read<FuseDirent>(payload.AsSpan(at));
                string name = Encoding.UTF8.GetString(payload, at + 24, (int)dirent.NameLen);
                result.Add((name, dirent.Off, dirent.Type));
                at += (24 + (int)dirent.NameLen + 7) & ~7;
            }
            return result;
        }
    }

    static byte[] StructBytes(object value)
    {
        var bytes = new byte[Marshal.SizeOf(value)];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false);
        }
        finally
        {
            handle.Free();
        }
        return bytes;
    }

    const ulong Root = 1;

    [Fact]
    public void Structures_match_the_kernel_layout()
    {
        Assert.Equal(40, Marshal.SizeOf<FuseInHeader>());
        Assert.Equal(16, Marshal.SizeOf<FuseOutHeader>());
        Assert.Equal(88, Marshal.SizeOf<FuseAttr>());
        Assert.Equal(128, Marshal.SizeOf<FuseEntryOut>());
        Assert.Equal(104, Marshal.SizeOf<FuseAttrOut>());
        Assert.Equal(64, Marshal.SizeOf<FuseInitOut>());
        Assert.Equal(88, Marshal.SizeOf<FuseSetAttrIn>());
        Assert.Equal(40, Marshal.SizeOf<FuseReadIn>());
        Assert.Equal(40, Marshal.SizeOf<FuseWriteIn>());
        Assert.Equal(80, Marshal.SizeOf<FuseStatFsOut>());
        Assert.Equal(24, Marshal.SizeOf<FuseDirent>());
    }

    [Fact]
    public void Init_settles_on_a_version_both_sides_speak()
    {
        var (_, volume) = TestVolumes.Create();
        var driver = new Driver(volume);

        var (error, payload) = driver.Send(Mount.Fuse.Opcode.Init, 0,
            new FuseInitIn { Major = 7, Minor = 45, MaxReadahead = 131072, Flags = 0xFFFFFFFF });
        Assert.Equal(0, error);
        var init = MemoryMarshal.Read<FuseInitOut>(payload);
        Assert.Equal(7u, init.Major);
        Assert.Equal(31u, init.Minor);
        Assert.Equal(Mount.Fuse.MaxWrite, init.MaxWrite);
        Assert.Equal(131072u, init.MaxReadahead);
    }

    [Fact]
    public void Files_written_through_fuse_land_on_the_volume()
    {
        var (_, volume) = TestVolumes.Create();
        var driver = new Driver(volume);

        var dir = driver.MkDir(Root, "Content");
        var file = driver.Create(dir.NodeId, "game.bin");
        Assert.Equal(Mount.Fuse.ModeFile, file.Attr.Mode & Mount.Fuse.ModeTypeMask);
        Assert.Equal(Uid, file.Attr.Uid);

        var data = TestVolumes.RandomBytes(300_000, 7);
        for (int at = 0; at < data.Length; at += 65536)
            Assert.Equal(0, driver.Write(file.NodeId, at, data[at..Math.Min(at + 65536, data.Length)]));

        Assert.Equal(data[1000..5000], driver.Read(file.NodeId, 1000, 4000));
        Assert.Empty(driver.Read(file.NodeId, data.Length, 4096));
        Assert.Equal(data, TestVolumes.ReadAll(volume, volume.Resolve(@"\Content\game.bin")));
        Assert.Equal((ulong)data.Length, driver.Lookup(dir.NodeId, "game.bin").Attr.Size);
        TestVolumes.AssertClean(volume);
    }

    [Fact]
    public void Directory_listings_page_through_every_entry()
    {
        var (_, volume) = TestVolumes.Create();
        var driver = new Driver(volume);
        for (int i = 0; i < 40; i++)
            driver.Create(Root, $"file{i:D2}.txt");
        driver.MkDir(Root, "folder");

        var all = driver.ReadDir(Root);
        Assert.Equal(new[] { ".", ".." }, all.Take(2).Select(e => e.Name));
        Assert.Equal(43, all.Count);
        Assert.Equal(Mount.Fuse.TypeDirectory, all.Single(e => e.Name == "folder").Type);
        Assert.Equal(Mount.Fuse.TypeFile, all.Single(e => e.Name == "file07.txt").Type);

        // a small buffer makes the kernel come back with the offset of the last entry it got
        var seen = new List<string>();
        ulong offset = 0;
        while (true)
        {
            var page = driver.ReadDir(Root, offset, 200);
            if (page.Count == 0)
                break;
            seen.AddRange(page.Select(e => e.Name));
            offset = page[^1].Off;
        }
        Assert.Equal(all.Select(e => e.Name), seen);
    }

    [Fact]
    public void Removing_follows_unix_rules()
    {
        var (_, volume) = TestVolumes.Create();
        var driver = new Driver(volume);
        var dir = driver.MkDir(Root, "dir");
        driver.Create(dir.NodeId, "inside");

        Assert.Equal(-Errno.EISDIR, driver.Send(Mount.Fuse.Opcode.Unlink, Root, "dir").Error);
        Assert.Equal(-Errno.ENOTEMPTY, driver.Send(Mount.Fuse.Opcode.RmDir, Root, "dir").Error);
        Assert.Equal(-Errno.ENOTDIR, driver.Send(Mount.Fuse.Opcode.RmDir, dir.NodeId, "inside").Error);
        Assert.Equal(-Errno.ENOENT, driver.Send(Mount.Fuse.Opcode.Unlink, Root, "nothing").Error);

        Assert.Equal(0, driver.Send(Mount.Fuse.Opcode.Unlink, dir.NodeId, "inside").Error);
        Assert.Equal(0, driver.Send(Mount.Fuse.Opcode.RmDir, Root, "dir").Error);
        Assert.Null(volume.Find("dir"));
        TestVolumes.AssertClean(volume);
    }

    [Fact]
    public void Rename_moves_replaces_and_respects_noreplace()
    {
        var (_, volume) = TestVolumes.Create();
        var driver = new Driver(volume);
        var dir = driver.MkDir(Root, "dir");
        var a = driver.Create(Root, "a");
        driver.Write(a.NodeId, 0, new byte[] { 1, 2, 3 });
        driver.Create(dir.NodeId, "b");

        var noReplace = new FuseRename2In { NewDir = dir.NodeId, Flags = Mount.Fuse.RenameNoReplace };
        Assert.Equal(-Errno.EEXIST, driver.Send(Mount.Fuse.Opcode.Rename2, Root, noReplace, "a", "b").Error);

        Assert.Equal(0, driver.Send(Mount.Fuse.Opcode.Rename, Root, new FuseRenameIn { NewDir = dir.NodeId }, "a", "b").Error);
        Assert.Null(volume.Find("a"));
        Assert.Equal(new byte[] { 1, 2, 3 }, TestVolumes.ReadAll(volume, volume.Resolve(@"\dir\b")));

        // changing only the case of a name is a rename too
        Assert.Equal(0, driver.Send(Mount.Fuse.Opcode.Rename, dir.NodeId, new FuseRenameIn { NewDir = dir.NodeId }, "b", "B").Error);
        Assert.Equal("B", volume.Resolve(@"\dir\b").Name);
        TestVolumes.AssertClean(volume);
    }

    [Fact]
    public void Names_fatx_cant_store_are_refused()
    {
        var (_, volume) = TestVolumes.Create();
        var driver = new Driver(volume);

        var tooLong = new string('x', FatxName.MaxLength + 1);
        Assert.Equal(-Errno.ENAMETOOLONG, driver.Send(Mount.Fuse.Opcode.MkDir, Root, new FuseMkDirIn(), tooLong).Error);
        Assert.Equal(-Errno.EINVAL, driver.Send(Mount.Fuse.Opcode.Create, Root, new FuseCreateIn(), "a*b").Error);

        // FATX names ignore case, so X and x are the same entry
        driver.MkDir(Root, "x");
        Assert.Equal(-Errno.EEXIST, driver.Send(Mount.Fuse.Opcode.MkDir, Root, new FuseMkDirIn(), "X").Error);
    }

    [Fact]
    public void Setattr_truncates_sets_times_and_the_read_only_bit()
    {
        var (_, volume) = TestVolumes.Create();
        var driver = new Driver(volume);
        var file = driver.Create(Root, "f");
        driver.Write(file.NodeId, 0, new byte[10_000]);

        var stamp = new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var set = new FuseSetAttrIn
        {
            Valid = Mount.Fuse.SetSize | Mount.Fuse.SetMTime | Mount.Fuse.SetMode,
            Size = 100,
            MTime = (ulong)stamp.ToUnixTimeSeconds(),
            Mode = 0x8124,   // r--r--r--
        };
        var (error, payload) = driver.Send(Mount.Fuse.Opcode.SetAttr, file.NodeId, set);
        Assert.Equal(0, error);
        var attr = MemoryMarshal.Read<FuseAttrOut>(payload).Attr;
        Assert.Equal(100UL, attr.Size);
        Assert.Equal((ulong)stamp.ToUnixTimeSeconds(), attr.MTime);
        Assert.Equal(0u, attr.Mode & 0x92);

        var node = volume.Resolve("f");
        Assert.Equal(100, node.Size);
        Assert.True((node.Attributes & FatxAttributes.ReadOnly) != 0);
    }

    [Fact]
    public void A_read_only_volume_refuses_every_change()
    {
        var (device, writable) = TestVolumes.Create();
        writable.CreateFile(writable.Root, "f");
        writable.Dispose();
        using var volume = FatxVolume.Open(device, writable: false);
        var driver = new Driver(volume);

        var file = driver.Lookup(Root, "f");
        Assert.Equal(0u, file.Attr.Mode & 0x92);
        Assert.Equal(-Errno.EROFS, driver.Send(Mount.Fuse.Opcode.Open, file.NodeId, new FuseOpenIn { Flags = 1 }).Error);
        Assert.Equal(0, driver.Send(Mount.Fuse.Opcode.Open, file.NodeId, new FuseOpenIn { Flags = 0 }).Error);
        Assert.Equal(-Errno.EROFS, driver.Send(Mount.Fuse.Opcode.Create, Root, new FuseCreateIn(), "g").Error);
        Assert.Equal(-Errno.EROFS, driver.Send(Mount.Fuse.Opcode.MkDir, Root, new FuseMkDirIn(), "d").Error);
        Assert.Equal(-Errno.EROFS, driver.Send(Mount.Fuse.Opcode.Unlink, Root, "f").Error);
        Assert.Equal(-Errno.EROFS, driver.Send(Mount.Fuse.Opcode.SetAttr, file.NodeId,
            new FuseSetAttrIn { Valid = Mount.Fuse.SetSize }).Error);
    }

    [Fact]
    public void Forgotten_nodes_are_let_go()
    {
        var (_, volume) = TestVolumes.Create();
        var driver = new Driver(volume);
        var file = driver.Create(Root, "f");
        driver.Lookup(Root, "f");
        Assert.Equal(1, driver.Fuse.KnownNodes);

        // forget takes no reply
        Assert.Empty(driver.Send(Mount.Fuse.Opcode.Forget, file.NodeId, new FuseForgetIn { NLookup = 1 }).Payload);
        Assert.Equal(0, driver.Send(Mount.Fuse.Opcode.GetAttr, file.NodeId, new byte[16]).Error);
        driver.Send(Mount.Fuse.Opcode.Forget, file.NodeId, new FuseForgetIn { NLookup = 1 });
        Assert.Equal(0, driver.Fuse.KnownNodes);
        Assert.Equal(-Errno.ENOENT, driver.Send(Mount.Fuse.Opcode.GetAttr, file.NodeId, new byte[16]).Error);
    }

    [Fact]
    public void Statfs_reports_the_partition_size()
    {
        var (_, volume) = TestVolumes.Create();
        var driver = new Driver(volume);
        var (error, payload) = driver.Send(Mount.Fuse.Opcode.StatFs, Root);
        Assert.Equal(0, error);
        var stat = MemoryMarshal.Read<FuseStatFsOut>(payload);
        Assert.Equal((uint)volume.ClusterSize, stat.BSize);
        Assert.Equal((ulong)volume.TotalBytes, stat.Blocks * stat.BSize);
        Assert.Equal((ulong)volume.FreeBytes, stat.BFree * stat.BSize);
        Assert.Equal((uint)FatxName.MaxLength, stat.NameLen);
    }

    [Fact]
    public void Unknown_requests_get_enosys()
    {
        var (_, volume) = TestVolumes.Create();
        var driver = new Driver(volume);
        Assert.Equal(-Errno.ENOSYS, driver.Send(Mount.Fuse.Opcode.Symlink, Root, "a", "b").Error);
        Assert.Equal(-Errno.ENOSYS, driver.Send(Mount.Fuse.Opcode.GetXAttr, Root, new byte[8], "user.x").Error);
    }
}
