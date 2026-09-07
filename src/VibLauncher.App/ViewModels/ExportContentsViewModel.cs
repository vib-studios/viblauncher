using System.Collections.ObjectModel;
using VibLauncher.App.Mvvm;
using VibLauncher.Core.Downloads;
using VibLauncher.Core.Instances;

namespace VibLauncher.App.ViewModels;

/// <summary>
/// One row in the export picker: a file or folder, and whether it goes in.
/// </summary>
/// <remarks>
/// Ticks travel in both directions. Turning a folder off turns everything under
/// it off, and turning a file off leaves its folder half-ticked rather than
/// silently on, so the tree always shows the truth about what the archive will
/// contain without the user having to open every branch to check.
/// </remarks>
public sealed class ExportEntryViewModel : ObservableObject
{
    private readonly ExportEntryViewModel? _parent;
    private bool? _isIncluded = true;
    private bool _isExpanded;

    public ExportEntryViewModel(InstanceContentNode node, ExportEntryViewModel? parent = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        _parent = parent;

        Name = node.Name;
        RelativePath = node.RelativePath;
        IsDirectory = node.IsDirectory;
        SizeBytes = node.SizeBytes;
        FileCount = node.FileCount;

        Children = [.. node.Children.Select(child => new ExportEntryViewModel(child, this))];

        // The top level opens so the picker shows mods, config and saves without
        // a click. Everything below stays shut: a modded config folder is
        // hundreds of rows and nobody wants to scroll past it to reach saves.
        _isExpanded = parent is null;
    }

    public string Name { get; }

    /// <summary>The path under the game directory, which is what an exclusion records.</summary>
    public string RelativePath { get; }

    public bool IsDirectory { get; }

    public long SizeBytes { get; }

    public int FileCount { get; }

    public IReadOnlyList<ExportEntryViewModel> Children { get; }

    /// <summary>The size, and for a folder the number of files, as the row shows it.</summary>
    public string Detail =>
        IsDirectory
            ? $"{FileCount} {(FileCount == 1 ? "file" : "files")}, {DownloadItem.Format(SizeBytes)}"
            : DownloadItem.Format(SizeBytes);

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// True when everything here goes in, false when nothing does, <c>null</c>
    /// for a folder with some of each.
    /// </summary>
    public bool? IsIncluded
    {
        get => _isIncluded;
        set
        {
            // A three-state box would cycle through the indeterminate state on
            // click, which is not a thing anyone means to choose. The view binds
            // a two-state box, so a click here is always a definite yes or no.
            var resolved = value ?? true;

            if (_isIncluded == resolved)
            {
                return;
            }

            SetTo(resolved);
            _parent?.RefreshFromChildren();
            Changed?.Invoke();
        }
    }

    /// <summary>Raised on any tick, so the dialog can recount what is selected.</summary>
    public event Action? Changed;

    /// <summary>Applies a tick to this row and everything under it.</summary>
    public void SetTo(bool included)
    {
        if (_isIncluded != included)
        {
            _isIncluded = included;
            OnPropertyChanged(nameof(IsIncluded));
        }

        foreach (var child in Children)
        {
            child.SetTo(included);
        }
    }

    /// <summary>Adds the paths this row and its children exclude to <paramref name="excluded"/>.</summary>
    /// <remarks>
    /// A folder that is off contributes its own path and stops, because
    /// excluding a folder excludes what is under it. Only a half-ticked folder
    /// is worth walking into.
    /// </remarks>
    public void CollectExclusions(ICollection<string> excluded)
    {
        ArgumentNullException.ThrowIfNull(excluded);

        switch (_isIncluded)
        {
            case false:
                excluded.Add(RelativePath);
                return;

            case true:
                return;

            default:
                foreach (var child in Children)
                {
                    child.CollectExclusions(excluded);
                }

                return;
        }
    }

    /// <summary>Adds up what is actually going into the archive.</summary>
    public (int Files, long Bytes) Selected()
    {
        if (_isIncluded == true)
        {
            return (FileCount, SizeBytes);
        }

        if (_isIncluded == false)
        {
            return (0, 0);
        }

        var files = 0;
        long bytes = 0;

        foreach (var child in Children)
        {
            var (childFiles, childBytes) = child.Selected();
            files += childFiles;
            bytes += childBytes;
        }

        return (files, bytes);
    }

    /// <summary>Recomputes this row's tick from its children, then tells its own parent.</summary>
    private void RefreshFromChildren()
    {
        if (Children.Count == 0)
        {
            return;
        }

        var all = Children.All(c => c._isIncluded == true);
        var none = Children.All(c => c._isIncluded == false);

        var resolved = all ? true : none ? (bool?)false : null;
        if (_isIncluded == resolved)
        {
            return;
        }

        _isIncluded = resolved;
        OnPropertyChanged(nameof(IsIncluded));
        _parent?.RefreshFromChildren();
    }
}

/// <summary>
/// The export picker's model: the tree, and a running total of what it selects.
/// </summary>
public sealed class ExportContentsViewModel : ObservableObject
{
    private int _selectedFiles;
    private long _selectedBytes;

    public ExportContentsViewModel(IReadOnlyList<InstanceContentNode> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        Entries = [.. contents.Select(node => new ExportEntryViewModel(node))];

        foreach (var entry in Flatten(Entries))
        {
            entry.Changed += Recount;
        }

        Recount();
    }

    public ObservableCollection<ExportEntryViewModel> Entries { get; }

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>What the footer says: how much of the instance the archive will hold.</summary>
    public string Summary =>
        IsEmpty
            ? "This instance has nothing an export would carry yet."
            : $"{_selectedFiles} {(_selectedFiles == 1 ? "file" : "files")} selected, {DownloadItem.Format(_selectedBytes)}";

    /// <summary>
    /// An empty instance is still exportable: the archive carries its settings.
    /// Deselecting everything in a non-empty one is not, because that is the
    /// same archive with the work of picking it thrown away.
    /// </summary>
    public bool CanExport => _selectedFiles > 0 || IsEmpty;

    public void SelectAll() => SetAll(included: true);

    public void SelectNone() => SetAll(included: false);

    /// <summary>The options the picker produces: everything the user turned off.</summary>
    public InstanceExportOptions ToOptions()
    {
        var excluded = new List<string>();

        foreach (var entry in Entries)
        {
            entry.CollectExclusions(excluded);
        }

        return new InstanceExportOptions(excluded);
    }

    private static IEnumerable<ExportEntryViewModel> Flatten(IEnumerable<ExportEntryViewModel> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;

            foreach (var child in Flatten(entry.Children))
            {
                yield return child;
            }
        }
    }

    private void SetAll(bool included)
    {
        foreach (var entry in Entries)
        {
            entry.SetTo(included);
        }

        Recount();
    }

    private void Recount()
    {
        _selectedFiles = 0;
        _selectedBytes = 0;

        foreach (var entry in Entries)
        {
            var (files, bytes) = entry.Selected();
            _selectedFiles += files;
            _selectedBytes += bytes;
        }

        OnPropertiesChanged(nameof(Summary), nameof(CanExport));
    }
}
