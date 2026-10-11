namespace FreeXplorer.Cli;

// Tiny argument parser: positional words, --name value, --flag, --name=value, and a few -x shortcuts
sealed class ParsedArgs
{
    static readonly Dictionary<string, string> Shortcuts = new()
    {
        ["l"] = "long",
        ["r"] = "recursive",
        ["f"] = "force",
        ["y"] = "yes",
        ["w"] = "write",
    };

    readonly Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = new();

    public static ParsedArgs Parse(IEnumerable<string> args, IReadOnlySet<string> optionsWithValues)
    {
        var parsed = new ParsedArgs();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            if (a == "--")
            {
                parsed.Positional.AddRange(list.Skip(i + 1));
                break;
            }
            if (a.StartsWith("--") && a.Length > 2)
            {
                string body = a[2..];
                int eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    parsed.options[body[..eq]] = body[(eq + 1)..];
                }
                else if (optionsWithValues.Contains(body))
                {
                    if (i + 1 >= list.Count)
                        throw new UsageException($"--{body} needs a value");
                    parsed.options[body] = list[++i];
                }
                else
                {
                    parsed.options[body] = null;
                }
            }
            else if (a.Length > 1 && a[0] == '-' && !char.IsDigit(a[1]))
            {
                foreach (char c in a[1..])
                {
                    if (!Shortcuts.TryGetValue(c.ToString(), out var name))
                        throw new UsageException($"unknown option -{c}");
                    parsed.options[name] = null;
                }
            }
            else
            {
                parsed.Positional.Add(a);
            }
        }
        return parsed;
    }

    public bool Has(string name) => options.ContainsKey(name);
    public string? Get(string name) => options.GetValueOrDefault(name);

    public string Require(int index, string what) =>
        index < Positional.Count ? Positional[index] : throw new UsageException($"missing {what}");

    public string? Optional(int index) => index < Positional.Count ? Positional[index] : null;

    public void RejectUnknown(params string[] known)
    {
        foreach (var key in options.Keys)
            if (!known.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new UsageException($"unknown option --{key} for this command");
    }
}

sealed class UsageException : Exception
{
    public UsageException(string message) : base(message)
    {
    }
}
