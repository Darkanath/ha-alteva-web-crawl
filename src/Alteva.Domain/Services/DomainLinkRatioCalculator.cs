using System;
using System.Collections.Generic;

namespace Alteva.Domain.Services;

/// <summary>
/// Calculates the Domain Link Ratio in strict accordance with the assignment specifications:
/// Domain Link Ratio = (# outgoing links that remain within starting domain) / (total # outgoing links)
/// Ignores mailto:, tel:, and non-http(s) schemes.
/// If page has zero outgoing links, ratio is 0.0.
/// </summary>
public class DomainLinkRatioCalculator(IUrlNormalizer urlNormalizer) : IDomainLinkRatioCalculator
{
    private readonly IUrlNormalizer _urlNormalizer = urlNormalizer ?? throw new ArgumentNullException(nameof(urlNormalizer));

    public double Calculate(IEnumerable<string> outgoingUrls, string startingHost)
    {
        if (outgoingUrls == null || string.IsNullOrWhiteSpace(startingHost))
        {
            return 0.0;
        }

        var totalValidLinks = 0;
        var internalLinks = 0;

        foreach (var url in outgoingUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            // UrlNormalizer filters out mailto:, tel:, javascript:, etc., and non-http(s)
            var normalized = _urlNormalizer.Normalize(url);
            if (normalized == null)
            {
                continue;
            }

            totalValidLinks++;

            if (_urlNormalizer.IsSameDomain(normalized, startingHost))
            {
                internalLinks++;
            }
        }

        if (totalValidLinks == 0)
        {
            return 0.0;
        }

        // Return ratio rounded to 4 decimal places for precision
        return Math.Round((double)internalLinks / totalValidLinks, 4);
    }
}
