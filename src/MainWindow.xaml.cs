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

    // Tab drag-and-drop state
    private Point _tabDragStartPoint;
    private bool _isDraggingTab;
    private Models.TabItem? _dragSourceTab;

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
        _viewModel.PrintFunc = () => PreviewControl.ShowPrintUI();
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

        // Reload-from-disk (external-change notification) reuses the F5 refresh path,
        // which preserves the current scroll position.
        _viewModel.ReloadActiveTabRequested = OnRefreshRequested;

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

            // Save scroll position for the tab we're navigating away from — but only
            // if the preview is actually showing it. When several files are opened at
            // once, ActiveTab changes fire faster than the preview re-renders; without
            // this guard the still-displayed previous file's scrollY would be saved onto
            // the freshly-opened tabs, making them open part-way down instead of at top.
            if (tabLeaving != null
                && string.Equals(tabLeaving.FilePath, PreviewControl.LastRenderedFilePath,
                    StringComparison.OrdinalIgnoreCase))
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

    private async void OnRefreshRequested()
    {
        // Capture the live scroll position before refreshing, so the user
        // doesn't lose their place when pressing F5.
        var activeTab = _viewModel.ActiveTab;
        if (activeTab != null)
            activeTab.ScrollPosition = await PreviewControl.GetScrollPositionAsync();

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
        _viewModel.Dispose();

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

    // Called when a second launch forwards its files here; pull the window to the
    // foreground so the user sees the files they just opened.
    public void ActivateFromAnotherInstance()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void CopyActiveTabAsMarkdown()
    {
        var tab = _viewModel.ActiveTab;
        if (tab != null) _ = PreviewControl.CopyAsMarkdownAsync(tab.MarkdownContent);
    }

    // ── Tab drag-and-drop reordering ──────────────────────────────────

    private void TabHeaders_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Don't start a drag if the click was on the close button.
        if (IsDescendantOfCloseButton(e.OriginalSource as DependencyObject))
            return;

        _tabDragStartPoint = e.GetPosition(null);
        _isDraggingTab = false;
        _dragSourceTab = HitTestTab(e.GetPosition(TabHeaders));
    }

    private void TabHeaders_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingTab || _dragSourceTab == null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        var currentPos = e.GetPosition(null);
        if (Math.Abs(currentPos.X - _tabDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPos.Y - _tabDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _isDraggingTab = true;
        DragDrop.DoDragDrop(TabHeaders, _dragSourceTab, DragDropEffects.Move);
        _isDraggingTab = false;
        _dragSourceTab = null;
        TabDropCaret.Visibility = Visibility.Collapsed;
    }

    private void TabHeaders_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        // Suppress the default OLE drag cursor (which flashes the no-entry icon
        // whenever the mouse passes over a non-drop-target child element). We use
        // the standard arrow cursor throughout the drag.
        e.UseDefaultCursors = false;
        e.Handled = true;
    }

    private void TabStrip_PreviewDragEnter(object sender, DragEventArgs e)
    {
        // Show the caret immediately when the drag enters the tab strip.
        if (e.Data.GetDataPresent(typeof(Models.TabItem)) &&
            e.Data.GetData(typeof(Models.TabItem)) is Models.TabItem draggedTab)
        {
            var insertIndex = CalculateInsertIndex(e.GetPosition(TabHeaders));
            PositionDropCaret(insertIndex);
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void TabStrip_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(Models.TabItem)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var draggedTab = e.Data.GetData(typeof(Models.TabItem)) as Models.TabItem;
        if (draggedTab == null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var insertIndex = CalculateInsertIndex(e.GetPosition(TabHeaders));
        PositionDropCaret(insertIndex);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void TabStrip_Drop(object sender, DragEventArgs e)
    {
        TabDropCaret.Visibility = Visibility.Collapsed;

        if (!e.Data.GetDataPresent(typeof(Models.TabItem)))
            return;

        var draggedTab = e.Data.GetData(typeof(Models.TabItem)) as Models.TabItem;
        if (draggedTab == null)
            return;

        int sourceIndex = _viewModel.Tabs.IndexOf(draggedTab);
        if (sourceIndex < 0)
            return;

        int targetIndex = CalculateInsertIndex(e.GetPosition(TabHeaders));
        _viewModel.MoveTab(sourceIndex, targetIndex);
    }

    private void TabStrip_DragLeave(object sender, DragEventArgs e)
    {
        // Only hide the caret if we're leaving the entire tab strip, not just
        // moving between sibling elements inside it. The sender is the Border,
        // so DragLeave means the mouse actually left the tab strip area.
        TabDropCaret.Visibility = Visibility.Collapsed;
    }

    /// <summary>Finds the tab under the mouse point within the ListBox.</summary>
    private Models.TabItem? HitTestTab(Point point)
    {
        for (int i = 0; i < _viewModel.Tabs.Count; i++)
        {
            var container = TabHeaders.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (container == null) continue;

            var bounds = container.TransformToAncestor(TabHeaders).TransformBounds(
                new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Contains(point))
                return _viewModel.Tabs[i];
        }

        return null;
    }

    /// <summary>
    /// Computes the insertion index for the caret based on horizontal mouse position.
    /// Returns an index in [0..Count]: 0 means before the first tab; Count means after the last.
    /// </summary>
    private int CalculateInsertIndex(Point point)
    {
        for (int i = 0; i < _viewModel.Tabs.Count; i++)
        {
            var container = TabHeaders.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (container == null) continue;

            var bounds = container.TransformToAncestor(TabHeaders).TransformBounds(
                new Rect(0, 0, container.ActualWidth, container.ActualHeight));

            // The insertion point is before the tab if the mouse is in the left half.
            if (point.X < bounds.Left + bounds.Width / 2)
                return i;
        }

        // Mouse is past the rightmost tab — insert at end.
        return _viewModel.Tabs.Count;
    }

    /// <summary>Positions (or hides) the vertical caret at the given insertion index.</summary>
    private void PositionDropCaret(int insertIndex)
    {
        if (insertIndex < 0)
        {
            TabDropCaret.Visibility = Visibility.Collapsed;
            return;
        }

        double leftPos;
        if (insertIndex >= _viewModel.Tabs.Count)
        {
            // Insert after last tab — find the right edge of the last container.
            var lastContainer = TabHeaders.ItemContainerGenerator
                .ContainerFromIndex(_viewModel.Tabs.Count - 1) as ListBoxItem;
            if (lastContainer != null)
            {
                var bounds = lastContainer.TransformToAncestor(TabHeaders).TransformBounds(
                    new Rect(0, 0, lastContainer.ActualWidth, lastContainer.ActualHeight));
                leftPos = bounds.Right;
            }
            else
            {
                leftPos = 0;
            }
        }
        else
        {
            // Insert before the tab at insertIndex.
            var container = TabHeaders.ItemContainerGenerator
                .ContainerFromIndex(insertIndex) as ListBoxItem;
            if (container != null)
            {
                var bounds = container.TransformToAncestor(TabHeaders).TransformBounds(
                    new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                leftPos = bounds.Left;
            }
            else
            {
                leftPos = 0;
            }
        }

        Canvas.SetLeft(TabDropCaret, leftPos);
        Canvas.SetTop(TabDropCaret, 4);
        TabDropCaret.Height = Math.Max(0, TabHeaders.ActualHeight - 8);
        TabDropCaret.Visibility = Visibility.Visible;
    }

    /// <summary>Returns true if <paramref name="element"/> is inside the tab's close button.</summary>
    private static bool IsDescendantOfCloseButton(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Button)
                return true;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }

        return false;
    }
}
