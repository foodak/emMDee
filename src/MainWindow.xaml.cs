using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using emMDee.Services;
using emMDee.ViewModels;
using Microsoft.Win32;

namespace emMDee;

/// <summary>
/// Main application window for the emMDee markdown viewer.
/// </summary>
public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly MainViewModel _viewModel;
    private readonly SessionManager _sessionManager;
    private bool _isClosing;
    private double _savedZoomFactor;
    private Models.TabItem? _previousActiveTab;

    public MainWindow()
    {
        InitializeComponent();

        _sessionManager = new SessionManager();
        _viewModel = new MainViewModel(_sessionManager);
        DataContext = _viewModel;

        // Wire up search events
        _viewModel.SearchInPreviewFunc = OnSearchInPreviewAsync;
        _viewModel.SearchDismissed += OnSearchDismissed;
        _viewModel.SearchFocusRequested += OnSearchFocusRequested;
        _viewModel.SwitchToNextTabForSearchRequested += OnSwitchToNextTabForSearch;

        // Wire up copy actions
        _viewModel.CopyAsRichFunc = () => PreviewControl.CopyAsRichAsync();
        _viewModel.CopyAsMarkdownAction = CopyActiveTabAsMarkdown;
        PreviewControl.CopyAsMarkdownRequested += CopyActiveTabAsMarkdown;

        // Restore saved window position and zoom factor
        var (left, top, width, height, maximized, zoomFactor) = _viewModel.GetSavedWindowState();
        if (!double.IsNaN(left)) Left = left;
        if (!double.IsNaN(top)) Top = top;
        Width = width;
        Height = height;
        if (maximized) WindowState = WindowState.Maximized;
        _savedZoomFactor = zoomFactor;

        // Wire up tab change to update preview
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        PreviewControl.RefreshRequested += OnRefreshRequested;

        Closing += OnWindowClosing;

        // Restore session after window is loaded, then apply saved zoom
        Loaded += (_, _) =>
        {
            _viewModel.RestoreSession();
            PreviewControl.SetZoomFactor(_savedZoomFactor);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var isDark = App.IsSystemDarkMode();
        ApplyDwmDarkMode(isDark);
        App.ApplyChromeColors(isDark);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void ApplyDwmDarkMode(bool isDark)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int value = isDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            Dispatcher.Invoke(() =>
            {
                var isDark = App.IsSystemDarkMode();
                ApplyDwmDarkMode(isDark);
                App.ApplyChromeColors(isDark);
            });
        }
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
        {
            // Capture which tab we're leaving before the await
            var tabLeaving = _previousActiveTab;
            _previousActiveTab = _viewModel.ActiveTab;

            // Save scroll position for the tab we're navigating away from
            if (tabLeaving != null)
                tabLeaving.ScrollPosition = await PreviewControl.GetScrollPositionAsync();

            UpdatePreviewForActiveTab();
            UpdateWelcomeVisibility();
            ScrollActiveTabIntoView();
        }
    }

    private void ScrollActiveTabIntoView()
    {
        var tab = _viewModel.ActiveTab;
        if (tab == null) return;
        // Defer one layout pass so the ListBox has measured/positioned the new selection.
        Dispatcher.BeginInvoke(
            () => TabHeaders.ScrollIntoView(tab),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnRefreshRequested()
    {
        if (_viewModel.ReloadActiveTab())
            UpdatePreviewForActiveTab();
    }

    private void UpdatePreviewForActiveTab()
    {
        var activeTab = _viewModel.ActiveTab;
        if (activeTab == null)
        {
            PreviewControl.Visibility = Visibility.Collapsed;
            return;
        }

        PreviewControl.Visibility = Visibility.Visible;
        PreviewControl.RenderMarkdown(activeTab.MarkdownContent, activeTab.ScrollPosition, activeTab.FilePath);
    }

    private void UpdateWelcomeVisibility()
    {
        WelcomePanel.Visibility = _viewModel.HasOpenTabs ? Visibility.Collapsed : Visibility.Visible;
    }

    private Task<bool> OnSearchInPreviewAsync(string searchText, bool forward, bool fromStart)
        => PreviewControl.SearchInPreviewAsync(searchText, forward, fromStart);

    private void OnSearchDismissed()
    {
        PreviewControl.ClearSearchHighlights();
    }

    private void OnSearchFocusRequested()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private async void OnSwitchToNextTabForSearch(bool forward)
    {
        var tabs = _viewModel.Tabs;
        if (tabs.Count <= 1) return;

        // Save current tab's scroll before we start moving
        if (_viewModel.ActiveTab != null)
            _viewModel.ActiveTab.ScrollPosition = await PreviewControl.GetScrollPositionAsync();

        int startIndex = _viewModel.ActiveTab != null ? tabs.IndexOf(_viewModel.ActiveTab) : 0;
        int checkIndex = startIndex;

        // Walk every other tab in order, stopping as soon as a match is found.
        // The loop runs at most (Count - 1) times so we never revisit the starting tab.
        for (int i = 0; i < tabs.Count - 1; i++)
        {
            checkIndex = forward
                ? (checkIndex + 1) % tabs.Count
                : ((checkIndex - 1) + tabs.Count) % tabs.Count;

            var candidateTab = tabs[checkIndex];

            // Null _previousActiveTab so OnViewModelPropertyChanged runs synchronously
            // (no async scroll-save delay), avoiding a render race with our awaited render below.
            _previousActiveTab = null;
            _viewModel.ActiveTab = candidateTab;

            PreviewControl.Visibility = Visibility.Visible;
            await PreviewControl.RenderMarkdownAsync(candidateTab.MarkdownContent, candidateTab.ScrollPosition, candidateTab.FilePath);
            UpdateWelcomeVisibility();

            bool noMatch = await PreviewControl.SearchInPreviewAsync(
                _viewModel.SearchText, forward, fromStart: true);

            if (!noMatch)
                return; // found a match — stay on this tab
        }

        // No match anywhere — restore the tab we started from so the user isn't left
        // on a random no-match tab.
        _previousActiveTab = null;
        var startTab = tabs[startIndex];
        _viewModel.ActiveTab = startTab;
        PreviewControl.Visibility = Visibility.Visible;
        await PreviewControl.RenderMarkdownAsync(startTab.MarkdownContent, startTab.ScrollPosition, startTab.FilePath);
        UpdateWelcomeVisibility();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;

        // Cancel the close to allow async scroll capture, then re-close
        e.Cancel = true;
        _isClosing = true;

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        // Save the active tab's scroll position before closing
        if (_viewModel.ActiveTab != null)
            _viewModel.ActiveTab.ScrollPosition = await PreviewControl.GetScrollPositionAsync();

        _viewModel.SaveSession(Left, Top, Width, Height, WindowState == WindowState.Maximized, PreviewControl.ZoomFactor);

        // Defer the re-close to a later dispatcher cycle. Calling Close() directly here
        // can throw "Cannot... call Close... while a Window is closing" when the async
        // path completed synchronously (no WebView ready, or no active tab).
        _ = Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Background);
    }

    // --- Drag and Drop handlers ---

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var file in files)
            {
                if (IsMarkdownFile(file))
                    _viewModel.OpenFile(file);
            }
        }
        e.Handled = true;
    }

    private void TabContent_DragEnter(object sender, DragEventArgs e)
    {
        Window_DragEnter(sender, e);
    }

    private void TabContent_Drop(object sender, DragEventArgs e)
    {
        Window_Drop(sender, e);
    }

    private static bool IsMarkdownFile(string filePath)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        return ext == ".md" || ext == ".markdown" || ext == ".mdown" || ext == ".mkd";
    }

    // --- Search box key handler ---

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
                _viewModel.FindPrevious();
            else
                _viewModel.FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.ToggleSearch();
            e.Handled = true;
        }
    }

    // --- Tab close button ---

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Models.TabItem tab)
        {
            _viewModel.CloseTab(tab);
            UpdateWelcomeVisibility();
        }
    }

    // --- Public API for App.xaml.cs ---

    public void OpenFileFromCommandLine(string path)
    {
        _viewModel.OpenFile(path);
    }

    private void CopyActiveTabAsMarkdown()
    {
        var tab = _viewModel.ActiveTab;
        if (tab != null) _ = PreviewControl.CopyAsMarkdownAsync(tab.MarkdownContent);
    }
}
