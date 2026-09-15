namespace Alteva.Domain.Entities;

/// <summary>
/// Lifecycle of a single page within a recursive crawl. A job is complete once none of its
/// pages are <see cref="Queued"/>.
/// </summary>
public enum PageStatus
{
    /// <summary>
    /// Claimed for the job; its crawl message is queued (or being processed).
    /// </summary>
    Queued = 0,

    /// <summary>
    /// Fetched and parsed successfully; its ratio and outgoing edges are persisted.
    /// </summary>
    Done = 1,

    /// <summary>
    /// Permanently failed (HTTP error, network failure after retry). Counts as finished.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// Resolved but not crawlable (e.g. non-HTML content). Counts as finished.
    /// </summary>
    Skipped = 3
}
