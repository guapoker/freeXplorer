using FreeXplorer.Fatx;

namespace FreeXplorer.Tests;

// A plain dictionary of what the volume should contain, and a way to make random changes
// to both the volume and the dictionary at the same time.
sealed class FuzzModel
{
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase) { "" };
}

static class FuzzDriver
{
    static readonly string[] Names =
    {
        "a", "b", "c", "Content", "save.dat", "Game 1", "x.bin", "y.bin", "0000000000000000", "Headers", "TITLE", "t",
    };

    static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "\\" + name;
    static string ParentOf(string path) => path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";
    static string NameOf(string path) => path.Contains('\\') ? path[(path.LastIndexOf('\\') + 1)..] : path;

    // allowMoves=false keeps renames inside one directory
    public static void Step(FatxVolume vol, FuzzModel m, Random rng, bool allowMoves = true, int maxSize = 1_500_000)
    {
        try
        {
            Apply(vol, m, rng, allowMoves, maxSize);
        }
        catch (FatxException ex) when (ex.Error is FatxError.DiskFull or FatxError.PathTooLong)
        {
            // the model stays as it was, the volume has to be unchanged too
        }
    }

    static void Apply(FatxVolume vol, FuzzModel m, Random rng, bool allowMoves, int maxSize)
    {
        int roll = rng.Next(100);
        var dirs = m.Dirs.ToList();
        var files = m.Files.Keys.ToList();

        if (roll < 22)
        {
            string dir = dirs[rng.Next(dirs.Count)];
            string path = Join(dir, Names[rng.Next(Names.Length)]);
            if (m.Files.ContainsKey(path) || m.Dirs.Contains(path))
                return;
            var node = vol.CreateFile(vol.Resolve(dir), NameOf(path));
            m.Files[path] = Array.Empty<byte>();
            var data = RandomContent(rng, maxSize);
            vol.Write(node, 0, data);
            m.Files[path] = data;
        }
        else if (roll < 36 && files.Count > 0)
        {
            string path = files[rng.Next(files.Count)];
            var add = RandomContent(rng, Math.Min(100_000, maxSize));
            var node = vol.Resolve(path);
            vol.Write(node, node.Size, add);
            m.Files[path] = m.Files[path].Concat(add).ToArray();
        }
        else if (roll < 48 && files.Count > 0)
        {
            string path = files[rng.Next(files.Count)];
            var old = m.Files[path];
            long offset = rng.Next(0, old.Length + 5000);
            var patch = RandomContent(rng, Math.Min(30_000, maxSize));
            if (patch.Length == 0)
                return;
            vol.Write(vol.Resolve(path), offset, patch);
            var next = new byte[Math.Max(old.Length, offset + patch.Length)];
            old.CopyTo(next, 0);
            patch.CopyTo(next, offset);
            m.Files[path] = next;
        }
        else if (roll < 58 && files.Count > 0)
        {
            string path = files[rng.Next(files.Count)];
            var old = m.Files[path];
            int length = rng.Next(0, old.Length + 20_000);
            vol.SetLength(vol.Resolve(path), length);
            var next = new byte[length];
            Array.Copy(old, next, Math.Min(old.Length, length));
            m.Files[path] = next;
        }
        else if (roll < 70 && files.Count > 0)
        {
            string path = files[rng.Next(files.Count)];
            vol.Delete(vol.Resolve(path));
            m.Files.Remove(path);
        }
        else if (roll < 77)
        {
            string dir = dirs[rng.Next(dirs.Count)];
            string path = Join(dir, Names[rng.Next(Names.Length)]);
            if (m.Files.ContainsKey(path) || m.Dirs.Contains(path))
                return;
            vol.CreateDirectory(vol.Resolve(dir), NameOf(path));
            m.Dirs.Add(path);
        }
        else if (roll < 82 && dirs.Count > 1)
        {
            string path = dirs[rng.Next(1, dirs.Count)];
            bool empty = !m.Files.Keys.Concat(m.Dirs).Any(p => p != path && ParentOf(p).Equals(path, StringComparison.OrdinalIgnoreCase));
            if (!empty)
            {
                Assert.Throws<FatxException>(() => vol.Delete(vol.Resolve(path)));
                return;
            }
            vol.Delete(vol.Resolve(path));
            m.Dirs.Remove(path);
        }
        else if (roll < 93 && files.Count > 0)
        {
            string from = files[rng.Next(files.Count)];
            string toDir = allowMoves ? dirs[rng.Next(dirs.Count)] : ParentOf(from);
            string to = Join(toDir, Names[rng.Next(Names.Length)]);
            if (m.Dirs.Contains(to))
                return;
            bool exists = m.Files.ContainsKey(to) && !to.Equals(from, StringComparison.OrdinalIgnoreCase);
            bool replace = rng.Next(2) == 0;
            var node = vol.Resolve(from);
            if (exists && !replace)
            {
                Assert.Throws<FatxException>(() => vol.Rename(node, vol.Resolve(toDir), NameOf(to), false));
                return;
            }
            vol.Rename(node, vol.Resolve(toDir), NameOf(to), replace);
            var data = m.Files[from];
            m.Files.Remove(from);
            m.Files.Remove(to);
            m.Files[to] = data;
        }
        else if (dirs.Count > 1)
        {
            string from = dirs[rng.Next(1, dirs.Count)];
            string toDir = allowMoves ? dirs[rng.Next(dirs.Count)] : ParentOf(from);
            string to = Join(toDir, Names[rng.Next(Names.Length)]);
            bool intoSelf = toDir.Equals(from, StringComparison.OrdinalIgnoreCase)
                || toDir.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase);
            if (m.Files.ContainsKey(to) || m.Dirs.Contains(to) || intoSelf)
            {
                if (intoSelf && !m.Files.ContainsKey(to) && !m.Dirs.Contains(to))
                    Assert.Throws<FatxException>(() => vol.Rename(vol.Resolve(from), vol.Resolve(toDir), NameOf(to)));
                return;
            }
            vol.Rename(vol.Resolve(from), vol.Resolve(toDir), NameOf(to));

            string prefix = from + "\\";
            foreach (var path in m.Files.Keys.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var data = m.Files[path];
                m.Files.Remove(path);
                m.Files[to + path[from.Length..]] = data;
            }
            foreach (var path in m.Dirs.Where(p => p.Equals(from, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                m.Dirs.Remove(path);
                m.Dirs.Add(to + path[from.Length..]);
            }
        }
    }

    static byte[] RandomContent(Random rng, int max)
    {
        int kind = rng.Next(100);
        int length = kind switch
        {
            < 40 => rng.Next(0, 1000),
            < 80 => rng.Next(0, 65_000),
            < 95 => rng.Next(0, 300_000),
            _ => rng.Next(0, 1_500_000),
        };
        length = Math.Min(length, max);
        var data = new byte[length];
        rng.NextBytes(data);
        return data;
    }

    public static void Compare(FatxVolume vol, FuzzModel m, string where)
    {
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        var pending = new Stack<(FatxNode Node, string Path)>();
        pending.Push((vol.Root, ""));
        while (pending.Count > 0)
        {
            var (dir, path) = pending.Pop();
            foreach (var child in vol.GetChildren(dir))
            {
                string childPath = Join(path, child.Name);
                if (child.IsDirectory)
                {
                    seenDirs.Add(childPath);
                    pending.Push((child, childPath));
                }
                else
                {
                    seenFiles.Add(childPath);
                    Assert.True(m.Files.TryGetValue(childPath, out var expected), $"{where}: unexpected file {childPath}");
                    var actual = TestVolumes.ReadAll(vol, child);
                    Assert.True(expected!.AsSpan().SequenceEqual(actual), $"{where}: contents of {childPath} differ ({expected.Length} vs {actual.Length} bytes)");
                }
            }
        }
        Assert.True(seenFiles.SetEquals(m.Files.Keys), $"{where}: file lists differ");
        Assert.True(seenDirs.SetEquals(m.Dirs), $"{where}: directory lists differ");
    }
}
