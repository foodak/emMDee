using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace emMDee.Views;

/// <summary>
/// A WPF UserControl that wraps WebView2 for rendering markdown as rich HTML preview.
/// </summary>
public partial class MarkdownPreviewControl : UserControl
{
    public event Action? RefreshRequested;

    // Raised when the user picks "Copy as Markdown" from the context menu.
    // The host should respond by calling CopyAsMarkdown(activeTab.MarkdownContent).
    public event Action? CopyAsMarkdownRequested;

    private bool _webViewReady;
    private string? _pendingMarkdown;
    private string? _pendingFilePath;
    private double _pendingScrollPosition;
    private double _pendingZoomFactor = 1.0;
    private (string text, bool forward)? _pendingSearch;
    private string? _lastRenderedMarkdown;
    private string? _lastRenderedFilePath;

    public MarkdownPreviewControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            // Check if WebView2 is available
            string? version = null;
            try
            {
                version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                // WebView2 not installed
            }

            if (string.IsNullOrEmpty(version))
            {
                WebViewMissingBanner.Visibility = Visibility.Visible;
                return;
            }

            var env = await CoreWebView2Environment.CreateAsync();
            await WebView.EnsureCoreWebView2Async(env);

            // Load the HTML template
            var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "index.html");
            if (File.Exists(htmlPath))
            {
                WebView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
            else
            {
                // Fallback: load inline HTML with marked.js from CDN
                LoadFallbackHtml();
            }

            WebView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _webViewReady = true;

                if (_pendingZoomFactor != 1.0)
                    WebView.ZoomFactor = _pendingZoomFactor;

                if (_pendingMarkdown != null)
                {
                    RenderMarkdown(_pendingMarkdown, _pendingScrollPosition, _pendingFilePath);
                    _pendingMarkdown = null;
                    _pendingFilePath = null;
                    _pendingScrollPosition = 0;
                }
                else if (_lastRenderedMarkdown != null)
                {
                    // Restore content after any unexpected page reload
                    RenderMarkdown(_lastRenderedMarkdown, 0, _lastRenderedFilePath);
                }

                if (_pendingSearch != null)
                {
                    SearchInPreview(_pendingSearch.Value.text, _pendingSearch.Value.forward);
                    _pendingSearch = null;
                }
            };

            // Intercept F5/Ctrl+R browser reload: cancel the navigation and treat it as a file refresh
            WebView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (_webViewReady && args.Uri == WebView.CoreWebView2.Source)
                {
                    args.Cancel = true;
                    RefreshRequested?.Invoke();
                }
            };

            WebView.CoreWebView2.ContextMenuRequested += OnContextMenuRequested;
            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            WebView.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            WebViewMissingBanner.Visibility = Visibility.Visible;
            System.Diagnostics.Debug.WriteLine($"WebView2 init error: {ex.Message}");
        }
    }

    private void LoadFallbackHtml()
    {
        var html = @"
<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<style>
  body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
         padding: 20px 40px; max-width: 900px; margin: 0 auto; line-height: 1.6; color: #333; }
  pre { background: #f5f5f5; padding: 16px; border-radius: 6px; overflow-x: auto; }
  code { background: #f0f0f0; padding: 2px 6px; border-radius: 3px; }
  pre code { background: none; padding: 0; }
  table { border-collapse: collapse; width: 100%; }
  th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }
  th { background: #f5f5f5; }
  blockquote { border-left: 4px solid #ddd; margin-left: 0; padding-left: 16px; color: #666; }
  img { max-width: 100%; }
  .search-highlight { background-color: #FFEB3B; }
  .search-highlight-current { background-color: #FF9800; }
</style>
</head>
<body>
  <div id='content'>Loading...</div>
  <script>
    // Minimal inline markdown parser for when marked.js is not bundled
    function escapeHtml(text) {
      return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }
    function renderMarkdown(md) {
      // Simple fallback: wrap in <pre> if no marked.js
      document.getElementById('content').innerHTML = '<pre>' + escapeHtml(md) + '</pre>';
    }
    function searchText(text) {
      // Clear previous highlights
      document.querySelectorAll('.search-highlight,.search-highlight-current').forEach(el => {
        el.outerHTML = el.innerHTML;
      });
      if (!text) return;
      const content = document.getElementById('content');
      const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT);
      const textNodes = [];
      while (walker.nextNode()) textNodes.push(walker.currentNode);
      textNodes.forEach(node => {
        const parent = node.parentNode;
        if (!parent) return;
        const html = parent.innerHTML;
        const escaped = escapeHtml(node.textContent);
        // Simple search replace
      });
    }
  </script>
</body>
</html>";
        WebView.NavigateToString(html);
    }

    public double ZoomFactor => _webViewReady ? WebView.ZoomFactor : _pendingZoomFactor;

    public void SetZoomFactor(double zoom)
    {
        _pendingZoomFactor = zoom;
        if (_webViewReady)
            WebView.ZoomFactor = zoom;
    }

    public async Task<double> GetScrollPositionAsync()
    {
        if (!_webViewReady) return 0;
        try
        {
            var result = await WebView.CoreWebView2.ExecuteScriptAsync("window.scrollY");
            if (double.TryParse(result, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var pos))
                return pos;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetScrollPosition error: {ex.Message}");
        }
        return 0;
    }

    public void RenderMarkdown(string markdown, double scrollPosition = 0, string? filePath = null)
    {
        if (!_webViewReady)
        {
            _pendingMarkdown = markdown;
            _pendingFilePath = filePath;
            _pendingScrollPosition = scrollPosition;
            return;
        }

        _lastRenderedMarkdown = markdown;
        _lastRenderedFilePath = filePath;

        var escaped = markdown
            .Replace("\\", "\\\\")
            .Replace("`", "\\`")
            .Replace("$", "\\$");

        var baseUrlJson = BuildBaseUrlJson(filePath);
        var posStr = scrollPosition.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var script = $"renderMarkdown(`{escaped}`, {baseUrlJson}); window.scrollTo(0, {posStr});";
        WebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    public void SearchInPreview(string searchText, bool forward = true)
        => _ = SearchInPreviewAsync(searchText, forward);

    public async Task<bool> SearchInPreviewAsync(string searchText, bool forward = true, bool fromStart = false)
    {
        if (!_webViewReady)
        {
            _pendingSearch = (searchText, forward);
            return false;
        }

        var escaped = searchText
            .Replace("\\", "\\\\")
            .Replace("'", "\\'");

        var direction = forward ? "next" : "prev";
        var fromStartStr = fromStart ? "true" : "false";
        var script = $"searchText('{escaped}', '{direction}', {fromStartStr});";
        var result = await WebView.CoreWebView2.ExecuteScriptAsync(script);
        return result == "true";
    }

    public async Task RenderMarkdownAsync(string markdown, double scrollPosition = 0, string? filePath = null)
    {
        if (!_webViewReady)
        {
            _pendingMarkdown = markdown;
            _pendingFilePath = filePath;
            _pendingScrollPosition = scrollPosition;
            return;
        }

        _lastRenderedMarkdown = markdown;
        _lastRenderedFilePath = filePath;

        var escaped = markdown
            .Replace("\\", "\\\\")
            .Replace("`", "\\`")
            .Replace("$", "\\$");

        var baseUrlJson = BuildBaseUrlJson(filePath);
        var posStr = scrollPosition.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await WebView.CoreWebView2.ExecuteScriptAsync($"renderMarkdown(`{escaped}`, {baseUrlJson}); window.scrollTo(0, {posStr});");
    }

    private static string BuildBaseUrlJson(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return "null";
        var dir = Path.GetDirectoryName(filePath);
        if (dir == null) return "null";
        var uri = new Uri(dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        return System.Text.Json.JsonSerializer.Serialize(uri.AbsoluteUri);
    }

    public void ClearSearchHighlights()
    {
        if (_webViewReady)
            WebView.CoreWebView2.ExecuteScriptAsync("clearSearch();");
    }

    // --- Copy ---

    // Shortcuts pressed inside the WebView post a message back here, since
    // WebView2 doesn't surface key events to WPF KeyBindings.
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.TryGetProperty("type", out var t)
                && t.GetString() == "copy-as-markdown")
            {
                CopyAsMarkdownRequested?.Invoke();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnWebMessageReceived error: {ex.Message}");
        }
    }

    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        var env = WebView.CoreWebView2.Environment;
        var items = args.MenuItems;
        items.Clear();

        var copyRich = env.CreateContextMenuItem(
            "Copy", null, CoreWebView2ContextMenuItemKind.Command);
        copyRich.CustomItemSelected += (_, _) => _ = CopyAsRichAsync();
        items.Add(copyRich);

        var copyMd = env.CreateContextMenuItem(
            "Copy as Markdown", null, CoreWebView2ContextMenuItemKind.Command);
        copyMd.CustomItemSelected += (_, _) => CopyAsMarkdownRequested?.Invoke();
        items.Add(copyMd);

        items.Add(env.CreateContextMenuItem(
            string.Empty, null, CoreWebView2ContextMenuItemKind.Separator));

        var selectAll = env.CreateContextMenuItem(
            "Select All", null, CoreWebView2ContextMenuItemKind.Command);
        selectAll.CustomItemSelected += (_, _) =>
            WebView.CoreWebView2.ExecuteScriptAsync("document.execCommand('selectAll');");
        items.Add(selectAll);
    }

    /// <summary>
    /// Copies the current selection (or whole document if no selection) to the
    /// clipboard as both HTML and plain text. Word/Outlook get formatting,
    /// plain-text destinations get plain text.
    /// </summary>
    public async Task CopyAsRichAsync()
    {
        if (!_webViewReady) return;
        try
        {
            var json = await WebView.CoreWebView2.ExecuteScriptAsync("getCopyPayload()");
            // ExecuteScriptAsync returns a JSON-encoded string; the result itself is JSON,
            // so we end up with a JSON-encoded JSON string. Unwrap once.
            var inner = JsonSerializer.Deserialize<string>(json);
            if (string.IsNullOrEmpty(inner)) return;

            using var doc = JsonDocument.Parse(inner);
            var root = doc.RootElement;
            var html = root.GetProperty("html").GetString() ?? string.Empty;
            var text = root.GetProperty("text").GetString() ?? string.Empty;

            SetRichClipboard(html, text);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CopyAsRichAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies markdown to the clipboard as plain text.
    /// If there is a selection in the preview, extracts the corresponding raw
    /// markdown source for that selection. Otherwise copies the full raw markdown.
    /// </summary>
    public async Task CopyAsMarkdownAsync(string markdownSource)
    {
        string? selectedMd = null;
        if (_webViewReady)
        {
            try
            {
                var result = await WebView.CoreWebView2.ExecuteScriptAsync("getSelectedMarkdown()");
                selectedMd = JsonSerializer.Deserialize<string>(result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CopyAsMarkdownAsync selection probe failed: {ex.Message}");
            }
        }

        var textToCopy = !string.IsNullOrEmpty(selectedMd) ? selectedMd : markdownSource;
        if (!string.IsNullOrEmpty(textToCopy)) SetTextClipboard(textToCopy);
    }

    private static void SetRichClipboard(string html, string text)
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.Html, WrapHtmlForClipboard(html));
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData(DataFormats.Text, text);
            Clipboard.SetDataObject(data, true);
        }
        catch (COMException)
        {
            // Clipboard occasionally busy; one quick retry.
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.Html, WrapHtmlForClipboard(html));
                data.SetData(DataFormats.UnicodeText, text);
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SetRichClipboard retry failed: {ex.Message}");
            }
        }
    }

    private static void SetTextClipboard(string text)
    {
        try { Clipboard.SetText(text, TextDataFormat.UnicodeText); }
        catch (COMException)
        {
            try { Clipboard.SetText(text, TextDataFormat.UnicodeText); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SetTextClipboard retry failed: {ex.Message}");
            }
        }
    }

    // Wraps an HTML fragment in the CF_HTML header that Windows expects on the
    // clipboard. Offsets are UTF-8 byte counts per the CF_HTML spec.
    private static string WrapHtmlForClipboard(string fragmentHtml)
    {
        const string headerFormat =
            "Version:0.9\r\n" +
            "StartHTML:{0:D10}\r\n" +
            "EndHTML:{1:D10}\r\n" +
            "StartFragment:{2:D10}\r\n" +
            "EndFragment:{3:D10}\r\n";
        const string htmlStart = "<html><body>";
        const string fragmentStart = "<!--StartFragment-->";
        const string fragmentEnd = "<!--EndFragment-->";
        const string htmlEnd = "</body></html>";

        // D10 fixes the header to a constant byte length regardless of the actual values,
        // so we can compute offsets in a single pass.
        int headerLen = Encoding.UTF8.GetByteCount(string.Format(headerFormat, 0, 0, 0, 0));
        int startHtml = headerLen;
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(htmlStart + fragmentStart);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragmentHtml);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(fragmentEnd + htmlEnd);

        var header = string.Format(headerFormat, startHtml, endHtml, startFragment, endFragment);
        return header + htmlStart + fragmentStart + fragmentHtml + fragmentEnd + htmlEnd;
    }
}
