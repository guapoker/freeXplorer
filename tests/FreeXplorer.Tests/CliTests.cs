using System.Security.Cryptography;
using FreeXplorer.Cli;
using FreeXplorer.Mount;

namespace FreeXplorer.Tests;

public class CliTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "fx-cli-" + Guid.NewGuid().ToString("N"));

    public CliTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    string P(string name) => Path.Combine(dir, name);

    (int Code, string Out, string Err) RunWithInput(string input, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = Cli.Cli.Run(args, new CliContext(o, e, new StringReader(input)));
        return (code, o.ToString(), e.ToString());
    }

    (int Code, string Out, string Err) Run(params string[] args) => RunWithInput("", args);

    string NewImage(long gigabytes = 6)
    {
        string image = P("drive.img");
        var r = Run("new-image", image, "--size", gigabytes + "G");
        Assert.Equal(0, r.Code);
        return image;
    }

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // big sparse images are slow to hash, a dry run that wrote something would show up here anyway
    static (long Length, DateTime Written) Stamp(string path)
    {
        var info = new System.IO.FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    [Fact]
    public void No_arguments_shows_help_and_asks_for_a_command()
    {
        var r = Run();
        Assert.Equal(2, r.Code);
        Assert.Contains("usage:", r.Out);
        Assert.Equal(0, Run("help").Code);
        Assert.Equal(0, Run("--help").Code);
    }

    [Fact]
    public void Unknown_commands_and_options_are_usage_errors()
    {
        Assert.Equal(2, Run("frobnicate").Code);
        Assert.Equal(2, Run("ls", NewImage(), "--nonsense").Code);
        Assert.Equal(2, Run("ls").Code);
    }

    [Fact]
    public void Version_prints_something()
    {
        var r = Run("version");
        Assert.Equal(0, r.Code);
        Assert.StartsWith("freeXplorer ", r.Out);
    }

    [Fact]
    public void New_image_has_the_four_fatx_partitions()
    {
        string image = NewImage();
        var r = Run("info", image);
        Assert.Equal(0, r.Code);
        foreach (var id in new[] { "sysext ", "sysext2", "compat ", "content", "syscache", "gamecache" })
            Assert.Contains(id, r.Out);
        Assert.Contains("XTAF, 16 KB clusters", r.Out);
        Assert.Contains("console cache, not FATX", r.Out);
    }

    [Fact]
    public void New_image_wont_overwrite_or_make_a_tiny_layout()
    {
        string image = NewImage();
        Assert.Equal(2, Run("new-image", image, "--size", "6G").Code);
        Assert.Equal(2, Run("new-image", P("small.img"), "--size", "1G").Code);
        Assert.False(File.Exists(P("small.img")));
        Assert.Equal(0, Run("new-image", P("small.img"), "--size", "64M", "--partition").Code);
    }

    [Fact]
    public void A_folder_survives_the_round_trip()
    {
        string image = NewImage();
        string src = P("src");
        Directory.CreateDirectory(Path.Combine(src, "0000000000000000", "4D5307E6"));
        var rng = new Random(3);
        for (int i = 0; i < 6; i++)
        {
            var bytes = new byte[rng.Next(1, 700_000)];
            rng.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(src, "0000000000000000", "4D5307E6", $"f{i}.bin"), bytes);
        }
        File.WriteAllText(Path.Combine(src, "readme.txt"), "hello");

        var put = Run("put", image, "content", src, "\\Content", "--quiet");
        Assert.Equal(0, put.Code);
        Assert.Contains("7 files", put.Out);

        string back = P("back");
        var get = Run("get", image, "content", "\\Content", back, "--quiet");
        Assert.Equal(0, get.Code);

        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string twin = Path.Combine(back, Path.GetRelativePath(src, file));
            Assert.True(File.Exists(twin), twin);
            Assert.Equal(Hash(file), Hash(twin));
        }
        Assert.Contains("Clean", Run("fsck", image, "content").Out);
    }

    [Fact]
    public void Put_into_an_existing_folder_keeps_the_name()
    {
        string image = NewImage();
        string file = P("save.dat");
        File.WriteAllText(file, "x");
        Assert.Equal(0, Run("mkdir", image, "content", "\\Saves").Code);
        Assert.Equal(0, Run("put", image, "content", file, "\\Saves", "--quiet").Code);
        Assert.Contains("save.dat", Run("ls", image, "content", "\\Saves").Out);
    }

    [Fact]
    public void The_partition_can_be_left_out_when_theres_only_one_choice()
    {
        string image = P("one.img");
        Assert.Equal(0, Run("new-image", image, "--size", "64M", "--partition").Code);
        File.WriteAllText(P("a.txt"), "hi");
        Assert.Equal(0, Run("put", image, P("a.txt"), "\\a.txt", "--quiet").Code);
        Assert.Contains("a.txt", Run("ls", image).Out);
        Assert.Contains("a.txt", Run("ls", image, "image").Out);

        // with the full layout you have to say which
        string drive = NewImage();
        Assert.Equal(2, Run("ls", drive).Code);
    }

    [Fact]
    public void Ls_long_shows_sizes_and_directories_first()
    {
        string image = NewImage();
        File.WriteAllBytes(P("b.bin"), new byte[1234]);
        Run("mkdir", image, "content", "\\Zdir");
        Run("put", image, "content", P("b.bin"), "\\b.bin", "--quiet");

        var plain = Run("ls", image, "content").Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Zdir\\", plain[0].Trim());
        Assert.Equal("b.bin", plain[1].Trim());

        var detailed = Run("ls", image, "content", "-l").Out;
        Assert.Contains("1234", detailed);
        Assert.Contains("<dir>", detailed);
    }

    [Fact]
    public void Mv_and_rm()
    {
        string image = NewImage();
        File.WriteAllText(P("f.txt"), "data");
        Run("put", image, "content", P("f.txt"), "\\a\\b\\f.txt", "--quiet");
        Assert.Equal(0, Run("mv", image, "content", "\\a\\b\\f.txt", "\\moved.txt").Code);
        Assert.Contains("moved.txt", Run("ls", image, "content").Out);

        Assert.Equal(2, Run("rm", image, "content", "\\a").Code);       // not empty, no -r
        Assert.Equal(0, Run("rm", image, "content", "\\a", "-r").Code);
        Assert.Equal(0, Run("rm", image, "content", "\\moved.txt").Code);
        Assert.Equal("", Run("ls", image, "content").Out.Trim());
        Assert.Equal(2, Run("rm", image, "content", "\\").Code);          // not the whole partition
    }

    [Fact]
    public void Get_wont_clobber_without_force()
    {
        string image = NewImage();
        File.WriteAllText(P("f.txt"), "new");
        Run("put", image, "content", P("f.txt"), "\\f.txt", "--quiet");
        string target = P("existing.txt");
        File.WriteAllText(target, "old");

        Assert.Equal(1, Run("get", image, "content", "\\f.txt", target).Code);
        Assert.Equal("old", File.ReadAllText(target));
        Assert.Equal(0, Run("get", image, "content", "\\f.txt", target, "--force").Code);
        Assert.Equal("new", File.ReadAllText(target));
    }

    [Fact]
    public void Invalid_names_are_skipped_with_a_message_not_a_crash()
    {
        string image = NewImage();
        string folder = P("mixed");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "good.txt"), "1");
        File.WriteAllText(Path.Combine(folder, "caf\u00e9.txt"), "2");

        var r = Run("put", image, "content", folder, "\\Mixed", "--quiet");
        Assert.Equal(1, r.Code);
        Assert.Contains("café.txt", r.Err);
        Assert.Contains("good.txt", Run("ls", image, "content", "\\Mixed").Out);
    }

    [Fact]
    public void Format_dry_run_writes_nothing()
    {
        string image = NewImage();
        var before = Stamp(image);
        var r = Run("format", image, "--all", "--dry-run");
        Assert.Equal(0, r.Code);
        Assert.Contains("Dry run", r.Out);
        Assert.Contains("Content", r.Out);
        Assert.Equal(before, Stamp(image));
    }

    [Fact]
    public void Format_asks_first_and_does_nothing_without_a_yes()
    {
        string image = NewImage();
        File.WriteAllText(P("keep.txt"), "keep me");
        Run("put", image, "content", P("keep.txt"), "\\keep.txt", "--quiet");

        var no = RunWithInput("no\n", "format", image, "--partitions", "content");
        Assert.Equal(1, no.Code);
        Assert.Contains("Not confirmed", no.Out);
        Assert.Contains("keep.txt", Run("ls", image, "content").Out);

        var yes = RunWithInput("yes\n", "format", image, "--partitions", "content");
        Assert.Equal(0, yes.Code);
        Assert.Equal("", Run("ls", image, "content").Out.Trim());
    }

    [Fact]
    public void Formatting_one_partition_leaves_the_others_alone()
    {
        string image = NewImage();
        File.WriteAllText(P("c.txt"), "compat data");
        File.WriteAllText(P("d.txt"), "content data");
        Run("put", image, "compat", P("c.txt"), "\\c.txt", "--quiet");
        Run("put", image, "content", P("d.txt"), "\\d.txt", "--quiet");

        Assert.Equal(0, Run("format", image, "--partitions", "compat", "--yes").Code);
        Assert.Equal("", Run("ls", image, "compat").Out.Trim());
        Assert.Contains("d.txt", Run("ls", image, "content").Out);
    }

    [Fact]
    public void Format_needs_to_be_told_what_to_do()
    {
        string image = NewImage();
        Assert.Equal(2, Run("format", image, "--yes").Code);
        Assert.Equal(2, Run("format", image, "--all", "--whole", "--yes").Code);
        Assert.Equal(2, Run("format", image, "--partitions", "syscache", "--yes").Code);
        Assert.Equal(2, Run("format", image, "--partitions", "content", "--cluster-size", "3K", "--yes").Code);
    }

    [Fact]
    public void Whole_formats_an_entire_image_as_one_volume()
    {
        string image = P("raw.img");
        using (var fs = File.Create(image))
            fs.SetLength(64 << 20);
        var r = Run("format", image, "--whole", "--label", "Test", "--yes");
        Assert.Equal(0, r.Code);
        Assert.Contains("\"Test\"", Run("info", image).Out);
    }

    [Fact]
    public void Content_size_and_cluster_size_options_apply()
    {
        string image = NewImage(8);
        var r = Run("format", image, "--partitions", "content", "--content-size", "1G", "--cluster-size", "32K", "--dry-run");
        Assert.Equal(0, r.Code);
        Assert.Contains("1 GB", r.Out);
        Assert.Contains("32 KB clusters", r.Out);
    }

    [Fact]
    public void Physical_disks_that_dont_exist_are_reported()
    {
        var r = Run("info", "disk:99");
        Assert.Equal(1, r.Code);
        Assert.Contains("no disk 99", r.Err);
        Assert.Equal(1, Run("format", "disk:99", "--all", "--dry-run").Code);
    }

    [Fact]
    public void Whole_is_refused_for_physical_disks()
    {
        var r = Run("format", "disk:99", "--whole", "--yes");
        Assert.Equal(2, r.Code);
    }

    [Fact]
    public void Fsck_can_release_leaked_clusters()
    {
        string image = P("leak.img");
        Run("new-image", image, "--size", "64M", "--partition");

        // leak a cluster on purpose: mark one in the FAT as used without any file owning it
        using (var dev = new FreeXplorer.Storage.FileBlockDevice(image, true))
        {
            var geo = FreeXplorer.Fatx.FatxGeometry.Compute(dev.Length, 0x20);
            dev.Write(geo.FatOffset + 5 * geo.FatEntrySize, new byte[] { 0xFF, 0xFF });
        }

        var found = Run("fsck", image);
        Assert.Equal(0, found.Code);
        Assert.Contains("belong to nothing", found.Out);
        var fixedRun = Run("fsck", image, "--fix");
        Assert.Contains("released 1", fixedRun.Out);
        Assert.Contains("Clean", Run("fsck", image).Out);
    }

    [Fact]
    public void Fsck_reports_real_damage_with_a_failing_exit_code()
    {
        string image = P("broken.img");
        Run("new-image", image, "--size", "64M", "--partition");
        File.WriteAllBytes(P("f.bin"), new byte[200_000]);
        Run("put", image, P("f.bin"), "\\f.bin", "--quiet");

        // cut the chain: free the file's second cluster
        using (var dev = new FreeXplorer.Storage.FileBlockDevice(image, true))
        {
            var geo = FreeXplorer.Fatx.FatxGeometry.Compute(dev.Length, 0x20);
            using var vol = FreeXplorer.Fatx.FatxVolume.Open(dev);
            uint first = vol.Resolve("f.bin").FirstCluster;
            dev.Write(geo.FatOffset + (first + 1) * geo.FatEntrySize, new byte[geo.FatEntrySize]);
        }

        var r = Run("fsck", image);
        Assert.Equal(1, r.Code);
        Assert.Contains("error", r.Out);
    }

