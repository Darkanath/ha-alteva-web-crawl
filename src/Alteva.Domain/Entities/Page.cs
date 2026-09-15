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
    /// </summary>
    public double DomainLinkRatio { get; set; }
}
