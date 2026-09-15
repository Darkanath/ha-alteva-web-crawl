using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alteva.Domain.Entities;
using Alteva.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Alteva.CrawlWorker.Services;

public class CrawlerEngine(
    HttpClient httpClient,
    IUrlNormalizer urlNormalizer,
    IHtmlLinkExtractor htmlLinkExtractor,
    IDomainLinkRatioCalculator ratioCalculator,
    ILogger<CrawlerEngine> logger) : ICrawlerEngine
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IUrlNormalizer _urlNormalizer = urlNormalizer ?? throw new ArgumentNullException(nameof(urlNormalizer));
    private readonly IHtmlLinkExtractor _htmlLinkExtractor = htmlLinkExtractor ?? throw new ArgumentNullException(nameof(htmlLinkExtractor));
    private readonly IDomainLinkRatioCalculator _ratioCalculator = ratioCalculator ?? throw new ArgumentNullException(nameof(ratioCalculator));
    private readonly ILogger<CrawlerEngine> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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

        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edgeSet = new HashSet<(string Parent, string Child)>();
        var queue = new Queue<(string Url, int Depth)>();

        queue.Enqueue((normalizedRoot, 0));

        _logger.LogInformation("Job {JobId}: Starting crawl for {RootUrl} (Host: {Host}, MaxDepth: {MaxDepth}, MaxPages: {MaxPages})",
            jobId, normalizedRoot, startingHost, maxDepth, maxPages);

        while (queue.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var (currentUrl, depth) = queue.Dequeue();

            if (visitedUrls.Contains(currentUrl))
            {
                continue;
            }

            if (visitedUrls.Count >= maxPages)
            {
                _logger.LogWarning("Job {JobId}: Reached maximum page safety limit ({MaxPages}). Halting crawl.", jobId, maxPages);
                break;
            }

            string html;
            try
            {
                using var response = await _httpClient.GetAsync(currentUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Job {JobId}: Failed to fetch {Url}. Status: {StatusCode}", jobId, currentUrl, response.StatusCode);
                    if (depth == 0)
                    {
                        // Failure on root URL fails the entire crawl
                        result.Success = false;
                        result.ErrorMessage = $"Root URL returned HTTP status {response.StatusCode}.";
                        return result;
                    }
                    continue;
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Job {JobId}: Skipping non-HTML resource {Url} (Type: {ContentType})", jobId, currentUrl, contentType);
                    continue;
                }

                html = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Job {JobId}: Network or timeout error fetching {Url}", jobId, currentUrl);
                if (depth == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Failed to fetch root URL: {ex.Message}";
                    return result;
                }
                continue;
            }

            visitedUrls.Add(currentUrl);

            // Extract links
            var rawLinks = _htmlLinkExtractor.ExtractLinks(html);
            var normalizedOutgoing = new List<string>();

            foreach (var rawLink in rawLinks)
            {
                var normalizedLink = _urlNormalizer.Normalize(rawLink, currentUrl);
                if (normalizedLink == null)
                {
                    continue;
                }

                normalizedOutgoing.Add(normalizedLink);

                // Record edge
                var edgeKey = (currentUrl, normalizedLink);
                if (!edgeSet.Contains(edgeKey))
                {
                    edgeSet.Add(edgeKey);
                    result.Edges.Add(new Edge
                    {
                        JobId = jobId,
                        ParentUrl = currentUrl,
                        ChildUrl = normalizedLink
                    });
                }

                // Enqueue if within depth limit and within target domain and not yet visited
                if (depth < maxDepth &&
                    _urlNormalizer.IsSameDomain(normalizedLink, startingHost) &&
                    !visitedUrls.Contains(normalizedLink))
                {
                    queue.Enqueue((normalizedLink, depth + 1));
                }
            }

            // Calculate Domain Link Ratio
            var ratio = _ratioCalculator.Calculate(normalizedOutgoing, startingHost);

            result.Pages.Add(new Page
            {
                Id = Guid.NewGuid(),
                JobId = jobId,
                Url = currentUrl,
                DomainLinkRatio = ratio
            });
        }

        result.Success = true;
        _logger.LogInformation("Job {JobId}: Crawl completed successfully. Discovered {PagesCount} pages and {EdgesCount} edges.",
            jobId, result.Pages.Count, result.Edges.Count);

        return result;
    }
}
