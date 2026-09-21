using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class VersionNormalizationTests
{
    [Theory]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData("1.2.4", "1.2.4.0")]
    [InlineData("1.2.4.7", "1.2.4.7")]
    [InlineData("0.0", "0.0.0.0")]
    public void Canonical_pads_missing_components_with_zero(string input, string expected)
    {
        Version canonical = VersionNormalization.Canonical(Version.Parse(input));

        Assert.Equal(Version.Parse(expected), canonical);
        Assert.Equal(expected, canonical.ToString());
    }

    [Fact]
    public void Canonical_returns_the_same_instance_when_already_four_part()
    {
        var v = new Version(1, 2, 4, 0);

        Assert.Same(v, VersionNormalization.Canonical(v));
    }

    [Theory]
    [InlineData("1.2.4", true)]
    [InlineData("1.2.4.0", true)]
    [InlineData(" 1.2 ", true)]
    [InlineData("1", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("1.2-beta", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void TryParse_accepts_two_to_four_components_and_rejects_the_rest(string? text, bool expected)
    {
        bool ok = VersionNormalization.TryParse(text, out Version? version);

        Assert.Equal(expected, ok);
        Assert.Equal(expected, version is not null);
        if (ok)
        {
            Assert.True(VersionNormalization.IsCanonical(version!));
        }
    }

    [Fact]
    public void TryParse_result_equals_four_part_version_and_works_in_sets()
    {
        Assert.True(VersionNormalization.TryParse("1.2.4", out Version? parsed));

        Assert.Equal(new Version(1, 2, 4, 0), parsed);
        Assert.NotEqual(new Version(1, 2, 4), parsed);          // 这正是版本归一化要消灭的相等性陷阱
        Assert.Contains(new Version(1, 2, 4, 0), new HashSet<Version> { parsed! });
    }

    [Fact]
    public void IsCanonical_is_false_when_build_or_revision_is_undefined()
    {
        Assert.False(VersionNormalization.IsCanonical(new Version(1, 2)));
        Assert.False(VersionNormalization.IsCanonical(new Version(1, 2, 4)));
        Assert.True(VersionNormalization.IsCanonical(new Version(1, 2, 4, 0)));
        Assert.Equal(new Version(0, 0, 0, 0), VersionNormalization.Zero);
    }
}
