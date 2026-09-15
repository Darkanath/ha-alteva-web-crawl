using System;
using Alteva.Domain.Entities;
using Alteva.Domain.Services;
using FluentAssertions;
using Xunit;

namespace Alteva.Domain.UnitTests;

public class UrlHasherTests
{
    [Fact]
    public void Hash_MatchesSha256OfUtf16LeBytes_AsSqlServerHashBytesOnNvarchar()
    {
        // Reference value: SHA-256 over UTF-16LE bytes, i.e. HASHBYTES('SHA2_256', N'https://example.com/About')
        var hash = UrlHasher.Hash("https://example.com/About");

        Convert.ToHexString(hash).Should().Be("8DAB519DF56D493F94206FF6499489001926D49B4F350711EA6C7047E954A971");
        hash.Should().HaveCount(UrlHasher.HashLength);
    }

    [Fact]
    public void Hash_IsCaseSensitive()
    {
        UrlHasher.Hash("https://example.com/About")
            .Should().NotEqual(UrlHasher.Hash("https://example.com/about"));
    }

    [Fact]
    public void Page_KeepsUrlHashInSyncWithUrl()
    {
        var page = new Page { Url = "https://example.com/a" };
        page.UrlHash.Should().Equal(UrlHasher.Hash("https://example.com/a"));

        page.Url = "https://example.com/b";
        page.UrlHash.Should().Equal(UrlHasher.Hash("https://example.com/b"));
    }
}
