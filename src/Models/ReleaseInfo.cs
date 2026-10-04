using System.Text.Json;
using emMDee.Services;

namespace emMDee.Models;

/// <summary>
/// A GitHub release worth telling the user about. <see cref="Version"/> is the
/// normalised, comparable form ("1.0.5"); <see cref="TagName"/> is what GitHub
/// calls it ("v1.0.5") and is what the banner shows.
/// </summary>
public sealed record ReleaseInfo(string Version, string TagName, string ReleaseUrl)
{
    /// <summary>Fallback destination when a payload carries no <c>html_url</c>.</summary>
    public const string DefaultReleasesUrl = "https://github.com/foodak/emMDee/releases";

    /// <summary>
    /// Reads the two fields we need out of GitHub's <c>/releases/latest</c>
    /// payload. Returns null rather than throwing if the shape is not what we
    /// expect — a malformed response should never surface to the user.
    /// </summary>
    public static ReleaseInfo? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            var version = SemanticVersion.Normalize(tag);
            if (version == null)
                return null;

            var url = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                url = DefaultReleasesUrl;

            return new ReleaseInfo(version, tag.Trim(), url!);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
