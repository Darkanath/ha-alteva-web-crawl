using System;
using Alteva.Domain.Entities;
using Alteva.Domain.Models;

namespace Alteva.CrawlApi.Models.Responses;

/// <summary>
/// Detailed view of a crawl job including its lifecycle state and resulting tree structure.
/// </summary>
public class CrawlJobDetailsResponse
{
    public Guid Id { get; set; }
    public string InputUrl { get; set; } = string.Empty;
    public int MaxDepth { get; set; }
    public JobStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public JobTreeNode? Tree { get; set; }
}
