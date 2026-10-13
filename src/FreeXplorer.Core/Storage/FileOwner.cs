namespace FreeXplorer.Storage;

// Run under sudo, every file this program creates would belong to root. This hands new files
// and folders to the user who ran sudo instead, so they can move or delete them afterwards.
public static class FileOwner
{
    public static void GiveToSudoUser(string path)
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess)
            return;
        if (!uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), out uint uid)
            || !uint.TryParse(Environment.GetEnvironmentVariable("SUDO_GID"), out uint gid)
            || uid == 0)
            return;
        Libc.LChown(path, uid, gid);   // best effort, the file is fine either way
    }
}
