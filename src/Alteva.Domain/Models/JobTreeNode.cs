using System.Collections.Generic;

namespace Alteva.Domain.Models;

/// <summary>
/// Represents a node in the hierarchical tree view of crawled pages.
/// </summary>
public class JobTreeNode
{
    public string Url { get; set; } = string.Empty;
    public double DomainLinkRatio { get; set; }
    public int Depth { get; set; }
    public List<JobTreeNode> Children { get; set; } = [];
}
