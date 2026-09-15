using System;

namespace Alteva.Domain.Models;

/// <summary>
/// Message requesting that a single page of a crawl job be fetched and expanded.
/// The API publishes the root page at depth 0; workers publish one message per newly
/// claimed same-domain child at <c>Depth + 1</c> until <see cref="MaxDepth"/> is reached.
/// </summary>
public class CrawlPageMessage
{
    public Guid JobId { get; set; }

    /// <summary>
    /// Normalized absolute URL of the page to crawl. Matches <c>Page.Url</c>.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Link distance from the root at the time this message was published. A message whose
    /// depth is greater than the page's current depth is stale and is dropped.
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    /// The job's maximum depth, carried so workers need not reload the job to decide on fan-out.
    /// </summary>
    public int MaxDepth { get; set; }
}
