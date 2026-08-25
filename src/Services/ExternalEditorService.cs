using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace emMDee.Services;

/// <summary>
/// Opens the raw markdown source of a file in the user's default text editor
/// (the application associated with .txt files on this machine) so the user can
/// edit it outside emMDee. emMDee's file watcher picks up the saved changes and
/// offers a reload.
/// </summary>
internal static class ExternalEditorService
{
    /// <summary>
    /// Opens <paramref name="filePath"/> in the default text editor. The editor
    /// is resolved from the shell's .txt file association, with Notepad as a
    /// fallback when no handler is registered or the handler fails to launch.
    /// Returns false when no path was given or the editor could not be launched;
    /// the exception is logged and the caller decides how to surface it.
    /// </summary>
    internal static bool OpenInTextEditor(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        try
        {
            var quotedPath = $"\"{filePath}\"";

            var editor = ResolveTextEditorExecutable();
            if (!string.IsNullOrEmpty(editor))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = editor,
                        Arguments = quotedPath,
                        UseShellExecute = true
                    });
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[emMDee] Default editor launch failed, falling back to Notepad: {ex.Message}");
                }
            }

            // Fallback: the text editor built into Windows.
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = quotedPath,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[emMDee] OpenInTextEditor failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Asks the shell which executable handles .txt files (the OS text editor
    /// association). Returns null when it can't be resolved.
    /// </summary>
    private static string? ResolveTextEditorExecutable()
    {
        try
        {
            // First call: get the required buffer size (including null terminator).
            uint size = 0;
            AssocQueryString(AssocF.None, AssocStr.Executable, ".txt", null, null, ref size);
            if (size == 0)
                return null;

            var buffer = new StringBuilder((int)size);
            if (AssocQueryString(AssocF.None, AssocStr.Executable, ".txt", null, buffer, ref size) != S_OK)
                return null;

            var executable = buffer.ToString();
            return string.IsNullOrWhiteSpace(executable) ? null : executable;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[emMDee] ResolveTextEditorExecutable failed: {ex.Message}");
            return null;
        }
    }

    [Flags]
    private enum AssocF
    {
        None = 0
    }

    private enum AssocStr
    {
        Command = 1,
        Executable = 2,
        FriendlyAppName = 4
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(
        AssocF flags,
        AssocStr str,
        string pszAssoc,
        string? pszExtra,
        StringBuilder? pszOut,
        ref uint pcchOut);

    private const int S_OK = 0;
}
