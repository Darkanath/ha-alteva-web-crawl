using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alteva.Domain.Entities;
using Alteva.Domain.Models;
using Alteva.Domain.Services;
using Alteva.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace Alteva.CrawlWorker.Services;

/// <summary>
/// Downloads and parses exactly one page, producing the outcome to commit. No delays, no state.
/// </summary>
public class PageCrawler(
    HttpClient httpClient,
    IUrlNormalizer urlNormalizer,
    IHtmlLinkExtractor htmlLinkExtractor,
    IDomainLinkRatioCalculator ratioCalculator,
    ILogger<PageCrawler> logger)
{
    /// <summary>
    /// Fetches <see cref="CrawlPageMessage.Url"/>. HTTP errors, network errors and timeouts become a
    /// <see cref="PageStatus.Failed"/> result; cancellation of <paramref name="cancellationToken"/> propagates.
    /// </summary>
    public async Task<PageResult> CrawlAsync(CrawlPageMessage message, int maxPages, CancellationToken cancellationToken)
    {
        var startingHost = urlNormalizer.ExtractHost(message.RootUrl);

        string html;
        try
        {
            using var response = await httpClient.GetAsync(message.Url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Job {JobId}: {Url} returned HTTP {StatusCode}", message.JobId, message.Url, (int)response.StatusCode);
                return Outcome(message, maxPages, PageStatus.Failed, $"HTTP status {(int)response.StatusCode} ({response.StatusCode}).");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Job {JobId}: Skipping non-HTML resource {Url} ({ContentType})", message.JobId, message.Url, contentType);
                return Outcome(message, maxPages, PageStatus.Skipped, $"Non-HTML content ({contentType}).");
            }

            html = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        // HttpClient timeouts surface as TaskCanceledException without our token being cancelled.
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Job {JobId}: Network or timeout error fetching {Url}", message.JobId, message.Url);
            return Outcome(message, maxPages, PageStatus.Failed, $"Failed to fetch: {ex.Message}");
        }

        var linkedUrls = new List<string>();
        var childCandidates = new List<string>();
        foreach (var rawLink in htmlLinkExtractor.ExtractLinks(html))
        {
            var link = urlNormalizer.Normalize(rawLink, message.Url);
            if (link == null)
            {
                continue;
            }

            linkedUrls.Add(link);
            if (message.Depth < message.MaxDepth && urlNormalizer.IsSameDomain(link, startingHost))
            {
                childCandidates.Add(link);
            }
        }

        var ratio = ratioCalculator.Calculate(linkedUrls, startingHost);
        return new PageResult(message.JobId, message.Url, message.Depth, message.MaxDepth, maxPages,
            PageStatus.Done, ratio, null, linkedUrls, childCandidates);
    }

    private static PageResult Outcome(CrawlPageMessage message, int maxPages, PageStatus status, string reason) =>
        new(message.JobId, message.Url, message.Depth, message.MaxDepth, maxPages, status, null, reason, [], []);
}
