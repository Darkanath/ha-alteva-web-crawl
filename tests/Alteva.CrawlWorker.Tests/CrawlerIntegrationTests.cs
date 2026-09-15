using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Alteva.CrawlWorker.Services;
using Alteva.Domain.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alteva.CrawlWorker.Tests;

public class CrawlerIntegrationTests
{
    private class LocalHtmlFixtureHandler(Dictionary<string, string> fixtures) : HttpMessageHandler
    {
        private int _inFlight;

        public List<DateTime> RequestStartTimes { get; } = new();

        public int MaxInFlight { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestStartTimes.Add(DateTime.UtcNow);
            MaxInFlight = Math.Max(MaxInFlight, Interlocked.Increment(ref _inFlight));
            try
            {
                // Hold each request open briefly so overlapping downloads would be observable
                await Task.Delay(10, cancellationToken);

                var uri = request.RequestUri?.ToString() ?? string.Empty;
                if (fixtures.TryGetValue(uri, out var html))
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(html)
                    };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
                    return response;
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private static CrawlerEngine CreateEngine(Dictionary<string, string> fixtures, double delaySeconds = 0)
        => CreateEngine(new LocalHtmlFixtureHandler(fixtures), delaySeconds);

    private static CrawlerEngine CreateEngine(LocalHtmlFixtureHandler handler, double delaySeconds)
    {
        var httpClient = new HttpClient(handler);
        var urlNormalizer = new UrlNormalizer();
        var htmlExtractor = new HtmlLinkExtractor();
        var ratioCalculator = new DomainLinkRatioCalculator(urlNormalizer);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CRAWLER_DELAY_MIN_SECONDS"] = delaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CRAWLER_DELAY_MAX_SECONDS"] = delaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })
            .Build();

