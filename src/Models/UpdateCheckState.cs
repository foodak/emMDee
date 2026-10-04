namespace emMDee.Models;

/// <summary>
/// Persisted state for the update check, stored at
/// <c>%AppData%\emMDee\update-check.json</c>. Kept deliberately small: when we
/// last asked GitHub, what it told us, and which version the user asked never
/// to be told about again.
/// </summary>
public sealed class UpdateCheckState
{
    /// <summary>UTC time of the last <em>successful</em> fetch. Drives the once-a-day throttle.</summary>
    public DateTimeOffset LastCheckedUtc { get; set; }

    /// <summary>Normalised version of the newest known release ("1.0.5"), or null before the first successful check.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>The release's tag exactly as GitHub reports it ("v1.0.5"), for display.</summary>
    public string? LatestTagName { get; set; }

    /// <summary>Release page to send the user to.</summary>
    public string? LatestReleaseUrl { get; set; }

    /// <summary>Normalised version the user dismissed, so the banner does not return for it.</summary>
    public string? DismissedVersion { get; set; }
}
