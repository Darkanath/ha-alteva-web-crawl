using System;
using System.Collections.Generic;
using System.Linq;
using Alteva.Domain.Entities;
using Alteva.Domain.Models;

namespace Alteva.Domain.Services;

/// <summary>
/// Default implementation of <see cref="IJobTreeBuilder"/>.
/// Builds a breadth-first spanning tree: every page of the job appears exactly once, under the
/// first page (in breadth-first order) that links to it — the same order in which the crawl
/// claimed pages. Links to URLs that are not pages of the job (external, beyond max depth or
/// the page limit) are not nodes.
/// </summary>
public class JobTreeBuilder : IJobTreeBuilder
{
    public JobTreeNode? BuildTree(string rootUrl, IEnumerable<Page> pages, IEnumerable<Edge> edges)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(edges);

        var pagesByUrl = pages.ToDictionary(p => p.Url, StringComparer.Ordinal);
        if (!pagesByUrl.TryGetValue(rootUrl, out var rootPage))
        {
            return null;
        }

        var linksByParent = edges
            .GroupBy(e => e.ParentUrl, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ChildUrl).ToList(), StringComparer.Ordinal);

        var root = CreateNode(rootPage, depth: 0);
        var placed = new HashSet<string>(StringComparer.Ordinal) { root.Url };
        var queue = new Queue<JobTreeNode>([root]);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!linksByParent.TryGetValue(node.Url, out var childUrls))
            {
                continue;
            }

            foreach (var childUrl in childUrls)
            {
                if (pagesByUrl.TryGetValue(childUrl, out var childPage) && placed.Add(childUrl))
                {
                    var child = CreateNode(childPage, node.Depth + 1);
                    node.Children.Add(child);
                    queue.Enqueue(child);
                }
            }
        }

        return root;
    }

    private static JobTreeNode CreateNode(Page page, int depth) => new()
    {
        Url = page.Url,
        DomainLinkRatio = page.Status == PageStatus.Done ? page.DomainLinkRatio : null,
        Status = page.Status,
        Depth = depth
    };
}
