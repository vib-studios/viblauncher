namespace VibLauncher.Core.Instances;

/// <summary>
/// One file or folder an export would carry.
/// </summary>
/// <remarks>
/// Paths are relative to the instance's game directory and use forward slashes,
/// which is also how they are written into the archive. The picker, the archive
/// and <see cref="InstanceExportOptions"/> therefore all talk about the same
/// strings, with no conversion in between to get wrong.
/// </remarks>
public sealed class InstanceContentNode
{
    private readonly List<InstanceContentNode> _children = [];

    internal InstanceContentNode(string relativePath, string name, bool isDirectory)
    {
        RelativePath = relativePath;
        Name = name;
        IsDirectory = isDirectory;
    }

    /// <summary>The path under the game directory, for example <c>config/sodium.json</c>.</summary>
    public string RelativePath { get; }

    /// <summary>The last segment of <see cref="RelativePath"/>, which is what the picker shows.</summary>
    public string Name { get; }

    public bool IsDirectory { get; }

    /// <summary>The file's size, or the total of everything under this folder.</summary>
    public long SizeBytes { get; internal set; }

    /// <summary>How many files this node stands for. One for a file.</summary>
    public int FileCount { get; internal set; }

    /// <summary>Folders first, then files, each in name order.</summary>
    public IReadOnlyList<InstanceContentNode> Children => _children;

    internal void Add(InstanceContentNode child) => _children.Add(child);

    /// <summary>
    /// Rolls sizes and counts up from the leaves and puts every level in order.
    /// </summary>
    /// <remarks>
    /// Done in one pass after the tree is built rather than while filling it,
    /// because a folder's total is only known once the last file under it has
    /// been seen, and files arrive in whatever order the file system lists them.
    /// </remarks>
    internal void Settle()
    {
        if (!IsDirectory)
        {
            FileCount = 1;
            return;
        }

        long bytes = 0;
        var files = 0;

        foreach (var child in _children)
        {
            child.Settle();
            bytes += child.SizeBytes;
            files += child.FileCount;
        }

        SizeBytes = bytes;
        FileCount = files;

        _children.Sort(Compare);
    }

    private static int Compare(InstanceContentNode a, InstanceContentNode b) =>
        a.IsDirectory == b.IsDirectory
            ? string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase)
            : a.IsDirectory ? -1 : 1;
}

/// <summary>
/// What an export leaves out on top of the files it never carries anyway.
/// </summary>
/// <remarks>
/// Excluding a folder excludes everything under it, so the picker only has to
/// record the highest node the user turned off rather than every file beneath
/// it. That keeps the option object small even for an instance with a world in
/// it, and it survives a file being added to a deselected folder afterwards.
/// </remarks>
public sealed class InstanceExportOptions
{
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    public InstanceExportOptions(IEnumerable<string>? excludedPaths = null)
    {
        foreach (var path in excludedPaths ?? [])
        {
            var normalised = Normalise(path);
            if (normalised.Length > 0)
            {
                _excluded.Add(normalised);
            }
        }
    }

    /// <summary>Everything the export would normally carry. The default when no picker was shown.</summary>
    public static InstanceExportOptions Everything { get; } = new();

    /// <summary>The paths the user turned off, relative to the game directory.</summary>
    public IReadOnlyCollection<string> ExcludedPaths => _excluded;

    /// <summary>True when this path, or any folder above it, was turned off.</summary>
    public bool Excludes(string relativePath)
    {
        if (_excluded.Count == 0 || string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        var path = Normalise(relativePath);

        // Walked upwards rather than matched by prefix: a prefix test would also
        // match "configuration" against an excluded "config".
        while (path.Length > 0)
        {
            if (_excluded.Contains(path))
            {
                return true;
            }

            var separator = path.LastIndexOf('/');
            if (separator <= 0)
            {
                return false;
            }

            path = path[..separator];
        }

        return false;
    }

    private static string Normalise(string path) => path.Replace('\\', '/').Trim('/');
}
