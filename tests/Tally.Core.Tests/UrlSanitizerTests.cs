using Tally.Core;
using Xunit;

namespace Tally.Core.Tests;

/// <summary>What an address bar hands us vs. what Tally stores: host/path, or nothing.</summary>
public class UrlSanitizerTests
{
    [Theory]
    [InlineData("https://lutz.halopsa.com/tickets?id=493876&view=all", "lutz.halopsa.com/tickets")]
    [InlineData("http://lutz.itglue.com/1234/passwords/", "lutz.itglue.com/1234/passwords")]
    [InlineData("lutz.halopsa.com/tickets", "lutz.halopsa.com/tickets")]        // Chrome hides the scheme
    [InlineData("HTTPS://Example.COM/Path", "example.com/Path")]                // host lowercased, path kept
    [InlineData("https://example.com/page#section", "example.com/page")]        // fragment stripped
    [InlineData("http://halo/tickets", "halo/tickets")]                         // intranet host, explicit scheme
    public void KeepsHostAndPath_DropsQueryAndFragment(string raw, string expected)
        => Assert.Equal(expected, UrlSanitizer.Sanitize(raw));

    [Theory]
    [InlineData("halopsa start date field")]     // a half-typed search, not a page
    [InlineData("settings")]                     // bare word — no dot, no scheme
    [InlineData("chrome://settings")]            // internal page
    [InlineData("file:///C:/report.html")]       // not the web
    [InlineData("")]
    [InlineData(null)]
    public void NonPages_ComeBackNull(string? raw)
        => Assert.Null(UrlSanitizer.Sanitize(raw));

    [Fact]
    public void AbsurdlyLongPaths_AreCapped()
    {
        var result = UrlSanitizer.Sanitize("https://example.com/" + new string('x', 500));

        Assert.NotNull(result);
        Assert.Equal(200, result!.Length);
    }
}
