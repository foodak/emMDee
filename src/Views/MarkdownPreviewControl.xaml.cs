using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

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

    // Stores target info from the JS contextmenu listener (received via postMessage
    // before OnContextMenuRequested fires). Reset to null after each use.
    private ContextTargetInfo? _lastContextInfo;

    private class ContextTargetInfo
    {
        public bool HasImage { get; set; }
        public string? ImageSrc { get; set; }
        public bool HasLink { get; set; }
        public string? LinkHref { get; set; }
    }

    private bool _webViewReady;
    private string? _pendingMarkdown;
    private string? _pendingFilePath;
    private double _pendingScrollPosition;
    private double _pendingZoomFactor = 1.0;
    private (string text, bool forward)? _pendingSearch;
    private string? _lastRenderedMarkdown;
    private string? _lastRenderedFilePath;
    private string? _localPageUri;

    private bool _skipNextNavCompleted;

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
                _localPageUri = new Uri(htmlPath).AbsoluteUri;
                WebView.CoreWebView2.Navigate(_localPageUri);
            }
            else
            {
                // Fallback: load inline HTML with marked.js from CDN
                _localPageUri = "about:blank";
                LoadFallbackHtml();
            }

            WebView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _webViewReady = true;

                // If WebView2 session-restored to an external page, force back to local preview.
                // Only trigger for truly external URIs — same-page fragments (#section) are fine.
                if (_localPageUri != null
                    && WebView.CoreWebView2.Source != null
                    && !WebView.CoreWebView2.Source.StartsWith(_localPageUri, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(WebView.CoreWebView2.Source, "about:blank", StringComparison.OrdinalIgnoreCase))
                {
                    WebView.CoreWebView2.Navigate(_localPageUri);
                    return; // will re-enter NavigationCompleted after reload
                }

                if (_pendingZoomFactor != 1.0)
                    WebView.ZoomFactor = _pendingZoomFactor;

                if (_pendingMarkdown != null)
                {
                    RenderMarkdown(_pendingMarkdown, _pendingScrollPosition, _pendingFilePath);
                    _pendingMarkdown = null;
                    _pendingFilePath = null;
                    _pendingScrollPosition = 0;
                }
                else if (_skipNextNavCompleted)
                {
                    _skipNextNavCompleted = false;  // fragment navigation, no re-render needed
                }

                if (_pendingSearch != null)
                {
                    SearchInPreview(_pendingSearch.Value.text, _pendingSearch.Value.forward);
                    _pendingSearch = null;
                }
            };

            // Intercept all navigations away from the local preview page.
            WebView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!_webViewReady) return;

                var isSelfReload = args.Uri == WebView.CoreWebView2.Source;
                // Our preview page is local; also allow same-page fragment navigations
                // (e.g. clicking a TOC link that points to #heading).
                var isOurPage = args.Uri == _localPageUri
                    || args.Uri == "about:blank"
                    || (_localPageUri != null && args.Uri.StartsWith(_localPageUri + "#", StringComparison.Ordinal));

                if (isSelfReload)
                {
                    // F5/Ctrl+R: treat as file refresh
                    args.Cancel = true;
                    RefreshRequested?.Invoke();
                }
                else if (!isOurPage)
                {
                    // External or local-file link: open in system app, keep preview.
                    args.Cancel = true;
                    var target = args.Uri;
                    try
                    {
                        // Convert file:// URIs to local paths for Process.Start reliability
                        if (target.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var uri = new Uri(target);
                                target = Uri.UnescapeDataString(uri.LocalPath);
                            }
                            catch { }
                        }
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = target,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[emMDee] Failed to open link: {ex.Message}");
                    }
                }
                else
                {
                    // Same-page fragment navigation — suppress the re-render in
                    // NavigationCompleted so scroll position survives the hash change.
                    _skipNextNavCompleted = args.Uri != _localPageUri;
                }
                // else: local page navigation (initial load or same-page fragment) — allow
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

    // File path of the markdown currently rendered in the preview (null if none yet).
    // Used to guard scroll-position capture: scrollY only belongs to this file.
    public string? LastRenderedFilePath => _lastRenderedFilePath;

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

        // If WebView navigated away to an external page (e.g. clicked link),
        // re-navigate to our local preview page and queue the render.
        if (_localPageUri != null
            && WebView.CoreWebView2.Source != null
            && !WebView.CoreWebView2.Source.StartsWith(_localPageUri, StringComparison.OrdinalIgnoreCase))
        {
            _pendingMarkdown = markdown;
            _pendingFilePath = filePath;
            _pendingScrollPosition = scrollPosition;
            WebView.CoreWebView2.Navigate(_localPageUri);
            return;
        }

        _lastRenderedMarkdown = markdown;
        _lastRenderedFilePath = filePath;

        var inlined = InlineLocalImages(markdown, filePath);
        var escaped = inlined
            .Replace("\\", "\\\\")
            .Replace("`", "\\`")
            .Replace("$", "\\$");

        var baseUrlJson = BuildBaseUrlJson(filePath);
        var posStr = scrollPosition.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var script = $"renderMarkdown(`{escaped}`, {baseUrlJson}, {posStr});";
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

        // If WebView navigated away to an external page, re-navigate and queue.
        if (_localPageUri != null
            && WebView.CoreWebView2.Source != null
            && !WebView.CoreWebView2.Source.StartsWith(_localPageUri, StringComparison.OrdinalIgnoreCase))
        {
            _pendingMarkdown = markdown;
            _pendingFilePath = filePath;
            _pendingScrollPosition = scrollPosition;
            WebView.CoreWebView2.Navigate(_localPageUri);
            return;
        }

        _lastRenderedMarkdown = markdown;
        _lastRenderedFilePath = filePath;

        var inlined = InlineLocalImages(markdown, filePath);
        var escaped = inlined
            .Replace("\\", "\\\\")
            .Replace("`", "\\`")
            .Replace("$", "\\$");

        var baseUrlJson = BuildBaseUrlJson(filePath);
        var posStr = scrollPosition.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await WebView.CoreWebView2.ExecuteScriptAsync($"renderMarkdown(`{escaped}`, {baseUrlJson}, {posStr});");
    }

    // Replaces relative image references in markdown with data: URIs so that
    // WebView2's cross-directory file:// restriction never blocks them.
    // Matches ![alt text](path) — path allows spaces, parentheses, and most
    // common filename characters (parentheses nested one level deep).
    private static readonly Regex _imgRegex =
        new(@"!\[([^\]]*)\]\(((?:[^()\s]|\([^)]*\))+)\)", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> _mimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".png",  "image/png"     },
        { ".jpg",  "image/jpeg"    },
        { ".jpeg", "image/jpeg"    },
        { ".gif",  "image/gif"     },
        { ".webp", "image/webp"    },
        { ".bmp",  "image/bmp"     },
        { ".svg",  "image/svg+xml" },
        { ".ico",  "image/x-icon"  },
    };

    private static string InlineLocalImages(string markdown, string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return markdown;
        var dir = Path.GetDirectoryName(filePath);
        if (dir == null) return markdown;

        return _imgRegex.Replace(markdown, m =>
        {
            var alt  = m.Groups[1].Value;
            var path = m.Groups[2].Value;

            // Skip already-absolute or data: URIs.
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return m.Value;

            // Decode %xx in the path (Slack filenames occasionally have spaces encoded).
            var decoded = Uri.UnescapeDataString(path);
            var full = Path.GetFullPath(Path.Combine(dir, decoded));
            if (!File.Exists(full)) return m.Value;

            var ext = Path.GetExtension(full).ToLowerInvariant();
            if (!_mimeTypes.TryGetValue(ext, out var mime)) return m.Value;

            // Skip files whose content is actually an HTML auth-redirect page.
            if (IsLikelyHtmlFile(full)) return m.Value;

            try
            {
                var b64 = Convert.ToBase64String(File.ReadAllBytes(full));
                return $"![{alt}](data:{mime};base64,{b64})";
            }
            catch
            {
                return m.Value;
            }
        });
    }

    private static bool IsLikelyHtmlFile(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[9];
            var n = f.Read(buf, 0, buf.Length);
            var prefix = Encoding.ASCII.GetString(buf, 0, n).ToLowerInvariant();
            return prefix.StartsWith("<!doctype") || prefix.StartsWith("<html");
        }
        catch { return false; }
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

    // Opens the system print dialog (printer picker + Microsoft Print to PDF).
    // Prints the rendered HTML preview at full fidelity.
    public void ShowPrintUI()
    {
        if (_webViewReady)
            WebView.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System);
    }

    // --- Copy ---

    // Shortcuts pressed inside the WebView post a message back here, since
    // WebView2 doesn't surface key events to WPF KeyBindings.
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (!doc.RootElement.TryGetProperty("type", out var t))
                return;

            var type = t.GetString();
            if (type == "context-info")
            {
                _lastContextInfo = JsonSerializer.Deserialize<ContextTargetInfo>(e.WebMessageAsJson!);
            }
            else if (type == "copy-as-markdown")
            {
                CopyAsMarkdownRequested?.Invoke();
            }
            else if (type == "open-file-link")
            {
                if (doc.RootElement.TryGetProperty("href", out var hrefEl))
                {
                    var href = hrefEl.GetString();
                    if (!string.IsNullOrEmpty(href))
                    {
                        var localPath = href;
                        try
                        {
                            if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.IsFile)
                                localPath = uri.LocalPath + uri.Fragment;
                        }
                        catch { }

                        // Resolve relative paths against the markdown file's directory
                        if (!Path.IsPathRooted(localPath) && !string.IsNullOrEmpty(_lastRenderedFilePath))
                        {
                            try
                            {
                                var dir = Path.GetDirectoryName(_lastRenderedFilePath);
                                if (dir != null)
                                    localPath = Path.GetFullPath(Path.Combine(dir, localPath));
                            }
                            catch { }
                        }

                        if (File.Exists(localPath))
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = localPath,
                                    UseShellExecute = true
                                });
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to open file: {ex.Message}");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnWebMessageReceived error: {ex.Message}");
        }
    }

    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        // Fire-and-forget on the UI thread. The deferral delays the context menu
        // until we complete it after the JS query round-trip.
        _ = BuildContextMenuAsync(args, deferral);
    }

    private async Task BuildContextMenuAsync(
        CoreWebView2ContextMenuRequestedEventArgs args,
        CoreWebView2Deferral deferral)
    {
        try
        {
            var env = WebView.CoreWebView2.Environment;
            var items = args.MenuItems;
            items.Clear();

            // Query window.__ctxInfo (set by our JS contextmenu listener).
            // The variable is already set by the time this handler runs,
            // so ExecuteScriptAsync returns with minimal delay.
            string? imageSrc = null;
            string? linkHref = null;
            try
            {
                var scriptResult = await WebView.CoreWebView2.ExecuteScriptAsync(
                    "JSON.stringify(window.__ctxInfo || {})");

                // ExecuteScriptAsync returns a JSON-encoded string.
                // The script itself produces JSON via JSON.stringify.
                // So scriptResult is "\"{\\"hasImage\\":true,...}\"" — a JSON string
                // containing another JSON string. We need to unwrap once.
                var innerJson = JsonSerializer.Deserialize<string>(scriptResult);
                if (!string.IsNullOrEmpty(innerJson))
                {
                    using var doc = JsonDocument.Parse(innerJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("hasImage", out var hi) && hi.ValueKind == JsonValueKind.True)
                        imageSrc = root.TryGetProperty("imageSrc", out var isrc) && isrc.ValueKind == JsonValueKind.String ? isrc.GetString() : null;
                    if (root.TryGetProperty("hasLink", out var hl) && hl.ValueKind == JsonValueKind.True)
                        linkHref = root.TryGetProperty("linkHref", out var lh) && lh.ValueKind == JsonValueKind.String ? lh.GetString() : null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[emMDee] __ctxInfo query: {ex.Message}");
            }

            // --- Save Image As... ---
            var hasSaveOption = false;
            if (!string.IsNullOrEmpty(imageSrc))
            {
                var saveImage = env.CreateContextMenuItem(
                    "Save Image As\u2026", null, CoreWebView2ContextMenuItemKind.Command);
                saveImage.CustomItemSelected += (_, _) => _ = SaveUriAsAsync(imageSrc, "image");
                items.Add(saveImage);
                hasSaveOption = true;
            }
            else if (args.ContextMenuTarget.Kind == CoreWebView2ContextMenuTargetKind.Image
                     && args.ContextMenuTarget.HasSourceUri)
            {
                var saveImage = env.CreateContextMenuItem(
                    "Save Image As\u2026", null, CoreWebView2ContextMenuItemKind.Command);
                saveImage.CustomItemSelected += (_, _) =>
                    _ = SaveUriAsAsync(args.ContextMenuTarget.SourceUri, "image");
                items.Add(saveImage);
                hasSaveOption = true;
            }

            // --- Save Link As... ---
            if (!hasSaveOption)
            {
                if (!string.IsNullOrEmpty(linkHref))
                {
                    var saveLink = env.CreateContextMenuItem(
                        "Save Link As\u2026", null, CoreWebView2ContextMenuItemKind.Command);
                    saveLink.CustomItemSelected += (_, _) => _ = SaveUriAsAsync(linkHref, "link");
                    items.Add(saveLink);
                    hasSaveOption = true;
                }
                else if (args.ContextMenuTarget.HasLinkUri)
                {
                    var saveLink = env.CreateContextMenuItem(
                        "Save Link As\u2026", null, CoreWebView2ContextMenuItemKind.Command);
                    saveLink.CustomItemSelected += (_, _) =>
                        _ = SaveUriAsAsync(args.ContextMenuTarget.LinkUri, "link");
                    items.Add(saveLink);
                    hasSaveOption = true;
                }
            }

            // Generic text items only make sense on plain text — skip them
            // when right-clicking an image or link.
            if (!hasSaveOption)
            {
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
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Shows a Save File dialog and saves the resource at <paramref name="uri"/>
    /// to the chosen path. Handles file://, data:, and http(s):// URIs.
    /// </summary>
    private async Task SaveUriAsAsync(string uri, string kind)
    {
        try
        {
            // Resolve the best candidate filename and determine how to get data
            string? suggestedName = null;
            byte[]? data = null;
            string? fileSource = null;

            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                // data: URI — typically an inlined image
                suggestedName = GuessDataUriFileName(uri);
                data = ParseDataUri(uri);
            }
            else
            {
                // Resolve relative paths against the markdown file's directory
                var resolvedUri = uri;
                if (!Uri.TryCreate(uri, UriKind.Absolute, out _)
                    && !string.IsNullOrEmpty(_lastRenderedFilePath))
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(_lastRenderedFilePath);
                        if (dir != null)
                            resolvedUri = Path.GetFullPath(Path.Combine(dir, resolvedUri));
                    }
                    catch { }
                }

                if (resolvedUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    || Path.IsPathRooted(resolvedUri))
                {
                    // Local file — extract the path and copy
                    string localPath;
                    if (resolvedUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var fileUri = new Uri(resolvedUri);
                            localPath = Uri.UnescapeDataString(fileUri.LocalPath);
                        }
                        catch { localPath = resolvedUri; }
                    }
                    else
                    {
                        localPath = resolvedUri;
                    }

                    if (File.Exists(localPath))
                    {
                        suggestedName = Path.GetFileName(localPath);
                        fileSource = localPath;
                    }
                }
                else if (Uri.TryCreate(resolvedUri, UriKind.Absolute, out var absUri)
                         && (absUri.Scheme == "http" || absUri.Scheme == "https"))
                {
                    // Remote URL — download it
                    suggestedName = Path.GetFileName(absUri.AbsolutePath);
                    if (string.IsNullOrEmpty(suggestedName))
                        suggestedName = "download";

                    try
                    {
                        using var httpClient = new HttpClient();
                        httpClient.Timeout = TimeSpan.FromSeconds(30);
                        data = await httpClient.GetByteArrayAsync(absUri);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[emMDee] Download failed: {ex.Message}");
                        return;
                    }
                }
            }

            // Fallback extension/name
            if (string.IsNullOrEmpty(suggestedName))
                suggestedName = kind == "image" ? "image.png" : "download";

            // Show Save File dialog
            var dialog = new SaveFileDialog
            {
                FileName = suggestedName,
                Title = kind == "image" ? "Save Image As" : "Save Link As",
                Filter = "All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
                return;

            var destPath = dialog.FileName;

            if (fileSource != null)
            {
                // Copy from local file
                File.Copy(fileSource, destPath, overwrite: true);
            }
            else if (data != null)
            {
                // Save downloaded/decoded bytes
                await File.WriteAllBytesAsync(destPath, data);
            }
            else if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                // ParseDataUri returned null — data URI may be invalid
                System.Diagnostics.Debug.WriteLine("[emMDee] Could not parse data URI for saving.");
                return;
            }
            else
            {
                // The source doesn't exist locally and isn't a data/remote URI
                System.Diagnostics.Debug.WriteLine($"[emMDee] SaveUriAsAsync: unsupported URI scheme: {uri}");
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[emMDee] SaveUriAsAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts raw bytes from a data: URI (e.g. data:image/png;base64,ABC123...).
    /// Returns null if parsing fails.
    /// </summary>
    private static byte[]? ParseDataUri(string uri)
    {
        // Format: data:[<mediatype>][;base64],<data>
        var commaIndex = uri.IndexOf(',');
        if (commaIndex < 0) return null;

        var header = uri.Substring(0, commaIndex);
        var payload = uri.Substring(commaIndex + 1);
        var isBase64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (isBase64)
                return Convert.FromBase64String(payload);
            else
                return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Guesses a file name from a data: URI based on its MIME type.
    /// </summary>
    private static string GuessDataUriFileName(string uri)
    {
        var commaIndex = uri.IndexOf(',');
        if (commaIndex < 0) return "image.png";

        var header = uri.Substring(0, commaIndex);
        // header like "data:image/png;base64"
        var parts = header.Split(';');
        if (parts.Length > 0)
        {
            var mimePart = parts[0]; // "data:image/png"
            var colonIdx = mimePart.IndexOf(':');
            if (colonIdx >= 0)
            {
                var mime = mimePart.Substring(colonIdx + 1); // "image/png"
                var ext = mime switch
                {
                    "image/png" => ".png",
                    "image/jpeg" => ".jpg",
                    "image/gif" => ".gif",
                    "image/webp" => ".webp",
                    "image/bmp" => ".bmp",
                    "image/svg+xml" => ".svg",
                    "image/x-icon" => ".ico",
                    "application/pdf" => ".pdf",
                    _ => "." + (mime.Contains('/') ? mime.Substring(mime.IndexOf('/') + 1) : "bin")
                };
                return "image" + ext;
            }
        }
        return "image.png";
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
