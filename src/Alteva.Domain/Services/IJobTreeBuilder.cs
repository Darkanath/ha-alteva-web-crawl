using System.Collections.Generic;
using Alteva.Domain.Entities;
using Alteva.Domain.Models;

namespace Alteva.Domain.Services;

/// <summary>
/// Reconstructs the hierarchical tree result from flat Page and Edge records.
/// </summary>
public interface IJobTreeBuilder
{
    /// <summary>
    /// Builds a hierarchical tree rooted at the initial job URL.
    /// </summary>
    /// <param name="rootUrl">The starting URL of the job.</param>
    /// <param name="pages">All pages discovered in the job.</param>
    /// <param name="edges">All directed parent-to-child links in the job.</param>
    /// <param name="maxDepth">Maximum depth to prevent infinite loops.</param>
    /// <returns>The root JobTreeNode with nested children, or null if root page not found.</returns>
    JobTreeNode? BuildTree(string rootUrl, IEnumerable<Page> pages, IEnumerable<Edge> edges, int maxDepth = 10);
}
