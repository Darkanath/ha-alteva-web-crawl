using System.Collections.Generic;
using Alteva.Domain.Entities;

namespace Alteva.Domain.Models;

/// <summary>
/// A crawled page in the job's tree view, placed under the page that first discovered it.
/// </summary>
public class JobTreeNode
{
    public string Url { get; set; } = string.Empty;

    /// <summary>Null unless <see cref="Status"/> is <see cref="PageStatus.Done"/>.</summary>
    public double? DomainLinkRatio { get; set; }

    public PageStatus Status { get; set; }

    public int Depth { get; set; }

    public List<JobTreeNode> Children { get; set; } = [];
}
