using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using FreeXplorer.Storage;

namespace FreeXplorer.Mount;

public enum DriverInstallResult
{
    Installed,
    NeedsRestart,
}

public sealed class DriverInstallException : Exception
{
    public DriverInstallException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

// Fetches the WinFsp installer from the project's GitHub release and runs it silently. What gets
// installed has to match the checksum below, so it's always the release this program was tried with.
public sealed class WinFspInstaller
{
    public const string Version = "2.1.25156";
    public const string PackageUrl = "https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi";
    public const string PackageSha256 = "073a70e00f77423e34bed98b86e600def93393ba5822204fac57a29324db9f7a";

    // the knobs are here for the tests, nothing else changes them
    public Uri Url { get; init; } = new(PackageUrl);
    public string Sha256 { get; init; } = PackageSha256;
    public long MaxBytes { get; init; } = 32L << 20;
    public HttpMessageHandler? Handler { get; init; }
    public string? WorkRoot { get; init; }
    public bool LockDownFolder { get; init; } = true;
    public bool RequireAdministrator { get; init; } = true;
    public Func<bool>? IsInstalled { get; init; }
    public Func<string, string, Task<int>>? RunInstaller { get; init; }

    public async Task<DriverInstallResult> InstallAsync(Action<string>? status = null, Action<double>? progress = null,
        CancellationToken cancel = default)
    {
        if (RequireAdministrator && !PhysicalDisks.IsAdministrator())
            throw new DriverInstallException("Installing a driver needs administrator rights. Start freeXplorer as administrator and try again.");

        string folder = MakeWorkFolder();
        try
        {
            string msi = Path.Combine(folder, "winfsp.msi");
            string log = Path.Combine(folder, "install.log");

            status?.Invoke("Downloading WinFsp...");
            await Download(msi, progress, cancel);

            status?.Invoke("Checking the download...");
            Verify(msi);

            status?.Invoke("Installing the driver...");
            int code;
            try
            {
                code = await (RunInstaller ?? RunMsiexec)(msi, log);
            }
            catch (Win32Exception ex)
            {
                throw new DriverInstallException($"Windows wouldn't start the installer ({ex.Message}).", ex);
            }

            if (code is 3010 or 1641)
                return DriverInstallResult.NeedsRestart;
            if (code == 0)
            {
                if (!(IsInstalled ?? (() => WinFspMount.IsInstalled))())
                    throw new DriverInstallException("The installer finished, but WinFsp still doesn't show up. Restart Windows and look again.");
                return DriverInstallResult.Installed;
            }
            throw new DriverInstallException(Problem(code) + KeepLog(log));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    string MakeWorkFolder()
    {
        string root = WorkRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "freeXplorer", "Setup");
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var info = Directory.CreateDirectory(folder);

        // an administrator is about to run what's in here, so nobody else gets to touch it
        if (LockDownFolder)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        return folder;
    }

    async Task Download(string path, Action<double>? progress, CancellationToken cancel)
    {
        using var http = Handler is null ? new HttpClient() : new HttpClient(Handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("freeXplorer");
        try
        {
            using var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            if (total > MaxBytes)
                throw new DriverInstallException("The download is far bigger than the WinFsp installer should be, so it was skipped.");

            await using var input = await response.Content.ReadAsStreamAsync(cancel);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, cancel)) > 0)
            {
                done += n;
                if (done > MaxBytes)
                    throw new DriverInstallException("The download is far bigger than the WinFsp installer should be, so it was stopped.");
                await output.WriteAsync(buffer.AsMemory(0, n), cancel);
                if (total > 0)
                    progress?.Invoke(Math.Min(1.0, (double)done / total.Value));
            }
        }
        catch (HttpRequestException ex)
        {
            throw new DriverInstallException(
                $"Couldn't download WinFsp ({ex.Message}). Check your internet connection, or install it yourself from {WinFspMount.DownloadPage}", ex);
        }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            throw new DriverInstallException($"The download took too long and was given up on. Install WinFsp yourself from {WinFspMount.DownloadPage}", ex);
        }
    }

    void Verify(string path)
    {
        string actual;
        using (var stream = File.OpenRead(path))
            actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new DriverInstallException("The download isn't the file this version of freeXplorer expects (its checksum is different), so it was thrown away and nothing was installed.");
    }

    internal static async Task<int> RunMsiexec(string msiPath, string logPath)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "/i", msiPath, "/qn", "/norestart", "/l*v", logPath })
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info) ?? throw new DriverInstallException("Windows wouldn't start the installer.");
        // no cancelling from here on: stopping an installer half way is worse than waiting for it
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    internal static string Problem(int code) => code switch
    {
        1602 => "The installation was cancelled.",
        1618 => "Windows is busy with another installation. Wait for it to finish, then try again.",
        1619 or 1620 => "Windows couldn't open the downloaded installer.",
        1625 => "A policy on this PC doesn't allow installing this.",
        1638 => "Windows says a version of WinFsp is already installed. If mounting still doesn't work, remove WinFsp in Settings > Apps and try again.",
        _ => $"The installer failed (error {code}).",
    };

    // leaves the installer's log next to the user's other temp files, so there is something to read or send along
    static string KeepLog(string log)
    {
        try
        {
            if (!File.Exists(log))
                return "";
            string kept = Path.Combine(Path.GetTempPath(), "freeXplorer-winfsp-install.log");
            File.Copy(log, kept, true);
            return $" The details are in {kept}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
