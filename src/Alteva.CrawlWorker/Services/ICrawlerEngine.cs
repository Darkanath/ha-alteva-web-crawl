using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alteva.CrawlWorker.Services;

/// <summary>
/// Core crawling engine responsible for executing the BFS crawl traversal,
/// page fetching, link extraction, Domain Link Ratio calculation, and safety bounds.
/// </summary>
public interface ICrawlerEngine
{
    /// <summary>
    /// Executes a crawl job, downloading one page at a time with a politeness delay between downloads.
    /// </summary>
    Task<CrawlExecutionResult> CrawlAsync(
        Guid jobId,
        string rootUrl,
        int maxDepth = 2,
        int maxPages = 200,
        CancellationToken cancellationToken = default);
}
