using System;

namespace Alteva.Domain.Models;

/// <summary>
/// Message requesting that a single page of a crawl job be fetched and expanded.
/// The API publishes the root page at depth 0; the worker publishes one message per newly
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
    /// Link distance from the root (root = 0).
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    /// The job's maximum depth, carried so the worker need not reload the job.
    /// </summary>
    public int MaxDepth { get; set; }

    /// <summary>
    /// The job's normalized root URL; its host defines "same domain" for child claims and the ratio.
    /// </summary>
    public string RootUrl { get; set; } = string.Empty;
}
