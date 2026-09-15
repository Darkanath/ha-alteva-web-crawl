using System;
using System.Collections.Generic;
using System.Linq;
using Alteva.Domain.Entities;
using Alteva.Domain.Models;

namespace Alteva.Domain.Services;

/// <summary>
/// Default implementation of <see cref="IJobTreeBuilder"/>.
/// Assembles a cycle-safe tree hierarchy from flat Page entities and directed Edge entities.
/// </summary>
public class JobTreeBuilder(IUrlNormalizer urlNormalizer) : IJobTreeBuilder
{
    private readonly IUrlNormalizer _urlNormalizer = urlNormalizer ?? throw new ArgumentNullException(nameof(urlNormalizer));

    public JobTreeNode? BuildTree(string rootUrl, IEnumerable<Page> pages, IEnumerable<Edge> edges, int maxDepth = 10)
    {
        if (string.IsNullOrWhiteSpace(rootUrl) || pages == null || edges == null)
        {
            return null;
        }

        var normalizedRoot = _urlNormalizer.Normalize(rootUrl) ?? rootUrl;

        // Map pages by normalized URL for O(1) ratio lookups
        var pageMap = pages
            .GroupBy(p => p.Url, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Group edges by parent URL for O(1) children lookups
        var edgeGroup = edges
            .GroupBy(e => e.ParentUrl, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ChildUrl).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        var rootPage = pageMap.TryGetValue(normalizedRoot, out var p) ? p : null;
        var rootNode = new JobTreeNode
        {
            Url = normalizedRoot,
            DomainLinkRatio = rootPage?.DomainLinkRatio ?? 0.0,
            Depth = 0
        };

        var currentPath = new HashSet<string>(StringComparer.Ordinal) { normalizedRoot };
        PopulateChildren(rootNode, edgeGroup, pageMap, currentPath, 0, maxDepth);

        return rootNode;
    }

    private void PopulateChildren(
        JobTreeNode parentNode,
        Dictionary<string, List<string>> edgesByParent,
        Dictionary<string, Page> pagesByUrl,
        HashSet<string> currentPath,
        int currentDepth,
        int maxDepth)
    {
        if (currentDepth >= maxDepth)
        {
            return;
        }

        if (!edgesByParent.TryGetValue(parentNode.Url, out var childUrls))
        {
            return;
        }

        foreach (var childUrl in childUrls)
        {
            // Cycle prevention along this branch
            if (currentPath.Contains(childUrl))
            {
                continue;
            }

            var page = pagesByUrl.TryGetValue(childUrl, out var p) ? p : null;
            var childNode = new JobTreeNode
            {
                Url = childUrl,
                DomainLinkRatio = page?.DomainLinkRatio ?? 0.0,
                Depth = currentDepth + 1
            };

            parentNode.Children.Add(childNode);

            currentPath.Add(childUrl);
            PopulateChildren(childNode, edgesByParent, pagesByUrl, currentPath, currentDepth + 1, maxDepth);
            currentPath.Remove(childUrl);
        }
    }
}
