using System.IO;
using emMDee.ViewModels;

namespace emMDee.Models;

/// <summary>
/// How an open file compares to its copy on disk.
/// </summary>
public enum ExternalChangeState
{
    None,
    Modified,
    Deleted
}

/// <summary>
/// Represents an open tab with its associated markdown file.
/// </summary>
public class TabItem : ObservableObject
{
    public string FilePath { get; set; } = string.Empty;

    private string _title = "Untitled";
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string MarkdownContent { get; set; } = string.Empty;
    public bool IsModified { get; set; }
    public double ScrollPosition { get; set; }

    // --- External-change tracking ---------------------------------------

    /// <summary>Last-write timestamp captured the last time we read the file.</summary>
    public DateTime LastWriteTimeUtc { get; set; }

    /// <summary>File size (bytes) captured the last time we read the file.</summary>
    public long FileSize { get; set; }

    private ExternalChangeState _externalState = ExternalChangeState.None;
    public ExternalChangeState ExternalState
    {
        get => _externalState;
        set
        {
            if (SetProperty(ref _externalState, value))
                OnPropertyChanged(nameof(HasExternalChange));
        }
    }

    /// <summary>True when the file differs from disk (changed or deleted) — drives the tab marker.</summary>
    public bool HasExternalChange => _externalState != ExternalChangeState.None;

    /// <summary>
    /// Records the current on-disk identity (mtime + size) so later changes can be detected.
    /// No-op if the file can't be read.
    /// </summary>
    public void SnapshotFileInfo()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Exists)
            {
                LastWriteTimeUtc = fi.LastWriteTimeUtc;
                FileSize = fi.Length;
            }
        }
        catch
        {
            // Treat unreadable as "no snapshot" — a later existence check decides the state.
        }
    }

    /// <summary>
    /// Compares the file on disk against the last snapshot.
    /// Returns true when disk matches the snapshot (no change). <paramref name="exists"/>
    /// reports whether the file is still present.
    /// </summary>
    public bool MatchesDisk(out bool exists)
    {
        try
        {
            var fi = new FileInfo(FilePath);
            exists = fi.Exists;
            if (!exists)
                return false;
            return fi.LastWriteTimeUtc == LastWriteTimeUtc && fi.Length == FileSize;
        }
        catch
        {
            // Transient IO error (e.g. another app mid-write): report "still present
            // but not matching" so we don't spuriously flag a delete.
            exists = true;
            return false;
        }
    }

    public static TabItem FromFile(string filePath, string? content = null)
    {
        var tab = new TabItem
        {
            FilePath = filePath,
            Title = System.IO.Path.GetFileName(filePath),
            MarkdownContent = content ?? string.Empty
        };
        tab.SnapshotFileInfo();
        return tab;
    }
}
