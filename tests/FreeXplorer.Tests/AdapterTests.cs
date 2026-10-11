using System.Runtime.InteropServices;
using Fsp;
using FreeXplorer.Fatx;
using FreeXplorer.Mount;
using FreeXplorer.Storage;
using WinFileInfo = Fsp.Interop.FileInfo;

namespace FreeXplorer.Tests;

// Drives the WinFsp callbacks by hand, the same way the driver would, minus the driver.
public class AdapterTests : IDisposable
{
    const uint DirectoryFile = FileSystemBase.FILE_DIRECTORY_FILE;
    const int Ok = FileSystemBase.STATUS_SUCCESS;

    readonly MemoryBlockDevice device;
    readonly FatxVolume volume;
    readonly FatxFileSystem fs;

    public AdapterTests()
    {
        (device, volume) = TestVolumes.Create(64L << 20);
        fs = new FatxFileSystem(volume, "Test");
    }

    public void Dispose() => volume.Dispose();

    FatxNode Make(string path, int size = 0, bool directory = false)
    {
        int status = fs.Create(path, directory ? DirectoryFile : 0, 0, 0, null!, 0,
            out object node, out _, out _, out _);
        Assert.Equal(Ok, status);
        var n = (FatxNode)node;
        if (size > 0)
            WriteBytes(n, 0, TestVolumes.RandomBytes(size, size));
        return n;
    }

