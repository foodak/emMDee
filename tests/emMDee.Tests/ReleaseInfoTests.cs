using emMDee.Models;
using Xunit;

namespace emMDee.Tests;

/// <summary>
/// The GitHub payload is parsed defensively: a missing or unexpected field must
/// come back as null so the caller stays silent, not as an exception on the
/// startup path.
/// </summary>
public sealed class ReleaseInfoTests
{
    [Fact]
    public void TryParse_reads_tag_and_url()
    {
        const string json = """
            {
              "tag_name": "v1.0.5",
              "name": "v1.0.5",
              "html_url": "https://github.com/foodak/emMDee/releases/tag/v1.0.5",
              "prerelease": false
            }
            """;

        var release = ReleaseInfo.TryParse(json);

        Assert.NotNull(release);
        Assert.Equal("1.0.5", release!.Version);
        Assert.Equal("v1.0.5", release.TagName);
        Assert.Equal("https://github.com/foodak/emMDee/releases/tag/v1.0.5", release.ReleaseUrl);
    }

    [Fact]
    public void TryParse_falls_back_to_releases_page_when_url_is_absent()
    {
        var release = ReleaseInfo.TryParse("""{ "tag_name": "1.0.5" }""");

        Assert.NotNull(release);
        Assert.Equal("1.0.5", release!.Version);
        Assert.Equal(ReleaseInfo.DefaultReleasesUrl, release.ReleaseUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{ "tag_name": "" }""")]
    [InlineData("""{ "tag_name": "latest" }""")]
    public void TryParse_returns_null_for_unusable_payloads(string? json)
        => Assert.Null(ReleaseInfo.TryParse(json));
}
