namespace FreeXplorer.Fatx;

// One file or directory. The same object is handed out for the lifetime of the volume,
// so renames and deletes show up on handles that are still open.
public sealed class FatxNode
{
    static long lastId;

    readonly FatxFlavor flavor;

    internal FatxNode(FatxFlavor flavor, string name, FatxAttributes attributes)
    {
        this.flavor = flavor;
        Name = name;
        Attributes = attributes;
    }

    // stays the same for as long as the volume is open, whatever happens to the name
    public long Id { get; } = Interlocked.Increment(ref lastId);

    public string Name { get; internal set; }
    public FatxAttributes Attributes { get; internal set; }
    public uint FirstCluster { get; internal set; }
    public long Size { get; internal set; }
    public FatxNode? Parent { get; internal set; }
    public bool IsDeleted { get; internal set; }

    public bool IsDirectory => (Attributes & FatxAttributes.Directory) != 0;
    public bool IsRoot => Parent is null;

    internal uint CreatedRaw { get; set; }
    internal uint ModifiedRaw { get; set; }
    internal uint AccessedRaw { get; set; }

    internal int Slot = -1;
    internal Listing? Children;
    internal ClusterMap? Map;

    public DateTime Created => FatxTime.Decode(CreatedRaw, flavor);
    public DateTime Modified => FatxTime.Decode(ModifiedRaw, flavor);
    public DateTime Accessed => FatxTime.Decode(AccessedRaw, flavor);

    public string Path
    {
        get
        {
            if (Parent is null)
                return "\\";
            var parts = new Stack<string>();
            for (var n = this; n.Parent is not null; n = n.Parent)
                parts.Push(n.Name);
            return "\\" + string.Join('\\', parts);
        }
    }

    public override string ToString() => Path;
}

// What a directory looks like in memory: its clusters, and its 64 byte slots in order.
// A null slot is a deleted entry that can be reused.
sealed class Listing
{
    public readonly List<uint> Clusters = new();
    public readonly List<FatxNode?> Slots = new();
    public readonly Dictionary<string, FatxNode> ByName = new(StringComparer.OrdinalIgnoreCase);
}
