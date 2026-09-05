using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace emMDee.Services;

/// <summary>
/// Writes the value of a single HTML form control back into the markdown file it was
/// rendered from — and nothing else.
///
/// emMDee is a viewer. The one exception is a control that opts in by carrying a
/// <c>data-answer="&lt;id&gt;"</c> attribute: those exist so a generated document (a review
/// request, a checklist, a form) can be answered in the same file it is read in.
///
/// The guard rails are the point of this class:
///  - A write happens ONLY for a control carrying <c>data-answer</c>. A file with no such
///    attribute has no write path at all, so ordinary markdown can never be touched.
///  - Only the control's own value is rewritten — the inner text of a
///    <c>&lt;textarea&gt;</c>, the <c>value</c>/<c>checked</c> attribute of an
///    <c>&lt;input&gt;</c>, the <c>selected</c> attribute of a <c>&lt;select&gt;</c>'s
///    options. Prose is never rewritten, and neither is any control the edit did not name.
///  - If the id is not found, or is found more than once, the write is REFUSED and the file
///    is left alone. An ambiguous document is a bug in the document, not a licence to guess.
///  - The save is atomic: a sibling temp file is written and flushed, then moved over the
///    original, so an interrupted write cannot truncate the answers already given.
/// </summary>
public static class AnswerFieldService
{
    /// <summary>Outcome of rewriting one field. <paramref name="Markdown"/> is null when refused.</summary>
    public readonly record struct ApplyResult(bool Applied, string? Markdown, string Reason)
    {
        public static ApplyResult Refused(string reason) => new(false, null, reason);
        public static ApplyResult Ok(string markdown) => new(true, markdown, "ok");
    }

    /// <summary>
    /// Returns <paramref name="markdown"/> with the control identified by <paramref name="id"/>
    /// carrying <paramref name="value"/>, or a refusal explaining why nothing was changed.
    /// </summary>
    /// <param name="kind">"textarea", "text", "checkbox" or "select" — as reported by the page.</param>
    public static ApplyResult Apply(string markdown, string id, string kind, string value)
    {
        if (string.IsNullOrEmpty(markdown) || string.IsNullOrEmpty(id))
            return ApplyResult.Refused("empty document or id");

        // An id is written by us into a generated document; keep it to characters that
        // cannot change the meaning of the regex or of the HTML it is spliced into.
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9_.:-]{1,64}$"))
            return ApplyResult.Refused($"id '{id}' is not a plain identifier");

        var marker = new Regex(
            $@"data-answer\s*=\s*""{Regex.Escape(id)}""",
            RegexOptions.IgnoreCase);

        var hits = marker.Matches(markdown);
        if (hits.Count == 0)
            return ApplyResult.Refused($"no control carries data-answer=\"{id}\"");
        if (hits.Count > 1)
            return ApplyResult.Refused($"data-answer=\"{id}\" appears {hits.Count} times; refusing to guess");

