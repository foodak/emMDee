using System.IO;
using System.Threading;

namespace emMDee.Services;

/// <summary>
/// Watches the files backing open tabs for external changes (edits, moves, deletes)
/// without holding any lock on them.
///
/// Design notes:
///  - One <see cref="FileSystemWatcher"/> per <em>directory</em>, shared by every watched
///    file in that directory and reference-counted, so opening ten files from one folder
///    costs one watcher, not ten.
///  - Raw OS events are noisy: a single save can raise 2-3 Changed events, and atomic
///    saves arrive as Deleted+Created or Renamed. Every raw event for a watched path is
///    funnelled through a per-path debounce timer; only after it goes quiet for
///    <see cref="DebounceMs"/> do we raise <see cref="FileEventDetected"/> exactly once.
///  - The watcher fires on thread-pool threads. The final notification is marshalled back
///    onto the UI <see cref="SynchronizationContext"/> captured at construction, so
///    subscribers can touch the view model directly.
///  - This service never decides "changed vs deleted" — it only says "re-evaluate this
///    path". The caller compares against its own snapshot, keeping the policy in one place.
/// </summary>
public sealed class FileWatchService : IDisposable
{
    private const int DebounceMs = 300;

    private sealed class DirectoryEntry
    {
        public required FileSystemWatcher Watcher { get; init; }
        // filename (case-insensitive) -> number of open tabs referencing it
        public Dictionary<string, int> FileRefCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, DirectoryEntry> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizationContext _uiContext;
    private bool _disposed;

    /// <summary>
    /// Raised (on the UI thread) when a watched file may have changed on disk.
    /// The argument is the full path; the subscriber decides what actually changed.
    /// </summary>
    public event Action<string>? FileEventDetected;

    public FileWatchService()
    {
        // Constructed on the UI thread; capture its context for marshalling callbacks back.
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    /// <summary>Begin watching a file. Safe to call repeatedly for the same path (ref-counted).</summary>
    public void Watch(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return;

        string dir;
        string file;
        try
        {
            dir = Path.GetDirectoryName(filePath) ?? string.Empty;
            file = Path.GetFileName(filePath);
        }
        catch
        {
            return;
        }
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
            return;

        lock (_lock)
        {
            if (_disposed)
                return;

            if (!_directories.TryGetValue(dir, out var entry))
            {
                FileSystemWatcher watcher;
                try
                {
                    watcher = new FileSystemWatcher(dir)
                    {
                        NotifyFilter = NotifyFilters.FileName
                                       | NotifyFilters.LastWrite
                                       | NotifyFilters.Size
                                       | NotifyFilters.CreationTime,
                        IncludeSubdirectories = false
                    };
                }
                catch
                {
                    // Directory gone / inaccessible (e.g. unplugged drive). Skip silently;
                    // the file simply won't be live-watched.
                    return;
                }

                watcher.Changed += OnRawChange;
                watcher.Created += OnRawChange;
                watcher.Deleted += OnRawChange;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnWatcherError;

                entry = new DirectoryEntry { Watcher = watcher };
                _directories[dir] = entry;

                try { watcher.EnableRaisingEvents = true; }
                catch { /* leave registered but inert; events just won't fire */ }
            }

            entry.FileRefCounts.TryGetValue(file, out int count);
            entry.FileRefCounts[file] = count + 1;
        }
    }

    /// <summary>Stop watching one reference to a file. The directory watcher is torn down when its last file leaves.</summary>
    public void Unwatch(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return;

        string dir;
        string file;
        try
        {
            dir = Path.GetDirectoryName(filePath) ?? string.Empty;
            file = Path.GetFileName(filePath);
        }
        catch
        {
            return;
        }
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
            return;

        lock (_lock)
        {
            if (!_directories.TryGetValue(dir, out var entry))
                return;

            if (entry.FileRefCounts.TryGetValue(file, out int count))
            {
                if (count <= 1)
                    entry.FileRefCounts.Remove(file);
                else
                    entry.FileRefCounts[file] = count - 1;
            }

            if (entry.FileRefCounts.Count == 0)
            {
                DisposeWatcher(entry.Watcher);
                _directories.Remove(dir);
            }
        }

        CancelDebounce(filePath);
    }

    // --- Raw OS event handlers (thread-pool threads) ---------------------

    private void OnRawChange(object sender, FileSystemEventArgs e)
        => ScheduleIfWatched(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // The old name disappearing and the new name appearing can each affect an open tab.
        ScheduleIfWatched(e.OldFullPath);
        ScheduleIfWatched(e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow or the watched directory vanished: we may have lost events, so
        // re-evaluate every file under this watcher rather than trust the now-stale stream.
        if (sender is not FileSystemWatcher watcher)
            return;

        List<string> affected = new();
        lock (_lock)
        {
            foreach (var (dir, entry) in _directories)
            {
                if (!ReferenceEquals(entry.Watcher, watcher))
                    continue;
                foreach (var file in entry.FileRefCounts.Keys)
                    affected.Add(Path.Combine(dir, file));
            }
        }

        foreach (var path in affected)
            Schedule(path);
    }

    // --- Debounce + dispatch ---------------------------------------------

    private void ScheduleIfWatched(string fullPath)
    {
        string dir;
        string file;
        try
        {
            dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
            file = Path.GetFileName(fullPath);
        }
        catch
        {
            return;
        }

        lock (_lock)
        {
            if (!_directories.TryGetValue(dir, out var entry))
                return;
            if (!entry.FileRefCounts.ContainsKey(file))
                return;
        }

        Schedule(fullPath);
    }

    private void Schedule(string fullPath)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            if (_debounceTimers.TryGetValue(fullPath, out var existing))
            {
                existing.Change(DebounceMs, Timeout.Infinite);
            }
            else
            {
                var timer = new Timer(OnDebounceElapsed, fullPath, DebounceMs, Timeout.Infinite);
                _debounceTimers[fullPath] = timer;
            }
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        var fullPath = (string)state!;

        lock (_lock)
        {
            if (_debounceTimers.Remove(fullPath, out var timer))
                timer.Dispose();
            if (_disposed)
                return;
        }

        // Marshal the notification onto the UI thread.
        _uiContext.Post(_ => FileEventDetected?.Invoke(fullPath), null);
    }

    private void CancelDebounce(string fullPath)
    {
        lock (_lock)
        {
            if (_debounceTimers.Remove(fullPath, out var timer))
                timer.Dispose();
        }
    }

    private static void DisposeWatcher(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch
        {
            // Best-effort teardown.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var entry in _directories.Values)
                DisposeWatcher(entry.Watcher);
            _directories.Clear();

            foreach (var timer in _debounceTimers.Values)
                timer.Dispose();
            _debounceTimers.Clear();
        }
    }
}
