namespace FreeXplorer.Fatx;

public static class FatxName
{
    public const int MaxLength = FatxGeometry.MaxFileNameLength;
    public const int MaxPathLength = 240;

    const string Punctuation = " !#$%&'()-.@[]^_`{}~";

    public static bool IsValidChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || Punctuation.Contains(c);

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength)
            return false;
        if (name is "." or "..")
            return false;
        foreach (char c in name)
            if (!IsValidChar(c))
                return false;
        return true;
    }

    public static void Require(string name)
    {
        if (name.Length > MaxLength)
            throw new FatxException(FatxError.InvalidName, $"\"{name}\" is longer than {MaxLength} characters");
        if (!IsValid(name))
            throw new FatxException(FatxError.InvalidName, $"\"{name}\" has characters FATX can't store");
    }

    public static string[] SplitPath(string path) =>
        path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
}