    uint WriteBytes(FatxNode node, ulong offset, byte[] data, bool constrained = false, bool toEnd = false)
    {
        var buffer = Marshal.AllocHGlobal(Math.Max(1, data.Length));
        try
        {
            Marshal.Copy(data, 0, buffer, data.Length);
            int status = fs.Write(node, null!, buffer, offset, (uint)data.Length, toEnd, constrained, out uint n, out _);
            Assert.Equal(Ok, status);
            return n;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    (int Status, byte[] Data) ReadBytes(FatxNode node, ulong offset, int length)
    {
        var buffer = Marshal.AllocHGlobal(Math.Max(1, length));
        try
        {
            int status = fs.Read(node, null!, buffer, offset, (uint)length, out uint n);
            var data = new byte[n];
            Marshal.Copy(buffer, data, 0, (int)n);
            return (status, data);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    List<string> Enumerate(FatxNode dir, string? marker = null)
    {
        object? context = null;
        var names = new List<string>();
        while (fs.ReadDirectoryEntry(dir, null!, null!, marker!, ref context!, out string name, out _))
            names.Add(name);
        return names;
    }

    [Fact]
    public void Volume_info_reports_size_and_free_space()
    {
        Assert.Equal(Ok, fs.GetVolumeInfo(out var info));
        Assert.Equal((ulong)volume.TotalBytes, info.TotalSize);
        Assert.True(info.FreeSize > 0 && info.FreeSize < info.TotalSize);
    }

    [Fact]
    public void Create_write_read()
    {
        var file = Make("\\hello.txt");
        var data = TestVolumes.RandomBytes(40_000, 1);
        Assert.Equal(40_000u, WriteBytes(file, 0, data));

        var (status, back) = ReadBytes(file, 0, 40_000);
        Assert.Equal(Ok, status);
        Assert.Equal(data, back);
        Assert.Equal(Ok, fs.GetFileInfo(file, null!, out var info));
        Assert.Equal(40_000ul, info.FileSize);
        Assert.True(info.AllocationSize >= 40_000);
        Assert.Equal(0u, info.FileAttributes & 0x10);
    }

    [Fact]
    public void Reading_past_the_end()
    {
        var file = Make("\\f", 100);
        Assert.Equal(FileSystemBase.STATUS_END_OF_FILE, ReadBytes(file, 100, 10).Status);
        var (status, data) = ReadBytes(file, 90, 50);
        Assert.Equal(Ok, status);
        Assert.Equal(10, data.Length);
    }

    [Fact]
    public void Writing_to_end_of_file_appends()
    {
        var file = Make("\\log", 10);
        WriteBytes(file, 0, new byte[] { 1, 2, 3 }, toEnd: true);
        Assert.Equal(13, file.Size);
    }

    [Fact]
    public void Constrained_writes_never_grow_the_file()
    {
        var file = Make("\\c", 100);
        Assert.Equal(0u, WriteBytes(file, 200, new byte[50], constrained: true));
        Assert.Equal(20u, WriteBytes(file, 80, new byte[50], constrained: true));
        Assert.Equal(100, file.Size);
    }

    [Fact]
    public void Open_reports_missing_names_and_missing_parents()
    {
        Make("\\dir", directory: true);
        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND,
            fs.Open("\\dir\\nope", 0, 0, out _, out _, out _, out _));
        Assert.Equal(FileSystemBase.STATUS_OBJECT_PATH_NOT_FOUND,
            fs.Open("\\nodir\\nope", 0, 0, out _, out _, out _, out _));
        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND,
            fs.Open("\\nope", 0, 0, out _, out _, out _, out _));
    }

    [Fact]
    public void Open_respects_directory_flags()
    {
        Make("\\d", directory: true);
        Make("\\f");
        Assert.Equal(FileSystemBase.STATUS_NOT_A_DIRECTORY,
            fs.Open("\\f", DirectoryFile, 0, out _, out _, out _, out _));
        Assert.Equal(FileSystemBase.STATUS_FILE_IS_A_DIRECTORY,
            fs.Open("\\d", FileSystemBase.FILE_NON_DIRECTORY_FILE, 0, out _, out _, out _, out _));
        Assert.Equal(Ok, fs.Open("\\D", 0, 0, out object node, out _, out _, out string normalized));
        Assert.Equal("\\d", normalized);
    }

    [Fact]
    public void A_writable_volume_opens_files_for_writing()
    {
        Make("\\w.bin");
        Assert.Equal(Ok, fs.Open("\\w.bin", 0, 0x0002 | 0x0004 | 0x00010000, out _, out _, out _, out _));
    }

    [Fact]
    public void Create_refuses_duplicates_and_bad_names()
    {
        Make("\\a");
        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_COLLISION,
            fs.Create("\\A", 0, 0, 0, null!, 0, out _, out _, out _, out _));
        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_INVALID,
            fs.Create("\\bad?name", 0, 0, 0, null!, 0, out _, out _, out _, out _));
        Assert.Equal(FileSystemBase.STATUS_OBJECT_PATH_NOT_FOUND,
            fs.Create("\\missing\\x", 0, 0, 0, null!, 0, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData("\\$RECYCLE.BIN")]
    [InlineData("\\$recycle.bin")]
    public void No_recycle_bin_so_deleting_really_deletes(string path)
    {
        Assert.Equal(FileSystemBase.STATUS_ACCESS_DENIED,
            fs.Create(path, DirectoryFile, 0, 0, null!, 0, out _, out _, out _, out _));
        Assert.Null(volume.Find("\\$RECYCLE.BIN"));
    }

    [Fact]
    public void Only_the_recycle_bin_of_the_root_is_special()
    {
        Make("\\games", directory: true);
        Assert.Equal(Ok, fs.Create("\\games\\$RECYCLE.BIN", 0, 0, 0, null!, 0, out _, out _, out _, out _));
        Assert.Equal(Ok, fs.Create("\\$RECYCLE.BIN.old", 0, 0, 0, null!, 0, out _, out _, out _, out _));
    }

    [Fact]
    public void Directory_listing_is_sorted_and_resumable()
    {
        var dir = Make("\\d", directory: true);
        foreach (var name in new[] { "zeta", "Alpha", "beta", "gamma", "Delta" })
            Make("\\d\\" + name);

        var all = Enumerate(dir);
        Assert.Equal(new[] { ".", "..", "Alpha", "beta", "Delta", "gamma", "zeta" }, all);

        // WinFsp resumes with the last name it saw
        Assert.Equal(new[] { "gamma", "zeta" }, Enumerate(dir, "Delta"));
        Assert.Equal(new[] { "zeta" }, Enumerate(dir, "gamma"));
        Assert.Empty(Enumerate(dir, "zeta"));
    }

    [Fact]
    public void The_root_listing_has_no_dot_entries()
    {
        Make("\\one");
        var root = volume.Root;
        Assert.Equal(new[] { "one" }, Enumerate(root));
    }

    [Fact]
    public void Delete_needs_an_empty_directory_and_happens_on_cleanup()
    {
        var dir = Make("\\d", directory: true);
        var file = Make("\\d\\f", 5000);

        Assert.Equal(FileSystemBase.STATUS_DIRECTORY_NOT_EMPTY, fs.CanDelete(dir, null!, "\\d"));
        Assert.Equal(Ok, fs.CanDelete(file, null!, "\\d\\f"));

        fs.Cleanup(file, null!, "\\d\\f", FileSystemBase.CleanupDelete);
        Assert.Null(volume.Find("d\\f"));

        Assert.Equal(Ok, fs.CanDelete(dir, null!, "\\d"));
        fs.Cleanup(dir, null!, "\\d", FileSystemBase.CleanupDelete);
        Assert.Null(volume.Find("d"));
        TestVolumes.AssertClean(volume);
    }

    [Fact]
    public void Rename_moves_and_replaces()
    {
        var a = Make("\\a", 10);
        var dir = Make("\\dir", directory: true);
        var b = Make("\\b", 20);

        Assert.Equal(Ok, fs.Rename(a, null!, "\\a", "\\dir\\moved", false));
        Assert.NotNull(volume.Find("dir\\moved"));
        Assert.Equal("\\dir\\moved", a.Path);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, fs.Rename(b, null!, "\\b", "\\dir\\moved", false));
        Assert.Equal(Ok, fs.Rename(b, null!, "\\b", "\\dir\\moved", true));
        Assert.Equal(20, volume.Find("dir\\moved")!.Size);

        Assert.Equal(Ok, fs.Rename(dir, null!, "\\dir", "\\renamed", false));
        Assert.NotNull(volume.Find("renamed\\moved"));
        TestVolumes.AssertClean(volume);
    }

    [Fact]
    public void A_directory_cant_be_replaced_by_a_file()
    {
        Make("\\folder", directory: true);
        var file = Make("\\file");
        Assert.Equal(FileSystemBase.STATUS_ACCESS_DENIED, fs.Rename(file, null!, "\\file", "\\folder", true));
        Assert.Equal(FileSystemBase.STATUS_OBJECT_PATH_NOT_FOUND, fs.Rename(file, null!, "\\file", "\\nowhere\\file", false));
    }

    [Fact]
    public void Truncate_and_extend_through_set_file_size()
    {
        var file = Make("\\s", 50_000);
        Assert.Equal(Ok, fs.SetFileSize(file, null!, 1000, false, out var info));
        Assert.Equal(1000ul, info.FileSize);

        // an allocation size bigger than the file is only a promise
        Assert.Equal(Ok, fs.SetFileSize(file, null!, 80_000, true, out info));
        Assert.Equal(1000ul, info.FileSize);

        Assert.Equal(Ok, fs.SetFileSize(file, null!, 20_000, false, out info));
        Assert.Equal(20_000ul, info.FileSize);
        Assert.All(ReadBytes(file, 1000, 19_000).Data, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Overwrite_empties_the_file()
    {
        var file = Make("\\o", 30_000);
        Assert.Equal(Ok, fs.Overwrite(file, null!, 0, false, 0, out var info));
        Assert.Equal(0ul, info.FileSize);
        Assert.Equal(0, file.Size);
    }

    [Fact]
    public void Basic_info_sets_attributes_and_times()
    {
        var file = Make("\\t");
        var when = new DateTime(2015, 5, 6, 7, 8, 10, DateTimeKind.Utc);
        ulong stamp = (ulong)when.ToFileTimeUtc();

        Assert.Equal(Ok, fs.SetBasicInfo(file, null!, 0x01 | 0x02, stamp, stamp, stamp, 0, out var info));
        Assert.True(file.Attributes.HasFlag(FatxAttributes.ReadOnly | FatxAttributes.Hidden));
        Assert.Equal(0x03u, info.FileAttributes & 0x03);
        Assert.Equal(stamp, info.LastWriteTime);

        // zero and "invalid" mean leave it alone
        Assert.Equal(Ok, fs.SetBasicInfo(file, null!, 0xFFFFFFFF, 0, 0, 0, 0, out info));
        Assert.Equal(0x03u, info.FileAttributes & 0x03);
    }

    [Fact]
    public void Plain_files_report_the_normal_attribute()
    {
        var dirInfo = Make("\\d", directory: true);
        Assert.Equal(Ok, fs.GetFileInfo(dirInfo, null!, out var info));
        Assert.Equal(0x10u, info.FileAttributes & 0x10);

        fs.GetSecurityByName("\\d", out uint attrs, ref NullBytes);
        Assert.Equal(0x10u, attrs & 0x10);
    }

    static byte[] NullBytes = null!;

    [Fact]
    public void Security_descriptor_is_handed_out_when_asked_for()
    {
        Make("\\f");
        byte[] sd = new byte[0];
        Assert.Equal(Ok, fs.GetSecurityByName("\\f", out _, ref sd));
        Assert.True(sd.Length > 20);
        Assert.Equal(Ok, fs.SetSecurity(null!, null!, System.Security.AccessControl.AccessControlSections.All, sd));
    }

    [Fact]
    public void Setting_the_label_writes_name_txt()
    {
        Assert.Equal(Ok, fs.SetVolumeLabel("Games", out _));
        Assert.Equal("Games", volume.Label);
        Assert.NotNull(volume.Find("name.txt"));
    }

    [Fact]
    public void Exceptions_become_the_right_status_codes()
    {
        int Map(FatxError e) => FatxFileSystem.ToStatus(new FatxException(e, "x"));
        Assert.Equal(FileSystemBase.STATUS_DISK_FULL, Map(FatxError.DiskFull));
        Assert.Equal(FileSystemBase.STATUS_MEDIA_WRITE_PROTECTED, Map(FatxError.ReadOnly));
        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Map(FatxError.AlreadyExists));
        Assert.Equal(FileSystemBase.STATUS_FILE_CORRUPT_ERROR, Map(FatxError.Corrupt));
        Assert.Equal(FileSystemBase.STATUS_IO_DEVICE_ERROR, FatxFileSystem.ToStatus(new IOException()));
        Assert.Equal(FileSystemBase.STATUS_ACCESS_DENIED, FatxFileSystem.ToStatus(new UnauthorizedAccessException()));
    }

    [Fact]
    public void Writes_that_dont_fit_report_disk_full_through_the_handler()
    {
        var file = Make("\\big");
        var buffer = Marshal.AllocHGlobal(100_000_000);
        try
        {
            // the real driver routes exceptions through ExceptionHandler
            var ex = Assert.Throws<FatxException>(() =>
                fs.Write(file, null!, buffer, 0, 100_000_000, false, false, out _, out _));
            Assert.Equal(FileSystemBase.STATUS_DISK_FULL, fs.ExceptionHandler(ex));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

public class AdapterReadOnlyTests : IDisposable
{
    readonly FatxVolume volume;
    readonly FatxFileSystem fs;
    readonly FatxNode file;

    public AdapterReadOnlyTests()
    {
        var (dev, writable) = TestVolumes.Create(32L << 20);
        file = writable.CreateFile(writable.Root, "keep.bin");
        writable.Write(file, 0, new byte[5000]);
        writable.Dispose();
        volume = FatxVolume.Open(dev, writable: false);
        file = volume.Resolve("keep.bin");
        fs = new FatxFileSystem(volume, "RO");
    }

    public void Dispose() => volume.Dispose();

    [Fact]
    public void Every_change_is_refused()
    {
        int protectedStatus = FileSystemBase.STATUS_MEDIA_WRITE_PROTECTED;
        Assert.True(fs.ReadOnly);
        Assert.Equal(protectedStatus, fs.Create("\\new", 0, 0, 0, null!, 0, out _, out _, out _, out _));
        Assert.Equal(protectedStatus, fs.Overwrite(file, null!, 0, false, 0, out _));
        Assert.Equal(protectedStatus, fs.SetFileSize(file, null!, 1, false, out _));
        Assert.Equal(protectedStatus, fs.CanDelete(file, null!, "\\keep.bin"));
        Assert.Equal(protectedStatus, fs.Rename(file, null!, "\\keep.bin", "\\other", false));
        Assert.Equal(protectedStatus, fs.SetBasicInfo(file, null!, 1, 0, 0, 0, 0, out _));
        Assert.Equal(protectedStatus, fs.SetVolumeLabel("x", out _));

        var buffer = Marshal.AllocHGlobal(10);
        try
        {
            Assert.Equal(protectedStatus, fs.Write(file, null!, buffer, 0, 10, false, false, out _, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData(0x0002u)]       // write data
    [InlineData(0x0004u)]       // append data
    [InlineData(0x00010000u)]   // delete
    [InlineData(0x0003u)]
    public void Opening_for_writing_is_refused_up_front(uint access)
    {
        Assert.Equal(FileSystemBase.STATUS_MEDIA_WRITE_PROTECTED,
            fs.Open("\\keep.bin", 0, access | 0x80, out _, out _, out _, out _));
    }

    [Fact]
    public void Opening_for_reading_and_attributes_is_fine()
    {
        // read data, read attributes, read EA, synchronize, read control
        uint readOnly = 0x0001 | 0x0080 | 0x0008 | 0x00100000 | 0x00020000;
        Assert.Equal(FileSystemBase.STATUS_SUCCESS,
            fs.Open("\\keep.bin", 0, readOnly, out _, out _, out _, out _));
        Assert.Equal(FileSystemBase.STATUS_SUCCESS,
            fs.Open("\\", 0, readOnly, out _, out _, out _, out _));
    }

    [Fact]
    public void Reading_still_works_and_nothing_got_deleted_by_cleanup()
    {
        fs.Cleanup(file, null!, "\\keep.bin", FileSystemBase.CleanupDelete);
        Assert.NotNull(volume.Find("keep.bin"));

        var buffer = Marshal.AllocHGlobal(100);
        try
        {
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, fs.Read(file, null!, buffer, 0, 100, out uint n));
            Assert.Equal(100u, n);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void Changing_nothing_is_not_an_error()
    {
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fs.SetBasicInfo(file, null!, 0xFFFFFFFF, 0, 0, 0, 0, out _));
    }
}
