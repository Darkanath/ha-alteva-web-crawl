using Alteva.Domain.Services;
using FluentAssertions;
using Xunit;

namespace Alteva.CrawlWorker.Tests;

public class HtmlLinkExtractorTests
{
    private readonly HtmlLinkExtractor _extractor = new();

    [Fact]
    public void ExtractLinks_WhenGivenHtmlWithLinks_ShouldExtractAllHrefs()
    {
        var html = @"
            <!DOCTYPE html>
            <html>
            <head><title>Test Page</title></head>
            <body>
                <h1>Welcome</h1>
                <p>Check our <a href=""https://example.com/about"">About</a> page.</p>
                <p>Or see <a class='button' href='/contact'>Contact Us</a>.</p>
                <div>
                    <a href=""https://external.com/partner"" target=""_blank"">Partner</a>
                    <a name=""anchor-without-href"">No href</a>
                </div>
            </body>
            </html>";

        var links = _extractor.ExtractLinks(html);

        links.Should().HaveCount(3);
        links.Should().Contain("https://example.com/about");
        links.Should().Contain("/contact");
        links.Should().Contain("https://external.com/partner");
    }

    [Fact]
    public void ExtractLinks_WithNoAnchorTags_ShouldReturnEmpty()
    {
        var html = "<div><p>No links here!</p></div>";
        var links = _extractor.ExtractLinks(html);
        links.Should().BeEmpty();
    }

    [Fact]
    public void ExtractLinks_WithEmptyOrNull_ShouldReturnEmpty()
    {
        _extractor.ExtractLinks("").Should().BeEmpty();
        _extractor.ExtractLinks(null!).Should().BeEmpty();
    }
}