#if WINDOWS
    [Fact]
    public void Mount_explains_what_to_install_when_winfsp_is_missing()
    {
        if (WinFspMount.IsInstalled)
            return;   // on a machine that has it, this would really mount something
        string image = NewImage();
        var r = Run("mount", image, "content");
        Assert.Equal(1, r.Code);
        Assert.Contains("WinFsp", r.Err);
    }
#else
    [Fact]
    public void Mount_turns_down_drive_letters_on_linux()
    {
        if (!FuseMount.IsInstalled)
            return;
        string image = NewImage();
        var r = Run("mount", image, "content", "--letter", "X:");
        Assert.Equal(2, r.Code);
        Assert.Contains("--folder", r.Err);
    }
#endif

    [Fact]
    public void Sizes_parse_the_way_people_type_them()
    {
        Assert.Equal(64L << 20, SizeText.Parse("64M"));
        Assert.Equal(64L << 20, SizeText.Parse("64MB"));
        Assert.Equal(8L << 30, SizeText.Parse("8g"));
        Assert.Equal((long)(1.5 * (1L << 40)), SizeText.Parse("1.5T"));
        Assert.Equal(1000, SizeText.Parse("1000"));
        Assert.Throws<UsageException>(() => SizeText.Parse("lots"));
        Assert.Throws<UsageException>(() => SizeText.Parse("-4G"));
        Assert.Equal(0x20, SizeText.ClusterSectors("16K"));
        Assert.Throws<UsageException>(() => SizeText.ClusterSectors("48K"));
    }

    [Fact]
    public void Disk_specs_are_recognised()
    {
        Assert.True(Target.TryParseDisk("disk:3", out int n) && n == 3);
        Assert.True(Target.TryParseDisk(@"\\.\PhysicalDrive12", out n) && n == 12);
        Assert.False(Target.TryParseDisk("drive.img", out _));
        Assert.False(Target.TryParseDisk("disk:", out _));
    }
}
