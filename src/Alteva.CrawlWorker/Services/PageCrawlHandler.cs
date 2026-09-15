using System;
using System.Threading;
using System.Threading.Tasks;
using Alteva.Domain.Entities;
using Alteva.Domain.Models;
using Alteva.Infrastructure.Data;
using Alteva.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Alteva.CrawlWorker.Services;

/// <summary>
/// Handles one <see cref="CrawlPageMessage"/> end to end (architecture_notes.md §3.3):
/// gate → polite download with retries (<see cref="PageCrawler"/>) → commit → publish claimed children.
/// Returning normally means the message can be acked.
/// </summary>
public class PageCrawlHandler
{
    public const int DefaultMaxPages = 200;

    private readonly ICrawlStateStore _store;
    private readonly PageCrawler _pageCrawler;
    private readonly IMessagePublisher _publisher;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PageCrawlHandler> _logger;
    private readonly int _maxPages;

    public PageCrawlHandler(
        ICrawlStateStore store,
        PageCrawler pageCrawler,
        IMessagePublisher publisher,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PageCrawlHandler> logger)
    {
        _store = store;
        _pageCrawler = pageCrawler;
        _publisher = publisher;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _maxPages = configuration.GetValue("MAX_PAGES_SAFETY_LIMIT", DefaultMaxPages);
    }

    /// <summary>
    /// How often the job status is polled during delays and downloads to abort a cancelled job.
    /// </summary>
    public TimeSpan CancellationPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public async Task HandleAsync(CrawlPageMessage message, CancellationToken stoppingToken)
    {
        switch (await _store.GetPageGateAsync(message.JobId, message.Url, stoppingToken))
        {
            case PageGate.Discard:
                _logger.LogInformation("Job {JobId}: Discarding message for {Url} (job not active or page unknown).", message.JobId, message.Url);
                return;

            case PageGate.AlreadyFinished:
                await RepublishQueuedChildrenAsync(message, stoppingToken);
                return;
        }

        var result = await CrawlUnlessCancelledAsync(message, stoppingToken);
        if (result == null)
        {
            _logger.LogInformation("Job {JobId}: Job was cancelled while crawling {Url}. Discarding.", message.JobId, message.Url);
            return;
        }

        await CommitAndPublishAsync(result, message, stoppingToken);
    }

    /// <summary>
    /// Commits the page as <see cref="PageStatus.Failed"/> after its processing failed on redelivery.
    /// Returns false if nothing was committed (job no longer active, or page no longer Queued).
    /// </summary>
    public async Task<bool> FailPageAsync(CrawlPageMessage message, string reason, CancellationToken cancellationToken)
    {
        var result = new PageResult(message.JobId, message.Url, message.Depth, message.MaxDepth, _maxPages,
            PageStatus.Failed, null, reason, [], []);
        var commit = await _store.CommitPageAsync(result, cancellationToken);
        return commit.Committed;
    }

    // Returns null when the job was cancelled during a delay or download.
    private async Task<PageResult?> CrawlUnlessCancelledAsync(CrawlPageMessage message, CancellationToken stoppingToken)
    {
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var watcher = CancelWhenJobInactiveAsync(message.JobId, jobCancellation);

        try
        {
            return await _pageCrawler.CrawlAsync(message, _maxPages, jobCancellation.Token);
        }
        catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            jobCancellation.Cancel();
            await watcher;
        }
    }

    private async Task CancelWhenJobInactiveAsync(Guid jobId, CancellationTokenSource jobCancellation)
    {
        var token = jobCancellation.Token;
        try
        {
            while (true)
            {
                await Task.Delay(CancellationPollInterval, token);
                try
                {
                    // Own scope: the handler's DbContext must not be used concurrently.
                    using var scope = _scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<ICrawlStateStore>();
                    if (!await store.IsJobActiveAsync(jobId, token))
                    {
                        jobCancellation.Cancel();
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep crawling; the commit guard still protects a cancelled job.
                    _logger.LogWarning(ex, "Job {JobId}: Cancellation poll failed.", jobId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Page finished, job cancelled, or host stopping.
        }
    }

    private async Task CommitAndPublishAsync(PageResult result, CrawlPageMessage message, CancellationToken stoppingToken)
    {
        var commit = await _store.CommitPageAsync(result, stoppingToken);
        if (!commit.Committed)
        {
            _logger.LogInformation("Job {JobId}: Commit for {Url} rejected (job no longer active or page already finished).", message.JobId, message.Url);
            return;
        }

        foreach (var childUrl in commit.ClaimedChildren)
        {
            await PublishAsync(message, childUrl, message.Depth + 1, stoppingToken);
        }

        _logger.LogInformation("Job {JobId}: {Url} (depth {Depth}) -> {Status}, {ChildCount} children queued.",
            message.JobId, message.Url, message.Depth, result.Status, commit.ClaimedChildren.Count);
    }

    private async Task RepublishQueuedChildrenAsync(CrawlPageMessage message, CancellationToken stoppingToken)
    {
        var children = await _store.GetQueuedChildrenAsync(message.JobId, message.Url, stoppingToken);
        foreach (var (url, depth) in children)
        {
            await PublishAsync(message, url, depth, stoppingToken);
        }

        _logger.LogInformation("Job {JobId}: {Url} already finished; re-published {ChildCount} queued children.",
            message.JobId, message.Url, children.Count);
    }

    private Task PublishAsync(CrawlPageMessage parent, string url, int depth, CancellationToken cancellationToken) =>
        _publisher.PublishAsync(new CrawlPageMessage
        {
            JobId = parent.JobId,
            Url = url,
            Depth = depth,
            MaxDepth = parent.MaxDepth,
            RootUrl = parent.RootUrl
        }, cancellationToken: cancellationToken);
}
