using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace emMDee.Services;

/// <summary>
/// Gives .md files opened with emMDee a distinct, thin "document" icon —
/// separate from the application's own icon used by the exe and shortcuts.
///
/// Windows resolves the file icon from the ProgId that .md is associated with.
/// The manual "Open with → emMDee" association uses the ProgId
/// <c>Applications\emMDee.exe</c>, which has no DefaultIcon, so Explorer falls
/// back to the exe's embedded icon. Writing a DefaultIcon under that ProgId
/// overrides only the file icon and leaves the exe/shortcut icon untouched.
/// </summary>
internal static class FileAssociationService
{
    private const string ProgIdKeyPath = @"Software\Classes\Applications\emMDee.exe\DefaultIcon";
    private const string EmbeddedIconName = "emMDee.Resources.md-document.ico";
    private const string IconFileName = "md-document.ico";

    /// <summary>
    /// Idempotent: extracts the document icon to LocalAppData (refreshing it
    /// when the embedded copy changes) and points the ProgId's DefaultIcon at
    /// it. Best-effort — any failure is swallowed so startup is never blocked.
    /// </summary>
    internal static void EnsureDocumentIcon()
    {
        try
        {
            var iconPath = ExtractIcon();
            if (iconPath == null) return;

            var iconValue = $"\"{iconPath}\",0";

            using var key = Registry.CurrentUser.CreateSubKey(ProgIdKeyPath);
            if (key?.GetValue(null) as string == iconValue) return;

            key?.SetValue(null, iconValue);
            NotifyShell();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EnsureDocumentIcon failed: {ex.Message}");
        }
    }

    // Writes the embedded .ico to %LOCALAPPDATA%\emMDee\md-document.ico,
    // rewriting only when contents differ. Returns the path, or null.
    private static string? ExtractIcon()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(EmbeddedIconName);
        if (stream == null)
        {
            System.Diagnostics.Debug.WriteLine($"Embedded icon '{EmbeddedIconName}' not found.");
            return null;
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "emMDee");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, IconFileName);

        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(path, bytes);

        return path;
    }

    // Tell Explorer to refresh associations so the new icon shows immediately.
    private static void NotifyShell() =>
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
