using System;

namespace Alteva.Domain.Entities;

/// <summary>
/// Represents a directed hyperlink relationship (an edge in the graph) 
/// from one crawled page to another.
/// </summary>
public class Edge
{
    /// <summary>
    /// Internal database identifier for the edge.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The ID of the Job this link relationship belongs to.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// The absolute, normalized URL of the page containing the link.
    /// </summary>
    public string ParentUrl { get; set; } = string.Empty;

    /// <summary>
    /// The absolute, normalized URL that the link points to.
    /// </summary>
    public string ChildUrl { get; set; } = string.Empty;
}
