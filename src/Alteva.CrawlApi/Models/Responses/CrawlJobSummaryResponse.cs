using System;
using Alteva.Domain.Entities;

namespace Alteva.CrawlApi.Models.Responses;

/// <summary>
/// Summary representation of a crawl job for history lists.
/// </summary>
public class CrawlJobSummaryResponse
{
    public Guid Id { get; set; }
    public string InputUrl { get; set; } = string.Empty;
    public int MaxDepth { get; set; }
    public JobStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
}
