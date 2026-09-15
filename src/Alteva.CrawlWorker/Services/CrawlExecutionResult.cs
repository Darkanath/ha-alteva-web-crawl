using System;
using System.Collections.Generic;
using Alteva.Domain.Entities;

namespace Alteva.CrawlWorker.Services;

/// <summary>
/// Encapsulates the results of a completed or partially completed crawl operation.
/// </summary>
public class CrawlExecutionResult
{
    public Guid JobId { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<Page> Pages { get; set; } = [];
    public List<Edge> Edges { get; set; } = [];
    public int TotalPagesCrawled => Pages.Count;
}
