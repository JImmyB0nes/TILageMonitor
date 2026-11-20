using Xunit;

namespace TILageMonitor.Tests;

public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("2.1.0", "2.0.30", true)]
    [InlineData("2.0.30", "2.0.30", false)]
    [InlineData("2.1.0", "2.1.0-beta.1", true)]   // final supersedes beta
    [InlineData("2.0.31", "2.1.0-beta.1", false)] // older stable does not downgrade a beta
    [InlineData("v2.1.1", "2.1.0-beta.1", true)]
    public void IsNewer_handles_prereleases(string latest, string current, bool expected)
    {
        Assert.True(UpdateService.TryParseReleaseVersion(latest, out var l, out var lPre));
        Assert.True(UpdateService.TryParseReleaseVersion(current, out var c, out var cPre));
        Assert.Equal(expected, UpdateService.IsNewer(l, lPre, c, cPre));
    }
}
