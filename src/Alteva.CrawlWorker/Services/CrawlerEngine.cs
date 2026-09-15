using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alteva.Domain.Entities;
using Alteva.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Alteva.CrawlWorker.Services;

public class CrawlerEngine : ICrawlerEngine
{
    public const double DefaultDelayMinSeconds = 3;
    public const double DefaultDelayMaxSeconds = 5;

    private readonly HttpClient _httpClient;
    private readonly IUrlNormalizer _urlNormalizer;
    private readonly IHtmlLinkExtractor _htmlLinkExtractor;
    private readonly IDomainLinkRatioCalculator _ratioCalculator;
    private readonly ILogger<CrawlerEngine> _logger;
    private readonly TimeSpan _delayMin;
    private readonly TimeSpan _delayMax;

    private readonly record struct PageFetchOutcome(string? Html, bool IsHardFailure, string? ErrorMessage);

    private readonly record struct PageProcessingResult(
        Page Page,
        List<(string Parent, string Child)> EdgeKeys,
        List<string> NextLevelCandidates);

    public CrawlerEngine(
        HttpClient httpClient,
        IUrlNormalizer urlNormalizer,
        IHtmlLinkExtractor htmlLinkExtractor,
        IDomainLinkRatioCalculator ratioCalculator,
        IConfiguration configuration,
        ILogger<CrawlerEngine> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _urlNormalizer = urlNormalizer ?? throw new ArgumentNullException(nameof(urlNormalizer));
        _htmlLinkExtractor = htmlLinkExtractor ?? throw new ArgumentNullException(nameof(htmlLinkExtractor));
        _ratioCalculator = ratioCalculator ?? throw new ArgumentNullException(nameof(ratioCalculator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(configuration);

        // Politeness delay between consecutive downloads, randomized within [min, max].
        var delayMinSeconds = Math.Max(0, configuration.GetValue("CRAWLER_DELAY_MIN_SECONDS", DefaultDelayMinSeconds));
        var delayMaxSeconds = Math.Max(delayMinSeconds, configuration.GetValue("CRAWLER_DELAY_MAX_SECONDS", DefaultDelayMaxSeconds));
        _delayMin = TimeSpan.FromSeconds(delayMinSeconds);
        _delayMax = TimeSpan.FromSeconds(delayMaxSeconds);
    }

    public async Task<CrawlExecutionResult> CrawlAsync(
        Guid jobId,
        string rootUrl,
        int maxDepth = 2,
        int maxPages = 200,
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

        _logger.LogInformation("Job {JobId}: Starting crawl for {RootUrl} (Host: {Host}, MaxDepth: {MaxDepth}, MaxPages: {MaxPages}, Delay: {DelayMin}-{DelayMax}s)",
            jobId, normalizedRoot, startingHost, maxDepth, maxPages, _delayMin.TotalSeconds, _delayMax.TotalSeconds);

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

        var edgeSet = new HashSet<(string Parent, string Child)>();
        var enqueuedUrls = new HashSet<string>(StringComparer.Ordinal) { normalizedRoot };
        var frontier = new Queue<(string Url, int Depth)>();

        var rootPageResult = ComputePageResult(jobId, normalizedRoot, 0, rootOutcome.Html, maxDepth, startingHost);
        MergePageResult(rootPageResult, edgeSet, result);
        EnqueueCandidates(rootPageResult.NextLevelCandidates, 1, enqueuedUrls, frontier);

        // Breadth-first, one page at a time: each download finishes (and is followed by a
        // politeness delay) before the next one starts.
        while (frontier.Count > 0)
        {
            if (result.Pages.Count >= maxPages)
            {
                _logger.LogWarning("Job {JobId}: Reached maximum page safety limit ({MaxPages}). Halting crawl.", jobId, maxPages);
                break;
            }

            var (url, depth) = frontier.Dequeue();

            await Task.Delay(NextPolitenessDelay(), cancellationToken);

            var outcome = await FetchHtmlAsync(jobId, url, cancellationToken);
            if (outcome.Html == null)
            {
                // Hard failure or non-HTML: contributes nothing further.
                continue;
            }

            var pageResult = ComputePageResult(jobId, url, depth, outcome.Html, maxDepth, startingHost);
            MergePageResult(pageResult, edgeSet, result);
            EnqueueCandidates(pageResult.NextLevelCandidates, depth + 1, enqueuedUrls, frontier);
        }

        result.Success = true;
        _logger.LogInformation("Job {JobId}: Crawl completed successfully. Discovered {PagesCount} pages and {EdgesCount} edges.",
            jobId, result.Pages.Count, result.Edges.Count);

        return result;
    }

    private TimeSpan NextPolitenessDelay()
    {
        var spread = _delayMax - _delayMin;
        return _delayMin + spread * Random.Shared.NextDouble();
    }

    private static void EnqueueCandidates(
        List<string> candidates,
        int depth,
        HashSet<string> enqueuedUrls,
        Queue<(string Url, int Depth)> frontier)
    {
        foreach (var candidate in candidates)
        {
            if (enqueuedUrls.Add(candidate))
            {
                frontier.Enqueue((candidate, depth));
            }
        }
    }

    /// <summary>
    /// Extracts links from a fetched page, computes its Page/Edge results and the same-domain,
    /// within-depth candidates for the next BFS level.
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
            DomainLinkRatio = ratio,
            Depth = depth,
            Status = PageStatus.Done
        };

        return new PageProcessingResult(page, edgeKeys, nextLevelCandidates);
    }

    /// <summary>
    /// Merges a <see cref="ComputePageResult"/> outcome into the crawl result (deduplicating edges).
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
        // Shutdown cancellation propagates to the worker (which requeues the message); only
        // network errors and HttpClient timeouts count as a failed fetch.
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Job {JobId}: Network or timeout error fetching {Url}", jobId, url);
            return new PageFetchOutcome(null, true, $"Failed to fetch root URL: {ex.Message}");
        }
    }
}
