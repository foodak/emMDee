using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using emMDee.Models;

namespace emMDee.Services;

/// <summary>What a check produced.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>A newer release exists and should be surfaced.</summary>
    UpdateAvailable,

    /// <summary>Running version is current (or newer, as with a dev build).</summary>
    UpToDate,

    /// <summary>Nothing to report: throttled, unknown, or dismissed.</summary>
    Skipped,

    /// <summary>The check could not complete. Never shown for a background check.</summary>
    Failed
}

/// <summary>Result of a single check. <see cref="Error"/> is diagnostic only.</summary>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Release, string? Error = null);

/// <summary>
/// Asks GitHub for the newest release of emMDee and compares it against the
/// running build.
///
/// Design notes:
///  - Uses the public GitHub Releases API, so publishing a release is all the
///    server-side setup there is. No version file to host or keep in sync.
///  - At most one network request per <see cref="CheckInterval"/>; the answer is
///    cached on disk so a throttled start still shows a known update.
///  - This never throws and never blocks: the caller can fire it and forget it,
///    which is exactly what startup does. A user who is offline, behind a
///    proxy, or rate-limited simply sees nothing.
///  - Only a <em>successful</em> fetch advances the throttle, so a transient
///    failure is retried on the next launch rather than swallowing the next
///    24 hours.
/// </summary>
public sealed class UpdateCheckService
{
    private const string OwnerRepo = "foodak/emMDee";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/" + OwnerRepo + "/releases/latest";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient SharedHttp = CreateClient();

    private readonly HttpClient _http;
    private readonly string _stateFilePath;
    private readonly UpdateCheckState _state;

    public UpdateCheckService() : this(SharedHttp, DefaultStateFilePath())
    {
    }

    /// <summary>Test seam: run against a specific client and state file.</summary>
    public UpdateCheckService(HttpClient http, string stateFilePath)
    {
        _http = http;
        _stateFilePath = stateFilePath;
        _state = Load(_stateFilePath);
    }

    private static string DefaultStateFilePath()
    {
        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "emMDee");
        Directory.CreateDirectory(appDir);
        return Path.Combine(appDir, "update-check.json");
    }

    /// <summary>
    /// Looks for a newer release. <paramref name="force"/> skips the daily
    /// throttle and surfaces a previously dismissed version, which is what a
    /// menu-driven "check now" wants.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(
        string currentVersion, bool force = false, CancellationToken cancellationToken = default)
    {
        try
        {
            bool due = force || DateTimeOffset.UtcNow - _state.LastCheckedUtc >= CheckInterval;
            if (!due)
                return Evaluate(CachedRelease(), currentVersion, force: false);

            string json;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);

                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return Fallback(currentVersion, force, $"HTTP {(int)response.StatusCode}");

                json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Fallback(currentVersion, force, ex.Message);
            }

            var release = ReleaseInfo.TryParse(json);
            if (release == null)
                return Fallback(currentVersion, force, "Unrecognised release payload");

            _state.LastCheckedUtc = DateTimeOffset.UtcNow;
            _state.LatestVersion = release.Version;
            _state.LatestTagName = release.TagName;
            _state.LatestReleaseUrl = release.ReleaseUrl;
            Save(_stateFilePath, _state);

            return Evaluate(release, currentVersion, force);
        }
        catch (Exception ex)
        {
            // Absolute backstop. This is fired from startup and must never fault.
            Debug.WriteLine($"Update check failed: {ex.Message}");
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, null, ex.Message);
        }
    }

    /// <summary>Remember that the user does not want to hear about this version again.</summary>
    public void Dismiss(ReleaseInfo release)
    {
        _state.DismissedVersion = release.Version;
        Save(_stateFilePath, _state);
    }

    // A failed fetch still shows a cached newer release rather than pretending
    // we know nothing — the user benefits from yesterday's answer.
    private UpdateCheckResult Fallback(string currentVersion, bool force, string error)
    {
        var cached = Evaluate(CachedRelease(), currentVersion, force);
        return cached.Outcome == UpdateCheckOutcome.UpdateAvailable
            ? cached
            : new UpdateCheckResult(UpdateCheckOutcome.Failed, null, error);
    }

    private UpdateCheckResult Evaluate(ReleaseInfo? release, string currentVersion, bool force)
    {
        if (release == null)
            return new UpdateCheckResult(UpdateCheckOutcome.Skipped, null);

        if (!SemanticVersion.IsNewer(release.Version, currentVersion))
            return new UpdateCheckResult(UpdateCheckOutcome.UpToDate, release);

        // An explicit check overrides an earlier dismissal.
        if (!force && string.Equals(_state.DismissedVersion, release.Version, StringComparison.OrdinalIgnoreCase))
            return new UpdateCheckResult(UpdateCheckOutcome.Skipped, release);

        return new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, release);
    }

    private ReleaseInfo? CachedRelease()
    {
        if (string.IsNullOrWhiteSpace(_state.LatestVersion))
            return null;

        return new ReleaseInfo(
            _state.LatestVersion!,
            string.IsNullOrWhiteSpace(_state.LatestTagName) ? "v" + _state.LatestVersion : _state.LatestTagName!,
            string.IsNullOrWhiteSpace(_state.LatestReleaseUrl) ? ReleaseInfo.DefaultReleasesUrl : _state.LatestReleaseUrl!);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // Per-request CTS governs the deadline; a whole-client timeout would
            // also fire while a large body is streaming.
            Timeout = Timeout.InfiniteTimeSpan
        };

        // GitHub rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("emMDee (+https://github.com/foodak/emMDee)");
        return client;
    }

    private static UpdateCheckState Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new UpdateCheckState();

            return JsonSerializer.Deserialize<UpdateCheckState>(File.ReadAllText(path))
                   ?? new UpdateCheckState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load update-check state: {ex.Message}");
            return new UpdateCheckState();
        }
    }

    private static void Save(string path, UpdateCheckState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save update-check state: {ex.Message}");
        }
    }
}