        return kind switch
        {
            "textarea" => ApplyTextarea(markdown, id, value),
            "text"     => ApplyInputValue(markdown, id, value),
            "checkbox" => ApplyCheckbox(markdown, id, value == "true"),
            "select"   => ApplySelect(markdown, id, value),
            _          => ApplyResult.Refused($"unsupported control kind '{kind}'"),
        };
    }

    // --- per-kind rewrites ------------------------------------------------

    private static ApplyResult ApplyTextarea(string markdown, string id, string value)
    {
        var re = new Regex(
            $@"(<textarea\b[^>]*data-answer\s*=\s*""{Regex.Escape(id)}""[^>]*>)(.*?)(</textarea\s*>)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var m = re.Match(markdown);
        if (!m.Success)
            return ApplyResult.Refused($"textarea data-answer=\"{id}\" is not closed");

        // A textarea's content is text, and a leading newline immediately after the open tag
        // is swallowed by the HTML parser. Emit one deliberately so a multi-line answer round
        // trips through render -> edit -> render unchanged.
        var body = "\n" + EscapeText(value);
        return ApplyResult.Ok(markdown[..m.Index] + m.Groups[1].Value + body + m.Groups[3].Value
                              + markdown[(m.Index + m.Length)..]);
    }

    private static ApplyResult ApplyInputValue(string markdown, string id, string value)
    {
        return RewriteTag(markdown, "input", id, tag =>
        {
            var withoutValue = Regex.Replace(tag, @"\s+value\s*=\s*""[^""]*""", string.Empty, RegexOptions.IgnoreCase);
            return InsertAttribute(withoutValue, $" value=\"{EscapeAttribute(value)}\"");
        });
    }

    private static ApplyResult ApplyCheckbox(string markdown, string id, bool ticked)
    {
        return RewriteTag(markdown, "input", id, tag =>
        {
            // Drop any existing checked (bare or valued), then re-add when ticked.
            var bare = Regex.Replace(tag, @"\s+checked(\s*=\s*""[^""]*"")?", string.Empty, RegexOptions.IgnoreCase);
            return ticked ? InsertAttribute(bare, " checked") : bare;
        });
    }

    private static ApplyResult ApplySelect(string markdown, string id, string value)
    {
        var re = new Regex(
            $@"(<select\b[^>]*data-answer\s*=\s*""{Regex.Escape(id)}""[^>]*>)(.*?)(</select\s*>)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var m = re.Match(markdown);
        if (!m.Success)
            return ApplyResult.Refused($"select data-answer=\"{id}\" is not closed");

        var optionRe = new Regex(@"<option\b([^>]*)>(.*?)</option\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var matched = false;
        var body = optionRe.Replace(m.Groups[2].Value, om =>
        {
            var attrs = Regex.Replace(om.Groups[1].Value, @"\s+selected(\s*=\s*""[^""]*"")?",
                string.Empty, RegexOptions.IgnoreCase);
            var text = om.Groups[2].Value.Trim();

            // The page reports the option's value, which HTML defines as the explicit
            // value attribute when present and the option's text when not.
            var explicitValue = Regex.Match(attrs, @"value\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
            var optionValue = explicitValue.Success ? explicitValue.Groups[1].Value : UnescapeText(text);

            if (!matched && string.Equals(optionValue, value, StringComparison.Ordinal))
            {
                matched = true;
                attrs += " selected";
            }
            return $"<option{attrs}>{om.Groups[2].Value}</option>";
        });

        if (!matched)
            return ApplyResult.Refused($"select data-answer=\"{id}\" has no option '{value}'");

        return ApplyResult.Ok(markdown[..m.Index] + m.Groups[1].Value + body + m.Groups[3].Value
                              + markdown[(m.Index + m.Length)..]);
    }

    // --- shared tag surgery -----------------------------------------------

    /// <summary>Applies <paramref name="rewrite"/> to the single self-contained tag carrying the id.</summary>
    private static ApplyResult RewriteTag(string markdown, string tagName, string id, Func<string, string> rewrite)
    {
        var re = new Regex(
            $@"<{tagName}\b[^>]*data-answer\s*=\s*""{Regex.Escape(id)}""[^>]*>",
            RegexOptions.IgnoreCase);

        var m = re.Match(markdown);
        if (!m.Success)
            return ApplyResult.Refused($"<{tagName}> with data-answer=\"{id}\" not found");

        return ApplyResult.Ok(markdown[..m.Index] + rewrite(m.Value) + markdown[(m.Index + m.Length)..]);
    }

    /// <summary>Inserts an attribute just before the tag's closing bracket, honouring "/&gt;".</summary>
    private static string InsertAttribute(string tag, string attribute)
    {
        var end = tag.EndsWith("/>", StringComparison.Ordinal) ? tag.Length - 2 : tag.Length - 1;
        return tag[..end].TrimEnd() + attribute + tag[end..];
    }

    private static string EscapeText(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string UnescapeText(string s) =>
        s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    private static string EscapeAttribute(string s) => EscapeText(s).Replace("\"", "&quot;");

    // --- saving -------------------------------------------------------------

    /// <summary>
    /// Writes <paramref name="markdown"/> over <paramref name="filePath"/> atomically,
    /// preserving the file's existing newline convention. Returns false with a reason
    /// rather than throwing.
    /// </summary>
    public static bool Save(string filePath, string markdown, out string reason)
    {
        reason = "ok";
        try
        {
            markdown = MatchNewlines(filePath, markdown);
            var temp = filePath + ".emmdee-tmp";
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write(markdown);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // Move overwrites in one operation, so a reader never sees a half-written file.
            File.Move(temp, filePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// A rewritten answer carries the browser's "\n" newlines. Bring the whole document
    /// back to the convention the file on disk already used, so answering a question does
    /// not show up as a whole-file diff.
    /// </summary>
    private static string MatchNewlines(string filePath, string markdown)
    {
        bool crlf;
        try
        {
            var existing = File.ReadAllText(filePath);
            var crlfCount = Regex.Matches(existing, "\r\n").Count;
            var lfCount = existing.Count(c => c == '\n');
            crlf = crlfCount > 0 && crlfCount >= lfCount - crlfCount;
        }
        catch
        {
            return markdown;
        }

        var normalized = markdown.Replace("\r\n", "\n");
        return crlf ? normalized.Replace("\n", "\r\n") : normalized;
    }

    /// <summary>True when the document contains at least one opted-in control.</summary>
    public static bool HasAnswerFields(string markdown) =>
        !string.IsNullOrEmpty(markdown) && markdown.Contains("data-answer", StringComparison.OrdinalIgnoreCase);
}
