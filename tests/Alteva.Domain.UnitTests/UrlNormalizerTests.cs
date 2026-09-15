using Alteva.Domain.Services;
using FluentAssertions;
using Xunit;

namespace Alteva.Domain.UnitTests;

public class UrlNormalizerTests
{
    private readonly UrlNormalizer _normalizer = new();

    [Theory]
    [InlineData("https://example.com/about#team", "https://example.com/about")]
    [InlineData("https://example.com/products/#overview", "https://example.com/products")]
    [InlineData("https://example.com/#top", "https://example.com/")]
    [InlineData("https://example.com/search?q=test#page2", "https://example.com/search?q=test")]
    public void Normalize_ShouldStripFragments(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("/about", "https://example.com", "https://example.com/about")]
    [InlineData("team", "https://example.com/about/", "https://example.com/about/team")]
    [InlineData("../contact", "https://example.com/services/consulting", "https://example.com/contact")]
    [InlineData("?sort=asc", "https://example.com/items", "https://example.com/items?sort=asc")]
    public void Normalize_ShouldResolveRelativeUrls(string relativeUrl, string baseUrl, string expected)
    {
        var result = _normalizer.Normalize(relativeUrl, baseUrl);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("HTTP://EXAMPLE.COM/Path", "http://example.com/Path")]
    [InlineData("https://Www.Sub.Domain.Com/", "https://www.sub.domain.com/")]
    public void Normalize_ShouldLowercaseSchemeAndHost(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("mailto:support@example.com")]
    [InlineData("tel:+18005551234")]
    [InlineData("javascript:alert('xss')")]
    [InlineData("javascript:void(0);")]
    [InlineData("data:text/plain;base64,SGVsbG8=")]
    [InlineData("ftp://ftp.example.com/file.zip")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null!)]
    public void Normalize_ShouldReturnNullForNonHttpSchemesAndEmptyInputs(string? input)
    {
        var result = _normalizer.Normalize(input!);
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("https://example.com:443/page", "https://example.com/page")]
    [InlineData("http://example.com:80/page", "http://example.com/page")]
    [InlineData("https://example.com:8443/page", "https://example.com:8443/page")]
    public void Normalize_ShouldStripDefaultPorts(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        result.Should().Be(expected);
    }

    [Fact]
    public void ExtractHost_ShouldReturnNormalizedHost()
    {
        _normalizer.ExtractHost("https://WWW.Example.COM:8080/path?q=1").Should().Be("www.example.com");
    }

    [Theory]
    [InlineData("https://example.com/page1", "example.com", true)]
    [InlineData("https://blog.example.com/page1", "example.com", true)]
    [InlineData("https://api.v1.example.com/doc", "example.com", true)]
    [InlineData("https://otherdomain.com/page1", "example.com", false)]
    [InlineData("https://notexample.com/page1", "example.com", false)]
    public void IsSameDomain_ShouldCorrectlyIdentifySameDomainAndSubdomains(string candidateUrl, string targetHost, bool expected)
    {
        var result = _normalizer.IsSameDomain(candidateUrl, targetHost);
        result.Should().Be(expected);
    }
}
