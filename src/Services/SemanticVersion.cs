namespace emMDee.Services;

/// <summary>
/// Compares dotted numeric versions ("1.10.2") without pulling in a dependency.
/// Tolerates a leading "v" and any trailing pre-release or build suffix
/// ("1.0.5-beta", "1.0.5+9f3a2b"), which are ignored — GitHub's
/// <c>/releases/latest</c> endpoint excludes pre-releases, so a suffix only
/// shows up on the odd tag name.
/// </summary>
public static class SemanticVersion
{
    /// <summary>
    /// True when <paramref name="candidate"/> is strictly newer than
    /// <paramref name="current"/>. Returns false when either side cannot be
    /// parsed: an unreadable version must never produce an update prompt.
    /// </summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        var left = Parse(candidate);
        var right = Parse(current);
        if (left == null || right == null)
            return false;

        // Missing components count as zero, so "1.0" and "1.0.0" are equal.
        int length = Math.Max(left.Length, right.Length);
        for (int i = 0; i < length; i++)
        {
            int a = i < left.Length ? left[i] : 0;
            int b = i < right.Length ? right[i] : 0;
            if (a != b)
                return a > b;
        }

        return false;
    }

    /// <summary>
    /// Canonical comparable form: "v1.0.5-beta" becomes "1.0.5". Returns null
    /// when there is no number to read.
    /// </summary>
    public static string? Normalize(string? version)
        => Parse(version) is { } parts ? string.Join('.', parts) : null;

    private static int[]? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var text = version.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        // Read the leading dotted-numeric run and stop at the first suffix char.
        int end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'))
            end++;

        // Past the numbers, only a pre-release ("-beta") or build ("+sha") marker is
        // acceptable. Anything else means this is not a version we understand, such
        // as a "1.x.0" placeholder — reading that as "1" would fake an upgrade.
        if (end < text.Length && text[end] != '-' && text[end] != '+')
            return null;

        text = text[..end].Trim('.');

        if (text.Length == 0)
            return null;

        var segments = text.Split('.');
        var numbers = new int[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], out numbers[i]) || numbers[i] < 0)
                return null;
        }

        return numbers;
    }
}
