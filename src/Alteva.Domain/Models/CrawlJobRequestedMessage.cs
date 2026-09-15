using System;

namespace Alteva.Domain.Models;

/// <summary>
/// Event message published when a new web crawl job has been initiated.
/// Consumed by background crawl workers.
/// </summary>
public class CrawlJobRequestedMessage
{
    public Guid JobId { get; set; }
    public string InputUrl { get; set; } = string.Empty;
    public int MaxDepth { get; set; } = 2;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
}
