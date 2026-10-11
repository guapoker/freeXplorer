using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using FreeXplorer.Fatx;
using FreeXplorer.Mount;
using FreeXplorer.Storage;

namespace FreeXplorer.Tests;

public class MountPointTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("*")]
    public void No_mount_point_means_any_free_letter(string? text)
    {
        var (letter, folder) = WinFspMount.ParseMountPoint(text);
        Assert.Null(letter);
        Assert.Null(folder);
    }

    [Theory]
    [InlineData("x", 'X')]
    [InlineData("X:", 'X')]
    [InlineData("z:", 'Z')]
    [InlineData(@"X:\", 'X')]
    [InlineData(@"\\.\X:", 'X')]
    [InlineData(@"\\?\q:", 'Q')]
    public void Every_spelling_of_a_drive_letter_is_recognised(string text, char letter)
    {
        var (found, folder) = WinFspMount.ParseMountPoint(text);
        Assert.Equal(letter, found);
        Assert.Null(folder);
    }

    [Theory]
    [InlineData(@"C:\mnt\content")]
    [InlineData(@"C:\mnt\")]
    [InlineData("mnt")]
    [InlineData(@"\\.\X:\sub")]
    [InlineData(@"\\server\share")]
    public void Anything_else_is_a_folder(string text)
    {
        var (letter, folder) = WinFspMount.ParseMountPoint(text);
        Assert.Null(letter);
        Assert.Equal(text, folder);
    }

    [Theory]
    [InlineData(@"\\.\X:", "X:")]
    [InlineData("y", "Y:")]
    [InlineData("Z:", "Z:")]
    [InlineData(@"C:\mnt\content", @"C:\mnt\content")]
    public void Drives_are_shown_the_short_way(string raw, string shown)
    {
        Assert.Equal(shown, WinFspMount.DisplayName(raw));
    }

    [Fact]
    public void Free_letters_count_down_from_Z_and_skip_the_taken_ones()
    {
        var all = WinFspMount.FreeDriveLetters(Array.Empty<char>());
        Assert.Equal("ZYXWVUTSRQPONMLKJIHGFED", new string(all.ToArray()));

        var some = WinFspMount.FreeDriveLetters(new[] { 'Z', 'y', 'F' });
        Assert.Equal('X', some[0]);
        Assert.DoesNotContain('Z', some);
        Assert.DoesNotContain('Y', some);
        Assert.DoesNotContain('F', some);
        Assert.Equal(20, some.Count);
    }

    [Fact]
    public void Letters_Windows_needs_for_itself_are_never_offered()
    {
        var offered = WinFspMount.FreeDriveLetters();
        Assert.DoesNotContain('A', offered);
        Assert.DoesNotContain('B', offered);
        Assert.DoesNotContain('C', offered);
        // whatever is in use right now stays out of the list
        foreach (string drive in Directory.GetLogicalDrives())
            Assert.DoesNotContain(char.ToUpperInvariant(drive[0]), offered);
    }
}

public class DriverInstallerTests : IDisposable
{
    readonly string work = Path.Combine(Path.GetTempPath(), "fx-setup-" + Guid.NewGuid().ToString("N"));
    readonly byte[] package = RandomNumberGenerator.GetBytes(300_000);

    public DriverInstallerTests() => Directory.CreateDirectory(work);

    public void Dispose() => Directory.Delete(work, true);

    sealed class Stub : HttpMessageHandler
    {
        readonly Func<HttpResponseMessage> answer;
        public Stub(Func<HttpResponseMessage> answer) => this.answer = answer;
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests++;
            return Task.FromResult(answer());
        }
    }

    HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    WinFspInstaller Installer(Func<string, string, Task<int>> run, byte[]? served = null, Func<bool>? installed = null, Stub? stub = null) =>
        new()
        {
            Url = new Uri("https://downloads.example/winfsp.msi"),
            Sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
            Handler = stub ?? new Stub(() => Ok(served ?? package)),
            WorkRoot = work,
            LockDownFolder = false,
            RequireAdministrator = false,
            IsInstalled = installed ?? (() => true),
            RunInstaller = run,
        };

    static Task<int> Exit(int code) => Task.FromResult(code);

    [Fact]
    public async Task Package_is_downloaded_checked_and_installed()
    {
        string? seenPath = null;
        byte[]? seenBytes = null;
        var steps = new List<string>();
        var fractions = new List<double>();

        var result = await Installer((msi, log) =>
        {
            seenPath = msi;
            seenBytes = File.ReadAllBytes(msi);
            return Exit(0);
        }).InstallAsync(steps.Add, fractions.Add);

        Assert.Equal(DriverInstallResult.Installed, result);
        Assert.Equal(package, seenBytes);
        Assert.EndsWith(".msi", seenPath);
        Assert.Equal(new[] { "Downloading WinFsp...", "Checking the download...", "Installing the driver..." }, steps);
        Assert.Equal(1.0, fractions[^1]);
        Assert.Equal(fractions.OrderBy(f => f), fractions);
    }

    [Fact]
    public async Task Nothing_is_left_behind_afterwards()
    {
        await Installer((_, _) => Exit(0)).InstallAsync();
        Assert.Empty(Directory.GetFileSystemEntries(work));
    }

    [Fact]
    public async Task Wrong_checksum_is_never_run()
    {
        bool ran = false;
        var tampered = (byte[])package.Clone();
        tampered[1000] ^= 1;

        var ex = await Assert.ThrowsAsync<DriverInstallException>(() =>
            Installer((_, _) => { ran = true; return Exit(0); }, served: tampered).InstallAsync());

        Assert.Contains("checksum", ex.Message);
        Assert.False(ran);
        Assert.Empty(Directory.GetFileSystemEntries(work));
    }

    [Fact]
    public async Task A_server_error_says_where_to_get_it_by_hand()
    {
        bool ran = false;
        var stub = new Stub(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<DriverInstallException>(() =>
            Installer((_, _) => { ran = true; return Exit(0); }, stub: stub).InstallAsync());

        Assert.Contains("Couldn't download", ex.Message);
        Assert.Contains(WinFspMount.DownloadPage, ex.Message);
        Assert.False(ran);
        Assert.Empty(Directory.GetFileSystemEntries(work));
    }

    [Fact]
    public async Task A_download_far_bigger_than_expected_is_refused()
    {
        var installer = new WinFspInstaller
        {
            Url = new Uri("https://downloads.example/winfsp.msi"),
            Sha256 = "00",
            Handler = new Stub(() => Ok(new byte[5000])),
            MaxBytes = 1000,
            WorkRoot = work,
            LockDownFolder = false,
            RequireAdministrator = false,
            RunInstaller = (_, _) => Exit(0),
        };
        var ex = await Assert.ThrowsAsync<DriverInstallException>(() => installer.InstallAsync());
        Assert.Contains("bigger", ex.Message);
    }

    [Theory]
    [InlineData(3010)]
    [InlineData(1641)]
    public async Task A_restart_request_is_passed_on(int code)
    {
        var result = await Installer((_, _) => Exit(code), installed: () => false).InstallAsync();
        Assert.Equal(DriverInstallResult.NeedsRestart, result);
    }

    [Fact]
    public async Task A_busy_installer_service_is_explained()
    {
        var ex = await Assert.ThrowsAsync<DriverInstallException>(() => Installer((_, _) => Exit(1618)).InstallAsync());
        Assert.Contains("another installation", ex.Message);
    }

    [Fact]
    public async Task Cancelled_install_is_not_a_failure()
    {
        var ex = await Assert.ThrowsAsync<DriverInstallException>(() => Installer((_, _) => Exit(1602)).InstallAsync());
        Assert.Contains("cancelled", ex.Message);
    }

    [Fact]
    public async Task Other_failures_give_the_error_number_and_keep_the_log()
    {
        string kept = Path.Combine(Path.GetTempPath(), "freeXplorer-winfsp-install.log");
        try
        {
            var ex = await Assert.ThrowsAsync<DriverInstallException>(() =>
                Installer((msi, log) =>
                {
                    File.WriteAllText(log, "something went wrong");
                    return Exit(1603);
                }).InstallAsync());

            Assert.Contains("1603", ex.Message);
            Assert.Contains(kept, ex.Message);
            Assert.Equal("something went wrong", File.ReadAllText(kept));
            Assert.Empty(Directory.GetFileSystemEntries(work));
        }
        finally
        {
            File.Delete(kept);
        }
    }

    [Fact]
    public async Task Installer_without_a_driver_is_an_error()
    {
        var ex = await Assert.ThrowsAsync<DriverInstallException>(() =>
            Installer((_, _) => Exit(0), installed: () => false).InstallAsync());
        Assert.Contains("still doesn't show up", ex.Message);
    }

    [Fact]
    public async Task Installing_needs_administrator_rights()
    {
        if (PhysicalDisks.IsAdministrator())
            return;   // nothing to refuse
        var installer = new WinFspInstaller { WorkRoot = work, Handler = new Stub(() => Ok(package)) };
        var ex = await Assert.ThrowsAsync<DriverInstallException>(() => installer.InstallAsync());
        Assert.Contains("administrator", ex.Message);
    }

    [Fact]
    public async Task Installer_folder_is_admin_only()
    {
        if (!PhysicalDisks.IsAdministrator())
            return;   // locking a folder like this would lock a normal user out of their own cleanup

        DirectorySecurity? seen = null;
        await new WinFspInstaller
        {
            Url = new Uri("https://downloads.example/winfsp.msi"),
            Sha256 = Convert.ToHexString(SHA256.HashData(package)),
            Handler = new Stub(() => Ok(package)),
            WorkRoot = work,
            IsInstalled = () => true,
            RunInstaller = (msi, _) =>
            {
                seen = new DirectoryInfo(Path.GetDirectoryName(msi)!).GetAccessControl();
                return Exit(0);
            },
        }.InstallAsync();

        Assert.NotNull(seen);
        Assert.True(seen!.AreAccessRulesProtected);
        var who = seen.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference).Value)
            .Distinct()
            .OrderBy(v => v)
            .ToArray();
        Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544" }, who);
    }

    [Fact]
    public async Task Windows_installer_reports_a_package_it_cant_open()
    {
        int code = await WinFspInstaller.RunMsiexec(Path.Combine(work, "not-there.msi"), Path.Combine(work, "log.txt"));
        Assert.Contains(code, new[] { 1619, 1620 });
        Assert.Contains("open", WinFspInstaller.Problem(code));
    }

    [Fact]
    public void The_pinned_download_is_the_one_checked_by_hand()
    {
        // winget's manifest and GitHub's release page both give this checksum for this file
        Assert.Equal("073a70e00f77423e34bed98b86e600def93393ba5822204fac57a29324db9f7a", WinFspInstaller.PackageSha256);
        Assert.Equal("https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi", WinFspInstaller.PackageUrl);
        Assert.EndsWith(WinFspInstaller.Version + ".msi", WinFspInstaller.PackageUrl);
    }
}

// Puts a practice volume on the real WinFsp driver for a moment. It adds a drive to This PC while it runs,
// so it only does anything on request: set FREEXPLORER_REAL_MOUNT=1 on a PC that has WinFsp.
public class RealMountTests
{
    readonly Xunit.Abstractions.ITestOutputHelper output;

    public RealMountTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

    static bool Requested => Environment.GetEnvironmentVariable("FREEXPLORER_REAL_MOUNT") == "1";

    bool Wanted
    {
        get
        {
            if (Requested && !WinFspMount.IsInstalled)
                throw new InvalidOperationException("FREEXPLORER_REAL_MOUNT is set but WinFsp isn't installed");
            return Requested;
        }
    }

    [Fact]
    public void A_volume_becomes_a_drive_and_goes_away_again()
    {
        if (!Wanted)
            return;

        var device = new MemoryBlockDevice(64L << 20);
        FatxFormatter.Format(device, 0, device.Length, new FatxFormatOptions { Label = "Practice" });
        using var volume = FatxVolume.Open(device, writable: true);
        volume.Write(volume.CreateFile(volume.Root, "hello.txt"), 0, "hi there"u8.ToArray());
        volume.Flush();

        string root;
        using (var mounted = WinFspMount.Mount(volume, "Practice"))
        {
            Assert.Matches("^[A-Z]:$", mounted.MountPoint);
            root = mounted.MountPoint + "\\";
            Assert.True(Directory.Exists(root));

            var drive = new DriveInfo(mounted.MountPoint);
            output.WriteLine($"mounted at {mounted.MountPoint}: {drive.DriveType}, {drive.DriveFormat}, '{drive.VolumeLabel}'");
            Assert.Equal("FATX", drive.DriveFormat);
            Assert.Equal("Practice", drive.VolumeLabel);
            Assert.Equal("hi there", File.ReadAllText(root + "hello.txt"));
            Assert.Contains(mounted.MountPoint[0], Directory.GetLogicalDrives().Select(d => d[0]));
        }
        Assert.False(Directory.Exists(root));
        output.WriteLine($"{root} is gone again");
    }

    [Fact]
    public void Two_volumes_get_two_different_letters()
    {
        if (!Wanted)
            return;

        var first = new MemoryBlockDevice(64L << 20);
        var second = new MemoryBlockDevice(64L << 20);
        FatxFormatter.Format(first, 0, first.Length);
        FatxFormatter.Format(second, 0, second.Length);
        using var a = FatxVolume.Open(first, writable: true);
        using var b = FatxVolume.Open(second, writable: true);

        using var one = WinFspMount.Mount(a, "One");
        using var two = WinFspMount.Mount(b, "Two");
        output.WriteLine($"mounted at {one.MountPoint} and {two.MountPoint}");
        Assert.NotEqual(one.MountPoint, two.MountPoint);
        Assert.True(Directory.Exists(one.MountPoint + "\\"));
        Assert.True(Directory.Exists(two.MountPoint + "\\"));
    }

    [Fact]
    public void A_letter_that_is_taken_is_refused_by_name()
    {
        if (!Wanted)
            return;

        var first = new MemoryBlockDevice(64L << 20);
        var second = new MemoryBlockDevice(64L << 20);
        FatxFormatter.Format(first, 0, first.Length);
        FatxFormatter.Format(second, 0, second.Length);
        using var a = FatxVolume.Open(first, writable: true);
        using var b = FatxVolume.Open(second, writable: true);

        using var one = WinFspMount.Mount(a, "One");
        var ex = Assert.Throws<MountException>(() => WinFspMount.Mount(b, "Two", one.MountPoint));
        output.WriteLine($"asking for {one.MountPoint} again: {ex.Message}");
        Assert.Contains(one.MountPoint, ex.Message);
    }
}
