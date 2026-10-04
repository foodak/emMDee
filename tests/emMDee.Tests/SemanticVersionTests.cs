using emMDee.Services;
using Xunit;

namespace emMDee.Tests;

/// <summary>
/// The comparison decides whether the user is nagged, so it has to be right in
/// both directions: never miss a real upgrade, and never invent one. Numeric
/// ordering is the whole point — string comparison puts "1.0.10" before "1.0.9".
/// </summary>
public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("v1.0.5", "1.0.4", true)]
    [InlineData("1.0.5", "1.0.4", true)]
    [InlineData("v1.0.10", "1.0.9", true)]   // numeric, not lexicographic
    [InlineData("v1.10.0", "v1.9.0", true)]
    [InlineData("v2.0.0", "1.99.99", true)]
    [InlineData("v1.0.4", "1.0.4", false)]   // equal
    [InlineData("v1.0.3", "1.0.4", false)]   // older
    [InlineData("v1.0", "1.0.0", false)]     // missing components read as zero
    [InlineData("v1.0.0", "1.0", false)]
    [InlineData("v0.9.0", "1.0", false)]
    public void IsNewer_compares_numerically(string candidate, string current, bool expected)
        => Assert.Equal(expected, SemanticVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("v1.x.0")]
    public void IsNewer_is_false_when_either_side_is_unparseable(string? candidate)
    {
        Assert.False(SemanticVersion.IsNewer(candidate, "1.0.4"));
        Assert.False(SemanticVersion.IsNewer("v1.0.5", candidate));
    }

    [Theory]
    [InlineData("v1.0.5", "1.0.5")]
    [InlineData("1.0.5", "1.0.5")]
    [InlineData("  v1.2.3  ", "1.2.3")]
    [InlineData("v1.0.5-beta.1", "1.0.5")]     // pre-release suffix ignored
    [InlineData("v1.0.5+build.7", "1.0.5")]    // build metadata ignored
    public void Normalize_strips_prefix_and_suffix(string input, string expected)
        => Assert.Equal(expected, SemanticVersion.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("nonsense")]
    public void Normalize_returns_null_when_there_is_no_version(string? input)
        => Assert.Null(SemanticVersion.Normalize(input));
}
