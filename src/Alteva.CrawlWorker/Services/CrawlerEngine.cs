using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alteva.Domain.Entities;
using Alteva.Domain.Services;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Alteva.CrawlWorker.Services;

public class CrawlerEngine(
    HttpClient httpClient,
    IUrlNormalizer urlNormalizer,
    IHtmlLinkExtractor htmlLinkExtractor,
    IDomainLinkRatioCalculator ratioCalculator,
    IConfiguration configuration,
    ILogger<CrawlerEngine> logger) : ICrawlerEngine
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IUrlNormalizer _urlNormalizer = urlNormalizer ?? throw new ArgumentNullException(nameof(urlNormalizer));
    private readonly IHtmlLinkExtractor _htmlLinkExtractor = htmlLinkExtractor ?? throw new ArgumentNullException(nameof(htmlLinkExtractor));
    private readonly IDomainLinkRatioCalculator _ratioCalculator = ratioCalculator ?? throw new ArgumentNullException(nameof(ratioCalculator));
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly ILogger<CrawlerEngine> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // Global per-domain politeness limiters
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _domainSemaphores = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct PageFetchOutcome(string? Html, bool IsHardFailure, string? ErrorMessage);

    private readonly record struct PageProcessingResult(
        Page Page,
        List<(string Parent, string Child)> EdgeKeys,
        List<string> NextLevelCandidates);

    public async Task<CrawlExecutionResult> CrawlAsync(
        Guid jobId,
        string rootUrl,
        int maxDepth = 2,
        int maxPages = 200,
        int maxConcurrency = 5,
        CancellationToken cancellationToken = default)
    {
        var result = new CrawlExecutionResult { JobId = jobId };

        var normalizedRoot = _urlNormalizer.Normalize(rootUrl);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            result.Success = false;
            result.ErrorMessage = $"Invalid starting URL '{rootUrl}'.";
            return result;
        }

        string startingHost;
        try
        {
            startingHost = _urlNormalizer.ExtractHost(normalizedRoot);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Failed to extract host from starting URL: {ex.Message}";
            return result;
        }

        maxConcurrency = Math.Max(1, maxConcurrency);

        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edgeSet = new HashSet<(string Parent, string Child)>();
        var syncLock = new object();

        _logger.LogInformation("Job {JobId}: Starting crawl for {RootUrl} (Host: {Host}, MaxDepth: {MaxDepth}, MaxPages: {MaxPages}, MaxConcurrency: {MaxConcurrency})",
            jobId, normalizedRoot, startingHost, maxDepth, maxPages, maxConcurrency);

        // The root is always a single fetch - a failure here fails the whole crawl, unlike a
        // failure discovered deeper in the traversal, which is skipped instead.
        var rootOutcome = await FetchHtmlAsync(jobId, normalizedRoot, cancellationToken);
        if (rootOutcome.IsHardFailure)
        {
            result.Success = false;
            result.ErrorMessage = rootOutcome.ErrorMessage;
            return result;
        }

        if (rootOutcome.Html == null)
        {
            // Root resolved but wasn't HTML (e.g. a redirect to a PDF) - nothing to crawl,
            // but not a failure either.
            result.Success = true;
            return result;
        }

        var rootPageResult = ComputePageResult(jobId, normalizedRoot, 0, rootOutcome.Html, maxDepth, startingHost);
        lock (syncLock)
        {
            visitedUrls.Add(normalizedRoot);
            MergePageResult(rootPageResult, edgeSet, result);
        }
        var currentLevelUrls = rootPageResult.NextLevelCandidates;

        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var depth = 1;

        // Breadth-first, one depth "wave" at a time: every URL in a wave is fetched with up to
        // maxConcurrency requests in flight at once, instead of the old one-page-at-a-time loop.
        while (currentLevelUrls.Count > 0 && depth <= maxDepth && !cancellationToken.IsCancellationRequested)
        {
            List<string> toFetch;
            lock (syncLock)
            {
                var remainingBudget = maxPages - visitedUrls.Count;
                toFetch = currentLevelUrls
                    .Where(u => !visitedUrls.Contains(u))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(Math.Max(0, remainingBudget))
                    .ToList();
            }

            if (toFetch.Count == 0)
            {
                if (visitedUrls.Count >= maxPages)
                {
                    _logger.LogWarning("Job {JobId}: Reached maximum page safety limit ({MaxPages}). Halting crawl.", jobId, maxPages);
                }
                break;
            }

            var nextLevelUrls = new List<string>();

            var fetchTasks = toFetch.Select(async url =>
            {
                // Cancellation during the wait itself is intentionally not observed here - a
                // fetch already in flight is left to finish (or hit the HttpClient timeout) and
                // the crawl winds down naturally via the while-loop's own cancellation check,
                // the same way the original sequential loop did.
                await semaphore.WaitAsync(CancellationToken.None);
                try
                {
                    var outcome = await FetchHtmlAsync(jobId, url, cancellationToken);
                    if (outcome.Html == null)
                    {
                        // Hard failure or non-HTML: contributes nothing further. Deliberately not
                        // added to visitedUrls, so if the same URL is reachable via another path
                        // later in the crawl, it gets a second attempt.
                        return;
                    }

                    // Regex link extraction, URL normalization and ratio math are pure CPU work
                    // over this page's own HTML - do it outside the lock so concurrent fetches
                    // don't serialize on it, and only take the lock for the actual state merge.
                    var pageResult = ComputePageResult(jobId, url, depth, outcome.Html, maxDepth, startingHost);

                    lock (syncLock)
                    {
                        visitedUrls.Add(url);
                        MergePageResult(pageResult, edgeSet, result);
                        nextLevelUrls.AddRange(pageResult.NextLevelCandidates);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(fetchTasks);

            currentLevelUrls = nextLevelUrls;
            depth++;
        }

        result.Success = true;
        _logger.LogInformation("Job {JobId}: Crawl completed successfully. Discovered {PagesCount} pages and {EdgesCount} edges.",
            jobId, result.Pages.Count, result.Edges.Count);

        return result;
    }

    /// <summary>
    /// Extracts links from a fetched page, computes its Page/Edge results and the same-domain,
    /// within-depth candidates for the next BFS wave. Pure computation over this page's own
    /// HTML only - touches no shared crawl state, so it's safe to call from multiple concurrent
    /// fetches without holding the crawl's sync lock.
    /// </summary>
    private PageProcessingResult ComputePageResult(
        Guid jobId,
        string currentUrl,
        int depth,
        string html,
        int maxDepth,
        string startingHost)
    {
        var rawLinks = _htmlLinkExtractor.ExtractLinks(html);
        var normalizedOutgoing = new List<string>(rawLinks.Count);
        var edgeKeys = new List<(string Parent, string Child)>();
        var nextLevelCandidates = new List<string>();

        foreach (var rawLink in rawLinks)
        {
            var normalizedLink = _urlNormalizer.Normalize(rawLink, currentUrl);
            if (normalizedLink == null)
            {
                continue;
            }

            normalizedOutgoing.Add(normalizedLink);
            edgeKeys.Add((currentUrl, normalizedLink));

            if (depth < maxDepth && _urlNormalizer.IsSameDomain(normalizedLink, startingHost))
            {
                nextLevelCandidates.Add(normalizedLink);
            }
        }

        var ratio = _ratioCalculator.Calculate(normalizedOutgoing, startingHost);
        var page = new Page
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            Url = currentUrl,
            DomainLinkRatio = ratio
        };

        return new PageProcessingResult(page, edgeKeys, nextLevelCandidates);
    }

    /// <summary>
    /// Merges a <see cref="ComputePageResult"/> outcome into the shared crawl state
    /// (deduplicating edges). Callers must hold the crawl's sync lock before calling this.
    /// </summary>
    private static void MergePageResult(
        PageProcessingResult pageResult,
        HashSet<(string Parent, string Child)> edgeSet,
        CrawlExecutionResult result)
    {
        result.Pages.Add(pageResult.Page);

        foreach (var edgeKey in pageResult.EdgeKeys)
        {
            if (edgeSet.Add(edgeKey))
            {
                result.Edges.Add(new Edge
                {
                    JobId = pageResult.Page.JobId,
                    ParentUrl = edgeKey.Parent,
                    ChildUrl = edgeKey.Child
                });
            }
        }
    }

    private async Task<PageFetchOutcome> FetchHtmlAsync(Guid jobId, string url, CancellationToken cancellationToken)
    {
        string host;
        try
        {
            host = _urlNormalizer.ExtractHost(url);
        }
        catch
        {
            // Fallback for malformed URLs
            host = "unknown";
        }

        var maxConcurrentPerDomain = Math.Max(1, _configuration.GetValue<int>("CRAWLER_MAX_CONCURRENT_PER_DOMAIN", 2));
        var domainSemaphore = _domainSemaphores.GetOrAdd(host, _ => new SemaphoreSlim(maxConcurrentPerDomain, maxConcurrentPerDomain));

        await domainSemaphore.WaitAsync(cancellationToken);
        try
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Job {JobId}: Failed to fetch {Url}. Status: {StatusCode}", jobId, url, response.StatusCode);
                    return new PageFetchOutcome(null, true, $"Root URL returned HTTP status {response.StatusCode}.");
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Job {JobId}: Skipping non-HTML resource {Url} (Type: {ContentType})", jobId, url, contentType);
                    return new PageFetchOutcome(null, false, null);
                }

                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                return new PageFetchOutcome(html, false, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Job {JobId}: Network or timeout error fetching {Url}", jobId, url);
                return new PageFetchOutcome(null, true, $"Failed to fetch root URL: {ex.Message}");
            }
        }
        finally
        {
            domainSemaphore.Release();
        }
    }
}
