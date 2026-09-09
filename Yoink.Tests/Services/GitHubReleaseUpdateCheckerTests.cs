using System;
using Yoink.Services;

namespace Yoink.Tests.Services;

/// <summary>
/// Just <see cref="GitHubReleaseUpdateChecker.ParseVersion"/> — the one pure/isolable piece;
/// <see cref="GitHubReleaseUpdateChecker.CheckForUpdateAsync"/> itself talks to the real GitHub API,
/// so it isn't exercised here — same reasoning as <see cref="DependencyProvisioningServiceTests"/>
/// only covering that class's own pure parsing.
/// </summary>
public class GitHubReleaseUpdateCheckerTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")] // case-insensitive leading v
    [InlineData("1.2.3", "1.2.3")] // no leading v at all
    [InlineData("v1.2.3-beta.1", "1.2.3")] // a future prerelease suffix is stripped, not preserved
    [InlineData("v0.1.0", "0.1.0")]
    public void ParseVersion_ExtractsVersionFromTagName(string tagName, string expected)
    {
        var version = GitHubReleaseUpdateChecker.ParseVersion(tagName);

        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("vNaN")]
    public void ParseVersion_ReturnsNull_ForUnparseableTagName(string? tagName)
    {
        Assert.Null(GitHubReleaseUpdateChecker.ParseVersion(tagName));
    }
}
