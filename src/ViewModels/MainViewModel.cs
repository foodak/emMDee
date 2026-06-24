using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using emMDee.Models;
using emMDee.Services;
using Microsoft.Win32;

namespace emMDee.ViewModels;

/// <summary>
/// Main view model for the markdown viewer application.
/// </summary>
public class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxRecentFiles = 10;

    private readonly SessionManager _sessionManager;
    private readonly FileWatchService _watchService;

    public ObservableCollection<Models.TabItem> Tabs { get; } = new();

    private Models.TabItem? _activeTab;
    public Models.TabItem? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (SetProperty(ref _activeTab, value))
            {
                OnPropertyChanged(nameof(HasOpenTabs));
                RefreshFileNotification();
            }
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    private bool _isSearchVisible;
    public bool IsSearchVisible
    {
        get => _isSearchVisible;
        set => SetProperty(ref _isSearchVisible, value);
    }

    private bool _isFindInAllTabs;
    public bool IsFindInAllTabs
    {
        get => _isFindInAllTabs;
        private set
        {
            if (SetProperty(ref _isFindInAllTabs, value))
                OnPropertyChanged(nameof(SearchBarLabel));
        }
    }

    public string SearchBarLabel => IsFindInAllTabs ? "Find all:" : "Find:";

    public bool HasOpenTabs => Tabs.Count > 0;

    // --- External-change notification (reflects the ActiveTab's state) ----

    public bool IsFileNotificationVisible =>
        ActiveTab?.ExternalState is ExternalChangeState.Modified or ExternalChangeState.Deleted;

    public bool IsFileModifiedNotification => ActiveTab?.ExternalState == ExternalChangeState.Modified;

    public bool IsFileDeletedNotification => ActiveTab?.ExternalState == ExternalChangeState.Deleted;

    public string FileNotificationMessage => ActiveTab?.ExternalState switch
    {
        ExternalChangeState.Modified => "This file has changed on disk.",
        ExternalChangeState.Deleted => "This file has been deleted or moved.",
        _ => string.Empty
    };

    /// <summary>Re-publishes every notification-derived property — call after ActiveTab or its state changes.</summary>
    private void RefreshFileNotification()
    {
        OnPropertyChanged(nameof(IsFileNotificationVisible));
        OnPropertyChanged(nameof(IsFileModifiedNotification));
        OnPropertyChanged(nameof(IsFileDeletedNotification));
        OnPropertyChanged(nameof(FileNotificationMessage));
    }

    /// <summary>Raised when the user clicks Reload — the view reloads + re-renders the active tab.</summary>
    public Action? ReloadActiveTabRequested;

    public ObservableCollection<RecentFileItem> RecentFiles { get; } = new();

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public ICommand OpenFileCommand { get; }
    public ICommand OpenRecentFileCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand CloseAllTabsCommand { get; }
    public ICommand ToggleSearchCommand { get; }
    public ICommand FindNextCommand { get; }
    public ICommand FindPreviousCommand { get; }
    public ICommand FindInAllTabsCommand { get; }
    public ICommand CopyAsRichCommand { get; }
    public ICommand CopyAsMarkdownCommand { get; }
    public ICommand PrintCommand { get; }
    public ICommand ReloadFromDiskCommand { get; }
    public ICommand DismissFileNotificationCommand { get; }
    public ICommand CloseDeletedTabCommand { get; }
    public ICommand ExitCommand { get; }

    // Func delegate for searching (returns true if search wrapped around / no matches)
    public Func<string, bool, bool, Task<bool>>? SearchInPreviewFunc;

    // Func delegates for copy actions, wired up by MainWindow to talk to the preview.
    public Func<Task>? CopyAsRichFunc;
    public Action? CopyAsMarkdownAction;
    public Action? PrintFunc;

    // Events to communicate with the view
    public event Action? SearchDismissed;
    public event Action? SearchFocusRequested;
    public event Action<bool>? SwitchToNextTabForSearchRequested;

    public MainViewModel(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;

        _watchService = new FileWatchService();
        _watchService.FileEventDetected += OnFileEventDetected;

        OpenFileCommand = new RelayCommand(_ => OpenFile());
        OpenRecentFileCommand = new RelayCommand(param => OpenRecentFile(param as string));
        CloseTabCommand = new RelayCommand(param => CloseTab(param as Models.TabItem));
        CloseAllTabsCommand = new RelayCommand(_ => CloseAllTabs(), _ => HasOpenTabs);
        ToggleSearchCommand = new RelayCommand(_ => ToggleSearch());
        FindNextCommand = new RelayCommand(_ => FindNext());
        FindPreviousCommand = new RelayCommand(_ => FindPrevious());
        FindInAllTabsCommand = new RelayCommand(_ => OpenFindInAllTabs());
        CopyAsRichCommand = new RelayCommand(
            _ => { if (CopyAsRichFunc != null) _ = CopyAsRichFunc(); },
            _ => ActiveTab != null);
        CopyAsMarkdownCommand = new RelayCommand(
            _ => CopyAsMarkdownAction?.Invoke(),
            _ => ActiveTab != null);
        PrintCommand = new RelayCommand(
            _ => PrintFunc?.Invoke(),
            _ => ActiveTab != null);
        ReloadFromDiskCommand = new RelayCommand(_ => ReloadActiveTabRequested?.Invoke());
        DismissFileNotificationCommand = new RelayCommand(_ => DismissFileNotification());
        CloseDeletedTabCommand = new RelayCommand(_ => CloseTab(ActiveTab));
        ExitCommand = new RelayCommand(_ => Application.Current.Shutdown());

        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasOpenTabs));
        };

        RecentFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRecentFiles));
        };
    }

    public void OpenFile(string? filePath = null)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*",
                Multiselect = true,
                Title = "Open Markdown File"
            };

            if (dialog.ShowDialog() != true)
                return;

            foreach (var path in dialog.FileNames)
            {
                OpenFileByPath(path);
            }
        }
        else
        {
            OpenFileByPath(filePath);
        }
    }

    public bool ReloadActiveTab()
    {
        var tab = ActiveTab;
        if (tab == null || string.IsNullOrEmpty(tab.FilePath) || !File.Exists(tab.FilePath))
            return false;
        try
        {
            tab.MarkdownContent = File.ReadAllText(tab.FilePath);
            tab.SnapshotFileInfo();
            tab.ExternalState = ExternalChangeState.None;
            RefreshFileNotification();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Handles a debounced "this file may have changed" signal from the watcher (UI thread).
    /// Compares disk against the tab's snapshot and sets the tab's <see cref="ExternalChangeState"/>.
    /// </summary>
    private void OnFileEventDetected(string fullPath)
    {
        foreach (var tab in Tabs)
        {
            if (!string.Equals(tab.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (tab.MatchesDisk(out bool exists))
                continue; // spurious event — disk still matches our snapshot

            tab.ExternalState = exists ? ExternalChangeState.Modified : ExternalChangeState.Deleted;

            if (ReferenceEquals(tab, ActiveTab))
                RefreshFileNotification();
        }
    }

    /// <summary>
    /// Dismisses the notification for the active tab ("keep my view"). For a modified file we
    /// re-snapshot so only the *next* change re-notifies; for a deleted file we simply clear it.
    /// </summary>
    private void DismissFileNotification()
    {
        var tab = ActiveTab;
        if (tab == null)
            return;

        if (tab.ExternalState == ExternalChangeState.Modified)
            tab.SnapshotFileInfo();

        tab.ExternalState = ExternalChangeState.None;
        RefreshFileNotification();
    }

    private void OpenFileByPath(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        // Add to recent files (before checking if already open,
        // so re-opening an existing tab still bumps it to the top)
        AddToRecentFiles(filePath);

        // Check if already open
        var existing = Tabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            ActiveTab = existing;
            return;
        }

        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to read file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var tab = Models.TabItem.FromFile(filePath, content);
        Tabs.Add(tab);
        _watchService.Watch(tab.FilePath);
        ActiveTab = tab;
    }

    private void AddToRecentFiles(string filePath)
    {
        // Remove if already present (so it can be re-inserted at the top)
        var existing = RecentFiles.FirstOrDefault(f =>
            string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            RecentFiles.Remove(existing);

        // Insert at the front
        RecentFiles.Insert(0, new RecentFileItem(filePath));

        // Trim to max
        while (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
    }

    private void OpenRecentFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return;

        if (!File.Exists(filePath))
        {
            // Remove stale entries
            var stale = RecentFiles.FirstOrDefault(f =>
                string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (stale != null)
                RecentFiles.Remove(stale);

            MessageBox.Show($"File not found:\n{filePath}", "Missing File",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OpenFileByPath(filePath);
    }

    public void CloseTab(Models.TabItem? tab)
    {
        if (tab == null && ActiveTab != null)
            tab = ActiveTab;

        if (tab == null)
            return;

        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        _watchService.Unwatch(tab.FilePath);

        if (Tabs.Count > 0)
        {
            ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
        else
        {
            ActiveTab = null;
        }
    }

    public void CloseAllTabs()
    {
        foreach (var tab in Tabs)
            _watchService.Unwatch(tab.FilePath);

        Tabs.Clear();
        ActiveTab = null;
    }

    public void ToggleSearch()
    {
        IsSearchVisible = !IsSearchVisible;
        if (!IsSearchVisible)
        {
            IsFindInAllTabs = false;
            SearchText = string.Empty;
            SearchDismissed?.Invoke();
        }
        else
        {
            SearchFocusRequested?.Invoke();
        }
    }

    private void OpenFindInAllTabs()
    {
        IsFindInAllTabs = true;
        IsSearchVisible = true;
        SearchFocusRequested?.Invoke();
    }

    public async void FindNext()
    {
        if (string.IsNullOrEmpty(SearchText) || SearchInPreviewFunc == null) return;

        bool wrapped = await SearchInPreviewFunc(SearchText, true, false);

        if (wrapped && IsFindInAllTabs && Tabs.Count > 1)
            SwitchToNextTabForSearchRequested?.Invoke(true);
    }

    public async void FindPrevious()
    {
        if (string.IsNullOrEmpty(SearchText) || SearchInPreviewFunc == null) return;

        bool wrapped = await SearchInPreviewFunc(SearchText, false, false);

        if (wrapped && IsFindInAllTabs && Tabs.Count > 1)
            SwitchToNextTabForSearchRequested?.Invoke(false);
    }

    public SessionData GetSessionData(double left, double top, double width, double height, bool maximized, double zoomFactor = 1.0)
    {
        return new SessionData
        {
            OpenFilePaths = Tabs.Select(t => t.FilePath).ToList(),
            RecentFilePaths = RecentFiles.Select(f => f.FilePath).ToList(),
            ActiveTabIndex = ActiveTab != null ? Tabs.IndexOf(ActiveTab) : -1,
            WindowLeft = left,
            WindowTop = top,
            WindowWidth = width,
            WindowHeight = height,
            WindowMaximized = maximized,
            ZoomFactor = zoomFactor,
            TabScrollPositions = Tabs.Select(t => t.ScrollPosition).ToList()
        };
    }

    public void SaveSession(double left, double top, double width, double height, bool maximized, double zoomFactor = 1.0)
    {
        var data = GetSessionData(left, top, width, height, maximized, zoomFactor);
        _sessionManager.Save(data);
    }

    public void RestoreSession()
    {
        var data = _sessionManager.Load();
        if (data == null)
            return;

        // Restore recent files list
        foreach (var path in data.RecentFilePaths.Where(File.Exists))
            RecentFiles.Add(new RecentFileItem(path));

        // Create all tabs with their scroll positions before setting ActiveTab,
        // so the initial render gets the correct scroll position
        for (int i = 0; i < data.OpenFilePaths.Count; i++)
        {
            var path = data.OpenFilePaths[i];
            if (!File.Exists(path)) continue;

            string content;
            try { content = File.ReadAllText(path); }
            catch { continue; }

            var tab = Models.TabItem.FromFile(path, content);
            if (i < data.TabScrollPositions.Count)
                tab.ScrollPosition = data.TabScrollPositions[i];
            Tabs.Add(tab);
            _watchService.Watch(tab.FilePath);
        }

        if (data.ActiveTabIndex >= 0 && data.ActiveTabIndex < Tabs.Count)
            ActiveTab = Tabs[data.ActiveTabIndex];
        else if (Tabs.Count > 0)
            ActiveTab = Tabs[0];
    }

    public (double left, double top, double width, double height, bool maximized, double zoomFactor) GetSavedWindowState()
    {
        var data = _sessionManager.Load();
        if (data == null)
            return (double.NaN, double.NaN, 900, 600, false, 1.0);

        return (data.WindowLeft, data.WindowTop, data.WindowWidth, data.WindowHeight, data.WindowMaximized, data.ZoomFactor);
    }

    public void Dispose()
    {
        _watchService.FileEventDetected -= OnFileEventDetected;
        _watchService.Dispose();
    }
}
