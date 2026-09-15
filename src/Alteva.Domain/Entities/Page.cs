using System;

namespace Alteva.Domain.Entities;

/// <summary>
/// Represents a single parsed HTML page discovered during a crawl job.
/// </summary>
public class Page
{
    /// <summary>
    /// Unique identifier for this page record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The ID of the Job this page belongs to.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// The normalized absolute URL of the page.
    /// Note: This is part of a composite unique key (JobId, Url) to ensure idempotency.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// The calculated ratio of outgoing links that remain on the same domain
    /// versus the total number of outgoing links on this page.
    /// Null until the page has been fetched and parsed (<see cref="PageStatus.Done"/>).
    /// </summary>
    public double? DomainLinkRatio { get; set; }

    /// <summary>
    /// Shortest known link distance from the job's root URL (root = 0). Lowered when a
    /// shorter path to the page is discovered, which re-queues it for expansion.
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    /// Current crawl state of this page. Rows are inserted as <see cref="PageStatus.Queued"/>
    /// when claimed, so the (JobId, Url) unique index doubles as the dedupe gate.
    /// </summary>
    public PageStatus Status { get; set; } = PageStatus.Queued;

    /// <summary>
    /// Number of transient processing failures recorded for this page's crawl message.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Why the page ended up <see cref="PageStatus.Failed"/> or <see cref="PageStatus.Skipped"/>.
    /// </summary>
    public string? FailureReason { get; set; }
}
