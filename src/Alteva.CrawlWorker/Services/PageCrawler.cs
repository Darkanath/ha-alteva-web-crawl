using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alteva.Domain.Entities;
using Alteva.Domain.Models;
using Alteva.Domain.Services;
using Alteva.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Alteva.CrawlWorker.Services;

/// <summary>
/// Politely downloads and parses exactly one page, producing the outcome to commit.
/// Every download attempt is preceded by the politeness delay; transient failures are retried.
/// </summary>
public class PageCrawler
{
    public const double DefaultDelayMinSeconds = 3;
    public const double DefaultDelayMaxSeconds = 5;
    public const int MaxAttempts = 3;
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly IUrlNormalizer _urlNormalizer;
    private readonly IHtmlLinkExtractor _htmlLinkExtractor;
    private readonly IDomainLinkRatioCalculator _ratioCalculator;
    private readonly ILogger<PageCrawler> _logger;
    private readonly TimeSpan _delayMin;
    private readonly TimeSpan _delayMax;

    private sealed record Attempt(string? Html, PageStatus Status, string? Reason, bool IsTransient, TimeSpan? RetryAfter);

    public PageCrawler(
        HttpClient httpClient,
        IUrlNormalizer urlNormalizer,
        IHtmlLinkExtractor htmlLinkExtractor,
        IDomainLinkRatioCalculator ratioCalculator,
        IConfiguration configuration,
        ILogger<PageCrawler> logger)
    {
        _httpClient = httpClient;
        _urlNormalizer = urlNormalizer;
        _htmlLinkExtractor = htmlLinkExtractor;
        _ratioCalculator = ratioCalculator;
        _logger = logger;

        var delayMinSeconds = Math.Max(0, configuration.GetValue("CRAWLER_DELAY_MIN_SECONDS", DefaultDelayMinSeconds));
        var delayMaxSeconds = Math.Max(delayMinSeconds, configuration.GetValue("CRAWLER_DELAY_MAX_SECONDS", DefaultDelayMaxSeconds));
        _delayMin = TimeSpan.FromSeconds(delayMinSeconds);
        _delayMax = TimeSpan.FromSeconds(delayMaxSeconds);
    }

    /// <summary>
    /// Fetches <see cref="CrawlPageMessage.Url"/> with up to <see cref="MaxAttempts"/> attempts.
    /// Network errors, timeouts, HTTP 408, 429 and 5xx are transient and retried; other HTTP errors fail at once.
    /// A page that still fails becomes a <see cref="PageStatus.Failed"/> result. Cancellation of
    /// <paramref name="cancellationToken"/> propagates, including during delays.
    /// </summary>
    public async Task<PageResult> CrawlAsync(CrawlPageMessage message, int maxPages, CancellationToken cancellationToken)
    {
        TimeSpan? retryAfter = null;
        for (var attempt = 1; ; attempt++)
        {
            var delay = NextPolitenessDelay();
            if (retryAfter > delay)
            {
                delay = retryAfter.Value;
            }
            await Task.Delay(delay, cancellationToken);

            var result = await FetchAsync(message, cancellationToken);
            if (result.Html != null)
            {
                return ParsePage(message, maxPages, result.Html);
            }

            if (!result.IsTransient || attempt == MaxAttempts)
            {
                var reason = result.IsTransient ? $"{result.Reason} (after {attempt} attempts)" : result.Reason;
                return new PageResult(message.JobId, message.Url, message.Depth, message.MaxDepth, maxPages,
                    result.Status, null, reason, [], []);
            }

            retryAfter = result.RetryAfter;
            _logger.LogWarning("Job {JobId}: Transient failure fetching {Url} (attempt {Attempt}/{MaxAttempts}): {Reason}. Retrying.",
                message.JobId, message.Url, attempt, MaxAttempts, result.Reason);
        }
    }

    private async Task<Attempt> FetchAsync(CrawlPageMessage message, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(message.Url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || status >= 500;
                _logger.LogWarning("Job {JobId}: {Url} returned HTTP {StatusCode}", message.JobId, message.Url, status);
                return new Attempt(null, PageStatus.Failed, $"HTTP status {status} ({response.StatusCode}).", transient, GetRetryAfter(response));
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Job {JobId}: Skipping non-HTML resource {Url} ({ContentType})", message.JobId, message.Url, contentType);
                return new Attempt(null, PageStatus.Skipped, $"Non-HTML content ({contentType}).", false, null);
            }

            return new Attempt(await response.Content.ReadAsStringAsync(cancellationToken), PageStatus.Done, null, false, null);
        }
        // HttpClient timeouts surface as TaskCanceledException without our token being cancelled.
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Job {JobId}: Network or timeout error fetching {Url}", message.JobId, message.Url);
            return new Attempt(null, PageStatus.Failed, $"Failed to fetch: {ex.Message}", true, null);
        }
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var retryAfter = header?.Delta ?? (header?.Date - DateTimeOffset.UtcNow);
        if (retryAfter is not { } value || value <= TimeSpan.Zero)
        {
            return null;
        }

        return value < MaxRetryAfter ? value : MaxRetryAfter;
    }

    private PageResult ParsePage(CrawlPageMessage message, int maxPages, string html)
    {
        var startingHost = _urlNormalizer.ExtractHost(message.RootUrl);
        var linkedUrls = new List<string>();
        var childCandidates = new List<string>();
        foreach (var rawLink in _htmlLinkExtractor.ExtractLinks(html))
        {
            var link = _urlNormalizer.Normalize(rawLink, message.Url);
            if (link == null)
            {
                continue;
            }

            linkedUrls.Add(link);
            if (message.Depth < message.MaxDepth && _urlNormalizer.IsSameDomain(link, startingHost))
            {
                childCandidates.Add(link);
            }
        }

        var ratio = _ratioCalculator.Calculate(linkedUrls, startingHost);
        return new PageResult(message.JobId, message.Url, message.Depth, message.MaxDepth, maxPages,
            PageStatus.Done, ratio, null, linkedUrls, childCandidates);
    }

    private TimeSpan NextPolitenessDelay() =>
        _delayMin + (_delayMax - _delayMin) * Random.Shared.NextDouble();
}
