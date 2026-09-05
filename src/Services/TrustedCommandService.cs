using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace emMDee.Services;

/// <summary>
/// Decides whether clicking a link in a document may launch an executable, and
/// remembers the answer.
///
/// A markdown link opens its target with the shell, which is the right behaviour
/// for a .png, a .pdf or an https:// address. For a .cmd, .exe or .ps1 it is code
/// execution from a single click on a document the viewer did not write, so those
/// targets ask first.
///
/// An approval covers either a FOLDER or a single FILE.
///
/// Folder is the default the dialog offers, because it matches the unit of work:
/// one review request holds several commands that all launch the same program at
/// different places, and asking per file put three questions in front of one
/// decision. A folder approval also covers commands added to that folder later —
/// which is the cost, and is why the dialog names the folder rather than implying
/// it.
///
/// A FILE approval additionally pins content: the record holds a SHA-256 of the
/// script as approved, so editing it, or dropping a different script at the same
/// path, asks again.
///
/// Stored beside the session as `%AppData%/emMDee/trusted-commands.json`, in its
/// own file so a session save can never drop it and so the owner can delete every
/// approval by deleting one file.
/// </summary>
public sealed class TrustedCommandService
{
    /// <summary>
    /// Extensions the shell will EXECUTE rather than open in a viewer. Anything
    /// not on this list keeps the old behaviour and opens without a prompt.
    /// </summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cmd", ".bat", ".exe", ".com", ".ps1", ".psm1", ".msi", ".msp",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".scr", ".lnk",
        ".pif", ".reg", ".hta", ".cpl", ".jar", ".py", ".pyw", ".sh",
    };

    private sealed class Approval
    {
        public string Path { get; set; } = string.Empty;

        /// <summary>Empty for a FOLDER approval; the file's digest for a file one.</summary>
        public string Sha256 { get; set; } = string.Empty;

        /// <summary>"folder" or "file". Absent in records written before folders existed.</summary>
        public string Kind { get; set; } = "file";

        public DateTime ApprovedUtc { get; set; }
    }

    private readonly string _storePath;
    private readonly Dictionary<string, Approval> _approvals = new(StringComparer.OrdinalIgnoreCase);

    public TrustedCommandService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDir = Path.Combine(appData, "emMDee");
        Directory.CreateDirectory(appDir);
        _storePath = Path.Combine(appDir, "trusted-commands.json");
        Load();
    }

    /// <summary>True when the shell would EXECUTE this target rather than display it.</summary>
    public static bool IsExecutable(string path)
    {
        try
        {
            return ExecutableExtensions.Contains(Path.GetExtension(path));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when this command may run without asking — because the folder it
    /// lives in was approved, or because this exact file with exactly this
    /// content was.
    /// </summary>
    public bool IsTrusted(string path)
    {
        var full = FullPath(path);
        if (full is null)
            return false;

        if (FolderOf(full) is string folder
            && _approvals.TryGetValue(folder, out var folderApproval)
            && folderApproval.Kind == "folder")
        {
            return true;
        }

        if (!_approvals.TryGetValue(full, out var approval) || approval.Kind == "folder")
            return false;

        var digest = Digest(full);
        return digest is not null && string.Equals(digest, approval.Sha256, StringComparison.Ordinal);
    }

    /// <summary>The directory an approval would cover, or null when unreadable.</summary>
    public static string? FolderOf(string path)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Approve every command in this command's FOLDER.
    ///
    /// The folder is the unit the owner actually works in: a review request
    /// carries several commands that all launch the same program at different
    /// places, and approving them one at a time asked three questions to make one
    /// decision. The trade-off is stated plainly in the dialog - a folder
    /// approval covers files added to it later, where a file approval does not.
    /// </summary>
    public void TrustFolder(string path)
    {
        if (FolderOf(path) is not string folder)
            return;

        _approvals[folder] = new Approval
        {
            Path = folder,
            Kind = "folder",
            ApprovedUtc = DateTime.UtcNow,
        };
        Save();
    }

    /// <summary>Remember this one file, as it is right now, as approved to run.</summary>
    public void Trust(string path)
    {
        var full = FullPath(path);
        var digest = full is null ? null : Digest(full);
        if (full is null || digest is null)
            return;

        _approvals[full] = new Approval
        {
            Path = full,
            Sha256 = digest,
            Kind = "file",
            ApprovedUtc = DateTime.UtcNow,
        };
        Save();
    }

    private static string? FullPath(string path)
    {
        try
        {
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? Digest(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            // Unreadable counts as "not the file I approved".
            return null;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath))
                return;

            var stored = JsonSerializer.Deserialize<List<Approval>>(File.ReadAllText(_storePath));
            foreach (var approval in stored ?? new List<Approval>())
            {
                if (!string.IsNullOrEmpty(approval.Path))
                    _approvals[approval.Path] = approval;
            }
        }
        catch (Exception ex)
        {
            // A corrupt store means "nothing is approved", never "everything is".
            System.Diagnostics.Debug.WriteLine($"Failed to load trusted commands: {ex.Message}");
            _approvals.Clear();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(
                _storePath,
                JsonSerializer.Serialize(_approvals.Values.ToList(),
                                         new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save trusted commands: {ex.Message}");
        }
    }
}
