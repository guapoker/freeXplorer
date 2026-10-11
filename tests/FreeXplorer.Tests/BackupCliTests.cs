using System.Security.Cryptography;
using System.Text;
using FreeXplorer.Cli;
using FreeXplorer.Storage;

namespace FreeXplorer.Tests;

public class BackupCliTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "fx-bkcli-" + Guid.NewGuid().ToString("N"));

    public BackupCliTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, true);

    string P(string name) => Path.Combine(dir, name);

    (int Code, string Out, string Err) RunWithInput(string input, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = Cli.Cli.Run(args, new CliContext(o, e, new StringReader(input)));
        return (code, o.ToString(), e.ToString());
    }

    (int Code, string Out, string Err) Run(params string[] args) => RunWithInput("", args);

    // a small drive with something on it
    string Practice(string name = "practice.img")
    {
        string image = P(name);
        Assert.Equal(0, Run("new-image", image, "--size", "64M", "--partition", "--label", "Practice").Code);
        File.WriteAllBytes(P("save.bin"), TestVolumes.RandomBytes(250_000, 3));
        Assert.Equal(0, Run("put", image, P("save.bin"), "\\save.bin", "--quiet").Code);
        return image;
    }

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    [Fact]
    public void Backup_copies_the_drive_and_adds_a_checksum()
    {
        string image = Practice();
        var r = Run("backup", image, P("saved.img"));

        Assert.Equal(0, r.Code);
        Assert.Equal(Hash(image), Hash(P("saved.img")));
        Assert.StartsWith(Hash(image), File.ReadAllText(P("saved.img.sha256")));
        Assert.Contains("SHA-256 " + Hash(image), r.Out);
        Assert.Contains("read back and checked", r.Out);
    }

    [Fact]
    public void Restoring_into_a_new_file_makes_an_identical_drive()
    {
        string image = Practice();
        Run("backup", image, P("saved.img"));

        var r = Run("restore", P("saved.img"), P("clone.img"));

        Assert.Equal(0, r.Code);
        Assert.Equal(Hash(image), Hash(P("clone.img")));
        Assert.Equal(0, Run("fsck", P("clone.img")).Code);
        Assert.Contains("save.bin", Run("ls", P("clone.img"), "\\").Out);
    }

    [Fact]
    public void An_existing_destination_is_not_overwritten_by_a_backup()
    {
        string image = Practice();
        File.WriteAllText(P("taken.img"), "mine");

        var r = Run("backup", image, P("taken.img"));
        Assert.Equal(2, r.Code);
        Assert.Contains("already something", r.Err);
        Assert.Equal("mine", File.ReadAllText(P("taken.img")));
    }

    [Fact]
    public void Restoring_over_an_existing_image_asks_first()
    {
        string image = Practice();
        Run("backup", image, P("saved.img"));
        File.WriteAllBytes(P("target.img"), new byte[64 << 20]);

        var declined = RunWithInput("no", "restore", P("saved.img"), P("target.img"));
        Assert.Equal(1, declined.Code);
        Assert.Contains("Not confirmed", declined.Out);
        Assert.All(File.ReadAllBytes(P("target.img")).Take(100_000), b => Assert.Equal(0, b));

        var yes = Run("restore", P("saved.img"), P("target.img"), "--yes");
        Assert.Equal(0, yes.Code);
        Assert.Equal(Hash(image), Hash(P("target.img")));
    }

    [Fact]
    public void Too_small_a_target_is_refused_up_front()
    {
        string image = Practice();
        Run("backup", image, P("saved.img"));
        File.WriteAllBytes(P("small.img"), new byte[32 << 20]);

        var r = Run("restore", P("saved.img"), P("small.img"), "--yes");
        Assert.Equal(2, r.Code);
        Assert.Contains("at least as big", r.Err);
        Assert.DoesNotContain("overwritten", r.Out);
    }

    [Fact]
    public void A_damaged_backup_is_refused_and_the_target_is_left_alone()
    {
        string image = Practice();
        Run("backup", image, P("saved.img"));
        using (var f = File.OpenWrite(P("saved.img")))
        {
            f.Position = 8_000_000;
            f.WriteByte(0x7F);
        }
        File.WriteAllBytes(P("target.img"), new byte[64 << 20]);

        var r = Run("restore", P("saved.img"), P("target.img"), "--yes");

        Assert.Equal(1, r.Code);
        Assert.Contains("damaged", r.Err);
        Assert.All(File.ReadAllBytes(P("target.img")), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Failed_restore_into_a_new_file_leaves_nothing()
    {
        string image = Practice();
        Run("backup", image, P("saved.img"));
        using (var f = File.OpenWrite(P("saved.img")))
        {
            f.Position = 1_000_000;
            f.WriteByte(0x7F);
        }

        var r = Run("restore", P("saved.img"), P("never.img"));
        Assert.Equal(1, r.Code);
        Assert.False(File.Exists(P("never.img")));
    }

    // the same thing Ctrl+C does: the token is cancelled before the job gets going
    (int Code, string Out, string Err) RunStopped(params string[] args)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var o = new StringWriter();
        var e = new StringWriter();
        int code = Cli.Cli.Run(args, new CliContext(o, e, new StringReader(""), cts.Token));
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void A_stopped_backup_leaves_no_file_behind()
    {
        string image = Practice();
        var r = RunStopped("backup", image, P("stopped.img"));

        Assert.Equal(1, r.Code);
        Assert.Contains("partly written backup was deleted", r.Out);
        Assert.False(File.Exists(P("stopped.img")));
        Assert.False(File.Exists(P("stopped.img.sha256")));
    }

    [Fact]
    public void Restore_stopped_before_writing()
    {
        string image = Practice();
        Run("backup", image, P("saved.img"));
        File.WriteAllBytes(P("target.img"), new byte[64 << 20]);

        var r = RunStopped("restore", P("saved.img"), P("target.img"), "--yes");

        Assert.Equal(1, r.Code);
        Assert.Contains("before anything was written", r.Out);
        Assert.All(File.ReadAllBytes(P("target.img")).Take(100_000), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Stopped_restore_into_a_new_file_removes_it()
    {
        string image = Practice();
        Run("backup", image, P("saved.img"));

        var r = RunStopped("restore", P("saved.img"), P("new-target.img"));

        Assert.Equal(1, r.Code);
        Assert.Contains("partly written image was deleted", r.Out);
        Assert.False(File.Exists(P("new-target.img")));
    }

    // a small image with a security sector in it, the way a console drive has one
    string ConsoleImage(string name, string serial, long claimedLength = 64L << 20, int seed = 1)
    {
        string path = P(name);
        using var device = FileBlockDevice.CreateImage(path, 64L << 20);
        device.Write(0, TestVolumes.RandomBytes(0x2000, seed));
        device.Write(0x2000, Encoding.ASCII.GetBytes(serial.PadRight(0x14)));
        device.Write(0x2058, BitConverter.GetBytes((uint)(claimedLength / 512)));
        device.Write(0x80000, TestVolumes.RandomBytes(2_000_000, seed + 1));
        return path;
    }

    [Fact]
    public void Keep_header_leaves_the_first_512_kb()
    {
        string source = ConsoleImage("source.img", "SOURCE-SERIAL", seed: 1);
        string target = ConsoleImage("target.img", "TARGET-SERIAL", seed: 10);
        Run("backup", source, P("source.bak"));
        byte[] targetStart = File.ReadAllBytes(target)[..0x80000];

        var r = Run("restore", P("source.bak"), target, "--keep-header", "--yes");

        Assert.Equal(0, r.Code);
        Assert.Contains("left as it was", r.Out);
        var after = File.ReadAllBytes(target);
        Assert.Equal(targetStart, after[..0x80000]);
        Assert.Equal(File.ReadAllBytes(source)[0x80000..], after[0x80000..]);
        Assert.Contains("TARGET-SERIAL", Encoding.ASCII.GetString(after, 0x2000, 0x14));
    }

    [Fact]
    public void Without_keep_header_everything_is_replaced()
    {
        string source = ConsoleImage("source.img", "SOURCE-SERIAL", seed: 1);
        string target = ConsoleImage("target.img", "TARGET-SERIAL", seed: 10);
        Run("backup", source, P("source.bak"));

        Assert.Equal(0, Run("restore", P("source.bak"), target, "--yes").Code);
        Assert.Equal(Hash(source), Hash(target));
        Assert.Contains("SOURCE-SERIAL", Encoding.ASCII.GetString(File.ReadAllBytes(target), 0x2000, 0x14));
    }

    [Fact]
    public void Keep_header_refused_when_sizes_differ()
    {
        string source = ConsoleImage("source.img", "SOURCE-SERIAL");
        string other = ConsoleImage("other.img", "OTHER-SERIAL", claimedLength: 32L << 20, seed: 5);
        Run("backup", source, P("source.bak"));
        string before = Hash(other);

        var r = Run("restore", P("source.bak"), other, "--keep-header", "--yes");

        Assert.Equal(2, r.Code);
        Assert.Contains("same size", r.Err);
        Assert.Equal(before, Hash(other));
    }

    [Fact]
    public void Keep_header_refused_without_a_security_sector()
    {
        string source = ConsoleImage("source.img", "SOURCE-SERIAL");
        Run("backup", source, P("source.bak"));
        File.WriteAllBytes(P("blank.img"), new byte[64 << 20]);

        var r = Run("restore", P("source.bak"), P("blank.img"), "--keep-header", "--yes");

        Assert.Equal(2, r.Code);
        Assert.Contains("nothing to keep", r.Err);
        Assert.All(File.ReadAllBytes(P("blank.img")).Take(200_000), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Keep_header_refused_for_a_new_file()
    {
        string source = ConsoleImage("source.img", "SOURCE-SERIAL");
        Run("backup", source, P("source.bak"));

        var r = Run("restore", P("source.bak"), P("brand-new.img"), "--keep-header");

        Assert.Equal(2, r.Code);
        Assert.False(File.Exists(P("brand-new.img")));
    }

    [Fact]
    public void Help_mentions_keep_header()
    {
        Assert.Contains("--keep-header", Run("help").Out);
    }

    [Fact]
    public void Missing_pieces_are_usage_errors()
    {
        Assert.Equal(2, Run("backup").Code);
        Assert.Equal(2, Run("backup", Practice()).Code);
        Assert.Equal(2, Run("restore").Code);
        Assert.Equal(2, Run("restore", P("not-there.img"), P("x.img")).Code);
        Assert.Equal(1, Run("backup", P("not-there.img"), P("x.img")).Code);
    }

    [Fact]
    public void Help_lists_the_new_commands()
    {
        var r = Run("help");
        Assert.Contains("backup <target> <file>", r.Out);
        Assert.Contains("restore <file> <target>", r.Out);
    }
}
