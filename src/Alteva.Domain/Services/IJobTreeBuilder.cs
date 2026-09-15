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
    /// Builds a tree rooted at the job's root page in which every page appears once.
    /// </summary>
    /// <param name="rootUrl">The job's normalized root URL (equal to the root page's URL).</param>
    /// <param name="pages">All pages of the job.</param>
    /// <param name="edges">All directed parent-to-child links of the job, in discovery order.</param>
    /// <returns>The root node with nested children, or null if the root page is not found.</returns>
    JobTreeNode? BuildTree(string rootUrl, IEnumerable<Page> pages, IEnumerable<Edge> edges);
}
