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
/// path, asks again. A file approval also pins which environment variables a
/// wrapper may set for the program (see <see cref="Approval.EnvVars"/>).
///
/// A .cmd/.bat that provably does nothing but LAUNCH other programs is covered
/// by THEIR approvals instead of its own: <see cref="CommandScriptResolver"/>
/// reads the wrapper, and when every program it launches is already approved,
/// the wrapper runs without asking. A review workflow that writes a fresh
/// wrapper per document — same program, new folder, new arguments — therefore
/// asks once for the program itself and then never again, while a wrapper the
/// resolver cannot prove stays as it always was and asks on its own.
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

        /// <summary>
        /// For a file approval: the environment variables a wrapper may set for
        /// this program and still count as "the command you approved". Null in
        /// folder approvals and in records written before wrappers existed, and
        /// treated as an empty set — a wrapper that sets anything asks again
        /// once, and the new answer records its variables.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? EnvVars { get; set; }

        public DateTime ApprovedUtc { get; set; }
    }

    private readonly string _storePath;
    private readonly Dictionary<string, Approval> _approvals = new(StringComparer.OrdinalIgnoreCase);

    public TrustedCommandService(string? storePath = null)
    {
        if (storePath is null)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appDir = Path.Combine(appData, "emMDee");
            Directory.CreateDirectory(appDir);
            _storePath = Path.Combine(appDir, "trusted-commands.json");
        }
        else
        {
            // Tests point the store at a temp file; the app never does.
            _storePath = storePath;
            var dir = Path.GetDirectoryName(storePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
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
    /// lives in was approved, because this exact file with exactly this content
    /// was, or because it is a wrapper that provably only launches programs
    /// that were.
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

        if (_approvals.TryGetValue(full, out var approval)
            && approval.Kind == "file"
            && DigestMatches(full, approval))
        {
            return true;
        }

        // A wrapper that provably only launches other programs inherits THEIR
        // approval. One level deep on purpose: the remembered unit is the
        // program the owner actually approved, and nothing the resolver cannot
        // prove is ever covered by this path.
        if (CommandScriptResolver.TryResolve(full) is ResolvedForwarder forwarder)
        {
            var targets = forwarder.InvokedExecutables
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return targets.Count > 0 && targets.All(t => IsApprovedTarget(t, forwarder.EnvVarsSet));
        }

        return false;
    }

    /// <summary>
    /// True when a program a wrapper launches is approved to run with the
    /// environment the wrapper prepares for it.
    ///
    /// A folder approval covers the program no matter what the wrapper says
    /// first — that is the broad trust the folder option was sold as. A file
    /// approval only covers it when the wrapper sets no variable beyond the
    /// ones recorded with the approval, so a later wrapper cannot smuggle new
    /// environment into a remembered decision.
    /// </summary>
    private bool IsApprovedTarget(string target, IReadOnlySet<string> envVarsSet)
    {
        var full = FullPath(target);
        if (full is null)
            return false;

        if (FolderOf(full) is string folder
            && _approvals.TryGetValue(folder, out var folderApproval)
            && folderApproval.Kind == "folder")
        {
            return true;
        }

        if (!_approvals.TryGetValue(full, out var approval)
            || approval.Kind != "file"
            || !DigestMatches(full, approval))
        {
            return false;
        }

        var allowed = approval.EnvVars is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(approval.EnvVars, StringComparer.OrdinalIgnoreCase);
        return envVarsSet.IsSubsetOf(allowed);
    }

    private static bool DigestMatches(string full, Approval approval)
    {
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
    public void Trust(string path) => Trust(path, null);

    /// <summary>
    /// Remember this one file, as it is right now, as approved to run, plus
    /// the environment variables a wrapper may set for it (the wrapper's own
    /// assignments at approval time). Pass null when the file itself was
    /// approved directly rather than through a wrapper.
    /// </summary>
    public void Trust(string path, IReadOnlySet<string>? envVars)
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
            EnvVars = envVars is null || envVars.Count == 0
                ? null
                : envVars.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
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
