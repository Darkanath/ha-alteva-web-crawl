namespace Alteva.Domain.Entities;

/// <summary>
/// Lifecycle of a single page within a recursive crawl. A job is complete once none of its
/// pages are <see cref="Queued"/> or <see cref="Processing"/>.
/// </summary>
public enum PageStatus
{
    /// <summary>
    /// Claimed for the job and a crawl message has been (or is about to be) published for it.
    /// </summary>
    Queued = 0,

    /// <summary>
    /// A worker has started fetching and parsing the page.
    /// </summary>
    Processing = 1,

    /// <summary>
    /// Fetched and parsed successfully; its ratio and outgoing edges are persisted.
    /// </summary>
    Done = 2,

    /// <summary>
    /// Permanently failed (HTTP error, network failure after retries). Counts as finished.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// Resolved but not crawlable (e.g. non-HTML content). Counts as finished.
    /// </summary>
    Skipped = 4
}
