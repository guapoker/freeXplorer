namespace FreeXplorer.Fatx;

public enum FsckSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record FsckIssue(FsckSeverity Severity, string Message);

public sealed class FsckReport
{
    readonly List<FsckIssue> issues = new();

    public IReadOnlyList<FsckIssue> Issues => issues;
    public long Directories { get; internal set; }
    public long Files { get; internal set; }
    public long FileBytes { get; internal set; }
    public long UsedClusters { get; internal set; }
    public long OrphanClusters { get; internal set; }
    public long FixedClusters { get; internal set; }

    public int Errors => issues.Count(i => i.Severity == FsckSeverity.Error);
    public int Warnings => issues.Count(i => i.Severity == FsckSeverity.Warning);
    public bool IsClean => Errors == 0 && Warnings == 0;

    internal void Add(FsckSeverity severity, string message)
    {
        // a damaged volume can produce millions of these, keep the report readable
        if (issues.Count < 500)
            issues.Add(new FsckIssue(severity, message));
        else if (issues.Count == 500)
            issues.Add(new FsckIssue(FsckSeverity.Info, "more problems found, not listing them all"));
    }
}
