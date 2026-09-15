using Alteva.Domain.Services;
using FluentAssertions;
using Xunit;

namespace Alteva.Domain.UnitTests;

public class DomainLinkRatioCalculatorTests
{
    private readonly DomainLinkRatioCalculator _calculator = new(new UrlNormalizer());

    [Fact]
    public void Calculate_WhenAllLinksAreInternal_ShouldReturnOne()
    {
        var links = new[]
        {
            "https://example.com/page1",
            "https://example.com/page2",
            "https://blog.example.com/post1"
        };

        var ratio = _calculator.Calculate(links, "example.com");
        ratio.Should().Be(1.0);
    }

    [Fact]
    public void Calculate_WhenHalfOfLinksAreInternal_ShouldReturnHalf()
    {
        var links = new[]
        {
            "https://example.com/page1",
            "https://external.com/article"
        };

        var ratio = _calculator.Calculate(links, "example.com");
        ratio.Should().Be(0.5);
    }

    [Fact]
    public void Calculate_WhenNoLinksAreInternal_ShouldReturnZero()
    {
        var links = new[]
        {
            "https://google.com",
            "https://github.com",
            "https://twitter.com"
        };

        var ratio = _calculator.Calculate(links, "example.com");
        ratio.Should().Be(0.0);
    }

    [Fact]
    public void Calculate_WhenPageHasNoOutgoingLinks_ShouldReturnZero()
    {
        var links = Array.Empty<string>();

        var ratio = _calculator.Calculate(links, "example.com");
        ratio.Should().Be(0.0);
    }

    [Fact]
    public void Calculate_ShouldIgnoreNonHttpSchemesInCalculation()
    {
        // 1 internal link, 1 external link, and 2 ignored non-http schemes (mailto, tel)
        var links = new[]
        {
            "https://example.com/contact",
            "https://external.org/partner",
            "mailto:hello@example.com",
            "tel:+1234567890",
            "javascript:void(0)"
        };

        // Total valid outgoing links = 2 (contact + external)
        // Internal = 1 (contact)
        // Ratio = 1 / 2 = 0.5
        var ratio = _calculator.Calculate(links, "example.com");
        ratio.Should().Be(0.5);
    }

    [Fact]
    public void Calculate_WhenOnlyIgnoredSchemesExist_ShouldReturnZero()
    {
        var links = new[]
        {
            "mailto:contact@example.com",
            "tel:911"
        };

        var ratio = _calculator.Calculate(links, "example.com");
        ratio.Should().Be(0.0);
    }
}