        return new CrawlerEngine(
            httpClient,
            urlNormalizer,
            htmlExtractor,
            ratioCalculator,
            configuration,
            NullLogger<CrawlerEngine>.Instance);
    }

    private static Dictionary<string, string> FanOutFixtures() => new()
    {
        ["https://mock-site.test/"] = @"<html><body>
            <a href=""/page1"">1</a><a href=""/page2"">2</a><a href=""/page3"">3</a>
        </body></html>",
        ["https://mock-site.test/page1"] = @"<html><body><a href=""/"">Home</a></body></html>",
        ["https://mock-site.test/page2"] = @"<html><body><a href=""/"">Home</a></body></html>",
        ["https://mock-site.test/page3"] = @"<html><body><a href=""/"">Home</a></body></html>"
    };

    [Fact]
    public async Task CrawlAsync_DownloadsPagesOneAtATime()
    {
        var handler = new LocalHtmlFixtureHandler(FanOutFixtures());
        var engine = CreateEngine(handler, delaySeconds: 0);

        var result = await engine.CrawlAsync(Guid.NewGuid(), "https://mock-site.test/", maxDepth: 1);

        result.Pages.Should().HaveCount(4);
        handler.MaxInFlight.Should().Be(1);
    }

    [Fact]
    public async Task CrawlAsync_WaitsPolitenessDelayBetweenDownloads()
    {
        var handler = new LocalHtmlFixtureHandler(FanOutFixtures());
        var engine = CreateEngine(handler, delaySeconds: 0.2);

        await engine.CrawlAsync(Guid.NewGuid(), "https://mock-site.test/", maxDepth: 1);

        handler.RequestStartTimes.Should().HaveCount(4);
        for (var i = 1; i < handler.RequestStartTimes.Count; i++)
        {
            // Small tolerance for timer resolution
            (handler.RequestStartTimes[i] - handler.RequestStartTimes[i - 1])
                .Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(180));
        }
    }

    [Fact]
    public async Task CrawlAsync_WithMultiLevelLocalHtmlFixtures_ShouldDiscoverPagesAndEdgesWithAccurateRatios()
    {
        // Setup local HTML test fixtures
        var fixtures = new Dictionary<string, string>
        {
            ["https://mock-site.test/"] = @"
                <html>
                <body>
                    <a href=""/about"">About Us</a>
                    <a href=""/contact"">Contact</a>
                    <a href=""https://external-partner.com"">Partner</a>
                </body>
                </html>",

            ["https://mock-site.test/about"] = @"
                <html>
                <body>
                    <a href=""/"">Home</a>
                    <a href=""/team"">Our Team</a>
                </body>
                </html>",

            ["https://mock-site.test/team"] = @"
                <html>
                <body>
                    <a href=""/about"">Back to About</a>
                    <a href=""https://twitter.com/mock"">Twitter</a>
                </body>
                </html>",

            ["https://mock-site.test/contact"] = @"
                <html>
                <body>
                    <a href=""mailto:info@mock-site.test"">Email</a>
                    <a href=""tel:+12345678"">Call</a>
                    <a href=""/"">Home</a>
                </body>
                </html>"
        };

        var engine = CreateEngine(fixtures);
        var jobId = Guid.NewGuid();

        // Act: Crawl with maxDepth = 2
        var result = await engine.CrawlAsync(jobId, "https://mock-site.test/", maxDepth: 2);

        // Assert
        result.Success.Should().BeTrue();
        result.Pages.Should().HaveCount(4); // root, /about, /contact, /team

        // Check root page metrics:
        // Outgoing links: /about (internal), /contact (internal), https://external-partner.com (external)
        // Ratio = 2 / 3 = 0.6667
        var rootPage = result.Pages.Find(p => p.Url == "https://mock-site.test/");
        rootPage.Should().NotBeNull();
        rootPage!.DomainLinkRatio.Should().Be(0.6667);

        // Check /contact page metrics:
        // Outgoing links: mailto (ignored), tel (ignored), / (internal)
        // Ratio = 1 / 1 = 1.0
        var contactPage = result.Pages.Find(p => p.Url == "https://mock-site.test/contact");
        contactPage.Should().NotBeNull();
        contactPage!.DomainLinkRatio.Should().Be(1.0);

        // Verify edges
        result.Edges.Should().Contain(e => e.ParentUrl == "https://mock-site.test/" && e.ChildUrl == "https://mock-site.test/about");
        result.Edges.Should().Contain(e => e.ParentUrl == "https://mock-site.test/" && e.ChildUrl == "https://external-partner.com/");
        result.Edges.Should().Contain(e => e.ParentUrl == "https://mock-site.test/about" && e.ChildUrl == "https://mock-site.test/team");
    }

    [Fact]
    public async Task CrawlAsync_WhenMaxPagesSafetyLimitIsExceeded_ShouldStopAtLimit()
    {
        var fixtures = new Dictionary<string, string>
        {
            ["https://mock-site.test/"] = @"
                <html>
                <body>
                    <a href=""/page1"">1</a>
                    <a href=""/page2"">2</a>
                    <a href=""/page3"">3</a>
                    <a href=""/page4"">4</a>
                </body>
                </html>",
            ["https://mock-site.test/page1"] = @"<html><body><a href=""/"">Home</a></body></html>",
            ["https://mock-site.test/page2"] = @"<html><body><a href=""/"">Home</a></body></html>",
            ["https://mock-site.test/page3"] = @"<html><body><a href=""/"">Home</a></body></html>",
            ["https://mock-site.test/page4"] = @"<html><body><a href=""/"">Home</a></body></html>"
        };

        var engine = CreateEngine(fixtures);
        var jobId = Guid.NewGuid();

        // Safety limit set to 2 pages maximum
        var result = await engine.CrawlAsync(jobId, "https://mock-site.test/", maxDepth: 2, maxPages: 2);

        result.Success.Should().BeTrue();
        result.Pages.Should().HaveCount(2);
    }

    [Fact]
    public async Task CrawlAsync_WhenRootUrlFails_ShouldReturnFailure()
    {
        // Empty fixtures -> Root URL returns 404
        var fixtures = new Dictionary<string, string>();
        var engine = CreateEngine(fixtures);
        var jobId = Guid.NewGuid();

        var result = await engine.CrawlAsync(jobId, "https://nonexistent.test/");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("HTTP status NotFound");
        result.Pages.Should().BeEmpty();
    }
}
