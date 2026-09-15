using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Alteva.CrawlWorker.Services;
using Alteva.Domain.Entities;
using Alteva.Domain.Models;
using Alteva.Domain.Services;
using Alteva.Infrastructure.Data;
using Alteva.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alteva.CrawlWorker.Tests;

public class PageCrawlHandlerTests : IDisposable
{
    private const string Root = "https://mock-site.test/";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly FakeQueue _queue = new();

    public PageCrawlHandlerTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite(_connection))
            .AddScoped<ICrawlStateStore, CrawlStateStore>()
            .BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    // ---------- Test doubles ----------

    /// <summary>Stands in for the RabbitMQ queue: published messages are consumed FIFO.</summary>
    private sealed class FakeQueue : IMessagePublisher
    {
        public ConcurrentQueue<CrawlPageMessage> Messages { get; } = new();
        public List<CrawlPageMessage> Published { get; } = new();

        public Task PublishAsync<T>(T message, string? routingKey = null, CancellationToken cancellationToken = default)
        {
            var page = (CrawlPageMessage)(object)message!;
            Published.Add(page);
            Messages.Enqueue(page);
            return Task.CompletedTask;
        }
    }

    private sealed class FixtureSite(Dictionary<string, string> pages) : HttpMessageHandler
    {
        private int _inFlight;

        public List<(string Url, DateTime StartedAt)> Requests { get; } = new();
        public int MaxInFlight { get; private set; }
        public HashSet<string> Hanging { get; } = new();
        public Dictionary<string, string> NonHtml { get; } = new();

        /// <summary>Failures served for a URL before its real content: an HTTP status (optionally with Retry-After seconds), or null for a network error.</summary>
        public Dictionary<string, Queue<(HttpStatusCode? Status, int? RetryAfterSeconds)>> Failures { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add((url, DateTime.UtcNow));
            }

            MaxInFlight = Math.Max(MaxInFlight, Interlocked.Increment(ref _inFlight));
            try
            {
                await Task.Delay(Hanging.Contains(url) ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(5), cancellationToken);

                if (Failures.TryGetValue(url, out var failures) && failures.TryDequeue(out var failure))
                {
                    if (failure.Status == null)
                    {
                        throw new HttpRequestException("Connection reset (simulated)");
                    }

                    var error = new HttpResponseMessage(failure.Status.Value);
                    if (failure.RetryAfterSeconds is { } seconds)
                    {
                        error.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                    }
                    return error;
                }

                if (NonHtml.TryGetValue(url, out var mediaType))
                {
                    var file = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                    file.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                    return file;
                }

                if (!pages.TryGetValue(url, out var html))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
                return response;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    // ---------- Helpers ----------

    private PageCrawlHandler CreateHandler(IServiceScope scope, FixtureSite site, double delaySeconds = 0, int maxPages = 200)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CRAWLER_DELAY_MIN_SECONDS"] = delaySeconds.ToString(CultureInfo.InvariantCulture),
                ["CRAWLER_DELAY_MAX_SECONDS"] = delaySeconds.ToString(CultureInfo.InvariantCulture),
                ["MAX_PAGES_SAFETY_LIMIT"] = maxPages.ToString(CultureInfo.InvariantCulture)
            })
            .Build();

        var normalizer = new UrlNormalizer();
        var crawler = new PageCrawler(new HttpClient(site), normalizer, new HtmlLinkExtractor(),
            new DomainLinkRatioCalculator(normalizer), configuration, NullLogger<PageCrawler>.Instance);

        return new PageCrawlHandler(
            scope.ServiceProvider.GetRequiredService<ICrawlStateStore>(),
            crawler,
            _queue,
            _services.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<PageCrawlHandler>.Instance)
        {
            CancellationPollInterval = TimeSpan.FromMilliseconds(50)
        };
    }

    private async Task<Job> SubmitJobAsync(int maxDepth)
    {
        using var scope = _services.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<ICrawlStateStore>().CreateJobAsync(Root, maxDepth, CancellationToken.None);
        await _queue.PublishAsync(RootMessage(job));
        _queue.Published.Clear();
        return job;
    }

    private static CrawlPageMessage RootMessage(Job job) =>
        new() { JobId = job.Id, Url = Root, Depth = 0, MaxDepth = job.MaxDepth, RootUrl = Root };

    /// <summary>Drains the fake queue one message at a time, like the single worker.</summary>
    private async Task RunWorkerAsync(FixtureSite site, double delaySeconds = 0, int maxPages = 200)
    {
        while (_queue.Messages.TryDequeue(out var message))
        {
            using var scope = _services.CreateScope();
            await CreateHandler(scope, site, delaySeconds, maxPages).HandleAsync(message, CancellationToken.None);
        }
    }

    private async Task<(Job Job, List<Page> Pages, List<Edge> Edges)> LoadAsync(Guid jobId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Jobs.SingleAsync(j => j.Id == jobId),
            await db.Pages.Where(p => p.JobId == jobId).ToListAsync(),
            await db.Edges.Where(e => e.JobId == jobId).ToListAsync());
    }

    private async Task CancelJobAsync(Guid jobId)
    {
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ICrawlStateStore>().CancelJobAsync(jobId, CancellationToken.None);
    }

    private static FixtureSite MultiLevelSite() => new(new Dictionary<string, string>
    {
        [Root] = """<a href="/about">About</a> <a href="/contact">Contact</a> <a href="https://external-partner.com">Partner</a>""",
        ["https://mock-site.test/about"] = """<a href="/">Home</a> <a href="/team">Team</a>""",
        ["https://mock-site.test/team"] = """<a href="/about">About</a> <a href="https://twitter.com/mock">Twitter</a>""",
        ["https://mock-site.test/contact"] = """<a href="mailto:info@mock-site.test">Email</a> <a href="tel:+1234">Call</a> <a href="/">Home</a>"""
    });

    // ---------- Recursive crawl ----------

    [Fact]
    public async Task Crawl_RecursesThroughQueueToMaxDepth_AndCompletesJob()
    {
        var site = MultiLevelSite();
        var job = await SubmitJobAsync(maxDepth: 2);

        await RunWorkerAsync(site);

        var (savedJob, pages, edges) = await LoadAsync(job.Id);
        savedJob.Status.Should().Be(JobStatus.Completed);
        pages.Should().OnlyContain(p => p.Status == PageStatus.Done);
        pages.ToDictionary(p => p.Url, p => p.Depth).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [Root] = 0,
            ["https://mock-site.test/about"] = 1,
            ["https://mock-site.test/contact"] = 1,
            ["https://mock-site.test/team"] = 2
        });

        // Ratios: root = 2 internal / 3; contact = 1 / 1 (mailto and tel ignored)
        pages.Single(p => p.Url == Root).DomainLinkRatio.Should().Be(0.6667);
        pages.Single(p => p.Url == "https://mock-site.test/contact").DomainLinkRatio.Should().Be(1.0);

        edges.Should().Contain(e => e.ParentUrl == Root && e.ChildUrl == "https://external-partner.com/");
        edges.Should().Contain(e => e.ParentUrl == "https://mock-site.test/about" && e.ChildUrl == "https://mock-site.test/team");

        // One message per claimed page, never for external or already-claimed URLs
        _queue.Published.Select(m => m.Url).Should().BeEquivalentTo(
            "https://mock-site.test/about", "https://mock-site.test/contact", "https://mock-site.test/team");
        site.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task Crawl_StopsAtMaxDepth()
    {
        var site = MultiLevelSite();
        var job = await SubmitJobAsync(maxDepth: 1);

        await RunWorkerAsync(site);

        var (savedJob, pages, _) = await LoadAsync(job.Id);
        savedJob.Status.Should().Be(JobStatus.Completed);
        pages.Select(p => p.Url).Should().NotContain("https://mock-site.test/team");
        pages.Should().HaveCount(3);
    }

    [Fact]
    public async Task Crawl_StopsClaimingAtPageLimit()
    {
        var job = await SubmitJobAsync(maxDepth: 2);

        await RunWorkerAsync(MultiLevelSite(), maxPages: 2);

        var (savedJob, pages, _) = await LoadAsync(job.Id);
        savedJob.Status.Should().Be(JobStatus.Completed);
        pages.Should().HaveCount(2);
    }

    [Fact]
    public async Task Crawl_RootNotFound_FailsJob()
    {
        var job = await SubmitJobAsync(maxDepth: 2);

        await RunWorkerAsync(new FixtureSite([]));

        var (savedJob, pages, _) = await LoadAsync(job.Id);
        savedJob.Status.Should().Be(JobStatus.Failed);
        savedJob.FailureReason.Should().Contain("404");
        pages.Single().Status.Should().Be(PageStatus.Failed);
    }

    [Fact]
    public async Task Crawl_NonHtmlRoot_SkipsPageAndCompletesJob()
    {
        var site = new FixtureSite([]);
        site.NonHtml[Root] = "application/pdf";
        var job = await SubmitJobAsync(maxDepth: 2);

        await RunWorkerAsync(site);

        var (savedJob, pages, _) = await LoadAsync(job.Id);
        savedJob.Status.Should().Be(JobStatus.Completed);
        pages.Single().Status.Should().Be(PageStatus.Skipped);
    }

    [Fact]
    public async Task Crawl_DownloadsOneAtATime_WithPolitenessDelayBeforeEachDownload()
    {
        var site = MultiLevelSite();
        await SubmitJobAsync(maxDepth: 2);

        var stopwatch = Stopwatch.StartNew();
        await RunWorkerAsync(site, delaySeconds: 0.2);

        site.MaxInFlight.Should().Be(1);
        site.Requests.Should().HaveCount(4);
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(4 * 180)); // delay precedes the root too
        for (var i = 1; i < site.Requests.Count; i++)
        {
            (site.Requests[i].StartedAt - site.Requests[i - 1].StartedAt).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(180));
        }
    }

    // ---------- HTTP retries ----------

    private FixtureSite SinglePageSite(params (HttpStatusCode? Status, int? RetryAfterSeconds)[] failuresBeforeSuccess)
    {
        var site = new FixtureSite(new Dictionary<string, string> { [Root] = "<p>no links</p>" });
        site.Failures[Root] = new Queue<(HttpStatusCode?, int?)>(failuresBeforeSuccess);
        return site;
    }

    [Fact]
    public async Task TransientHttpErrors_AreRetried_UntilSuccess()
    {
        var site = SinglePageSite((HttpStatusCode.ServiceUnavailable, null), (HttpStatusCode.BadGateway, null));
        var job = await SubmitJobAsync(maxDepth: 1);

        await RunWorkerAsync(site);

        site.Requests.Should().HaveCount(3);
        var (savedJob, pages, _) = await LoadAsync(job.Id);
        pages.Single().Status.Should().Be(PageStatus.Done);
        savedJob.Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task NetworkError_IsRetried()
    {
        var site = SinglePageSite((null, null));
        var job = await SubmitJobAsync(maxDepth: 1);

        await RunWorkerAsync(site);

        site.Requests.Should().HaveCount(2);
        (await LoadAsync(job.Id)).Pages.Single().Status.Should().Be(PageStatus.Done);
    }

    [Fact]
    public async Task PersistentTransientError_FailsPageAfterMaxAttempts()
    {
        var site = SinglePageSite(Enumerable.Repeat<(HttpStatusCode?, int?)>((HttpStatusCode.InternalServerError, null), 5).ToArray());
        var job = await SubmitJobAsync(maxDepth: 1);

        await RunWorkerAsync(site);

        site.Requests.Should().HaveCount(PageCrawler.MaxAttempts);
        var (savedJob, pages, _) = await LoadAsync(job.Id);
        pages.Single().Status.Should().Be(PageStatus.Failed);
        pages.Single().FailureReason.Should().Contain("500").And.Contain($"after {PageCrawler.MaxAttempts} attempts");
        savedJob.Status.Should().Be(JobStatus.Failed);
    }

    [Fact]
    public async Task PermanentHttpError_IsNotRetried()
    {
        var site = SinglePageSite((HttpStatusCode.Forbidden, null));
        var job = await SubmitJobAsync(maxDepth: 1);

        await RunWorkerAsync(site);

        site.Requests.Should().ContainSingle();
        (await LoadAsync(job.Id)).Pages.Single().FailureReason.Should().Be("HTTP status 403 (Forbidden).");
    }

    [Fact]
    public async Task TooManyRequests_WaitsForRetryAfter()
    {
        var site = SinglePageSite((HttpStatusCode.TooManyRequests, 1));
        await SubmitJobAsync(maxDepth: 1);

        await RunWorkerAsync(site);

        site.Requests.Should().HaveCount(2);
        (site.Requests[1].StartedAt - site.Requests[0].StartedAt).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(950));
    }

    // ---------- Cancellation ----------

    [Fact]
    public async Task CancelledJob_RemainingMessagesAreDiscardedWithoutDownloading()
    {
        var site = MultiLevelSite();
        var job = await SubmitJobAsync(maxDepth: 2);
        await CancelJobAsync(job.Id);

        var stopwatch = Stopwatch.StartNew();
        await RunWorkerAsync(site, delaySeconds: 5);

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1)); // no politeness delay for discarded messages
        site.Requests.Should().BeEmpty();
        _queue.Published.Should().BeEmpty();
        (await LoadAsync(job.Id)).Job.Status.Should().Be(JobStatus.Canceled);
    }

    [Fact]
    public async Task CancelDuringPolitenessDelay_AbortsPageWithoutDownloading()
    {
        var site = MultiLevelSite();
        var job = await SubmitJobAsync(maxDepth: 2);

        var stopwatch = Stopwatch.StartNew();
        var run = RunWorkerAsync(site, delaySeconds: 10);
        await Task.Delay(200);
        await CancelJobAsync(job.Id);
        await run;

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        site.Requests.Should().BeEmpty();
        _queue.Published.Should().BeEmpty();
        var (savedJob, pages, _) = await LoadAsync(job.Id);
        savedJob.Status.Should().Be(JobStatus.Canceled);
        pages.Single().Status.Should().Be(PageStatus.Skipped);
    }

    [Fact]
    public async Task CancelDuringDownload_AbortsRequest_AndPersistsNothing()
    {
        var site = MultiLevelSite();
        site.Hanging.Add(Root);
        var job = await SubmitJobAsync(maxDepth: 2);

        var stopwatch = Stopwatch.StartNew();
        var run = RunWorkerAsync(site);
        await Task.Delay(200);
        await CancelJobAsync(job.Id);
        await run;

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        site.Requests.Should().ContainSingle();
        _queue.Published.Should().BeEmpty();
        var (_, pages, edges) = await LoadAsync(job.Id);
        pages.Single().Status.Should().Be(PageStatus.Skipped);
        edges.Should().BeEmpty();
    }

    // ---------- Redelivery ----------

    [Fact]
    public async Task RedeliveredMessageForFinishedPage_RepublishesQueuedChildrenWithoutRedownloading()
    {
        var site = MultiLevelSite();
        var job = await SubmitJobAsync(maxDepth: 2);
        using (var scope = _services.CreateScope())
        {
            await CreateHandler(scope, site).HandleAsync(RootMessage(job), CancellationToken.None);
        }
        _queue.Published.Clear();

        // Simulates a crash after commit but before publish/ack: the root message is delivered again
        using (var scope = _services.CreateScope())
        {
            await CreateHandler(scope, site).HandleAsync(RootMessage(job), CancellationToken.None);
        }

        site.Requests.Should().ContainSingle(); // root downloaded once
        _queue.Published.Select(m => (m.Url, m.Depth)).Should().BeEquivalentTo(new[]
        {
            ("https://mock-site.test/about", 1),
            ("https://mock-site.test/contact", 1)
        });
    }

    [Fact]
    public async Task FailPage_MarksQueuedPageFailed_AndFinishesJob()
    {
        var job = await SubmitJobAsync(maxDepth: 2);

        using (var scope = _services.CreateScope())
        {
            var failed = await CreateHandler(scope, MultiLevelSite()).FailPageAsync(RootMessage(job), "boom", CancellationToken.None);
            failed.Should().BeTrue();
        }

        var (savedJob, pages, _) = await LoadAsync(job.Id);
        pages.Single().Status.Should().Be(PageStatus.Failed);
        savedJob.Status.Should().Be(JobStatus.Failed);
        savedJob.FailureReason.Should().Be("boom");
    }
}
