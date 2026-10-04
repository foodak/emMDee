using System.IO;
using System.Text.RegularExpressions;

namespace emMDee.Services;

/// <summary>
/// What a script provably launches, when it is provably nothing but a
/// forwarder. See <see cref="CommandScriptResolver.TryResolve"/>.
/// </summary>
public sealed class ResolvedForwarder
{
    /// <summary>Absolute paths of every program the script launches, in order.</summary>
    public IReadOnlyList<string> InvokedExecutables { get; }

    /// <summary>
    /// Environment variable names the script assigns before launching. Values
    /// are deliberately not recorded: the trusted program's behaviour with
    /// these variables chosen by a wrapper is what the owner approved.
    /// </summary>
    public IReadOnlySet<string> EnvVarsSet { get; }

    public ResolvedForwarder(IReadOnlyList<string> invokedExecutables, IReadOnlySet<string> envVarsSet)
    {
        InvokedExecutables = invokedExecutables;
        EnvVarsSet = envVarsSet;
    }
}

/// <summary>
/// Proves that a .cmd/.bat script is a pure FORWARDER — one that launches
/// other programs and does nothing else — and reports what it launches.
///
/// The grammar accepted is deliberately tiny, because the result gates a
/// security prompt. Anything this parser does not fully understand returns
/// null, and a null result means the caller asks the owner, exactly as it
/// did before the parser existed. Being wrong here only ever costs a prompt;
/// it must never be able to cost a silent launch.
///
/// Accepted, because none of it is code execution:
///   blank lines, rem/:: comments, @echo off, echo ..., pause, exit [/b N],
///   cd/chdir ..., choice ... (the system yes/no prompt utility),
///   set NAME=value, if [not] exist "..." ( ... ) blocks,
///   if [not] errorlevel N ( ... ) blocks and their single-line benign forms,
///   call "absolute-path" args...
///
/// Rejected, so the owner is asked:
///   % or ! expansion anywhere; & | &lt; &gt; ^ outside quotes; goto and labels;
///   for (its /f form executes commands); set /p, set /a, bare set;
///   relative or unquoted call targets; start, powershell, cmd /c and any
///   other bare command; single-line if ... ( ... ) blocks; else-adjacent
///   forms other than a bare ") else (".
/// </summary>
public static class CommandScriptResolver
{
    private static readonly HashSet<string> BatchExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cmd", ".bat",
    };

    /// <summary>
    /// Returns the programs <paramref name="scriptPath"/> launches, or null
    /// when the script is not a batch file or cannot be proved a pure
    /// forwarder.
    /// </summary>
    public static ResolvedForwarder? TryResolve(string scriptPath)
    {
        if (!BatchExtensions.Contains(Path.GetExtension(scriptPath)))
            return null;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(scriptPath);
        }
        catch
        {
            return null;
        }

        var invoked = new List<string>();
        var envVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            while (line.StartsWith('@'))
                line = line[1..].TrimStart();

            if (line.Length == 0)
                continue;

            if (!ParseLine(line, invoked, envVars, ref depth))
                return null;
        }

        return depth == 0 ? new ResolvedForwarder(invoked, envVars) : null;
    }

    private static bool ParseLine(string line, List<string> invoked, ISet<string> envVars, ref int depth)
    {
        if (HasExpansionOrOperator(line))
            return false;

        // Block closes. ") else (" reopens for the else branch; any other
        // content after a close is a form this parser will not guess at.
        if (line == ")")
        {
            if (depth == 0)
                return false;
            depth--;
            return true;
        }
        if (line == ") else (")
        {
            // Pop the if-branch, push the else-branch: net depth unchanged.
            if (depth == 0)
                return false;
            return true;
        }
        if (line.StartsWith(')'))
            return false;

        var lower = line.ToLowerInvariant();

        if (lower == "rem" || lower.StartsWith("rem ") || lower.StartsWith("::"))
            return true;

        // `echo` with any argument, including the `echo.` blank-line idiom.
        if (lower == "echo" || lower.StartsWith("echo ") || lower.StartsWith("echo."))
            return true;

        if (lower == "pause")
            return true;

        if (lower == "exit"
            || Regex.IsMatch(lower, @"^exit\s+/b\s+\d+$")
            || Regex.IsMatch(lower, @"^exit\s+\d+$"))
        {
            return true;
        }

        if (lower == "cd" || lower.StartsWith("cd ")
            || lower == "chdir" || lower.StartsWith("chdir "))
        {
            return true;
        }

        // The system choice.exe: prints a question and returns the key the
        // user pressed. No file access, no execution, nothing an attacker
        // could point at anything.
        if (lower == "choice" || lower.StartsWith("choice "))
            return true;

        if (lower.StartsWith("set "))
            return ParseSet(line[4..].Trim(), envVars);

        if (lower.StartsWith("call "))
            return ParseCall(line[5..].Trim(), invoked);

        if (lower.StartsWith("if "))
            return ParseIf(line[3..].Trim(), invoked, envVars, ref depth);

        // Everything else — bare commands, goto, labels, for, start, ... —
        // is unprovable and therefore asks.
        return false;
    }

    /// <summary>
    /// NAME=value assignments only, both bare and quoted. set /p, set /a and
    /// a bare set are rejected: /p reads input and a bare set prints state.
    /// </summary>
    private static bool ParseSet(string rest, ISet<string> envVars)
    {
        var body = rest;
        if (body.Length >= 2 && body[0] == '"' && body[^1] == '"')
            body = body[1..^1];

        var eq = body.IndexOf('=');
        if (eq <= 0)
            return false;

        var name = body[..eq];
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            return false;

        envVars.Add(name);
        return true;
    }

    /// <summary>
    /// A quoted, absolute target with arguments. The arguments must not carry
    /// quotes of their own — keeping them out keeps argument smuggling out.
    /// </summary>
    private static bool ParseCall(string rest, List<string> invoked)
    {
        var match = Regex.Match(rest, @"^""([^""]+)""(.*)$");
        if (!match.Success)
            return false;

        if (match.Groups[2].Value.Contains('"'))
            return false;

        var target = match.Groups[1].Value.Trim();
        if (!Path.IsPathRooted(target))
            return false;

        invoked.Add(Path.GetFullPath(target));
        return true;
    }

    private static bool ParseIf(string rest, List<string> invoked, ISet<string> envVars, ref int depth)
    {
        if (Regex.IsMatch(rest, @"^(not\s+)?exist\s+""[^""]*""\s*\($", RegexOptions.IgnoreCase)
            || Regex.IsMatch(rest, @"^(not\s+)?errorlevel\s+\d+\s*\($", RegexOptions.IgnoreCase))
        {
            depth++;
            return true;
        }

        // Single-line form: if [not] errorlevel N <benign command>. The
        // trailing command is parsed with the same rules, so it can only
        // ever be something already proven benign.
        var match = Regex.Match(rest, @"^(not\s+)?errorlevel\s+\d+\s+(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        var trailing = match.Groups[2].Value.Trim();
        if (trailing.Contains('(') || trailing.StartsWith(')'))
            return false;

        return ParseLine(trailing, invoked, envVars, ref depth);
    }

    /// <summary>
    /// True when the line contains anything that means "more code than this
    /// grammar understands". % and ! are expansions and are rejected even
    /// inside quotes, because cmd expands them before it honours the quotes;
    /// the operators are rejected only outside quotes, where they are
    /// chaining rather than text.
    /// </summary>
    private static bool HasExpansionOrOperator(string line)
    {
        if (line.Contains('%') || line.Contains('!'))
            return true;

        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && ch is '&' or '|' or '<' or '>' or '^')
                return true;
        }
        return false;
    }
}
