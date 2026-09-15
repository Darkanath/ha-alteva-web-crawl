using System.Collections.Generic;

namespace Alteva.Domain.Services;

/// <summary>
/// Service for calculating the Domain Link Ratio of a crawled page.
/// Formula: (# outgoing links within starting domain) / (total # outgoing links)
/// </summary>
public interface IDomainLinkRatioCalculator
{
    /// <summary>
    /// Calculates the Domain Link Ratio for a set of raw or normalized outgoing links.
    /// </summary>
    /// <param name="outgoingUrls">The collection of outgoing URLs discovered on the page.</param>
    /// <param name="startingHost">The host of the initial job URL.</param>
    /// <returns>A double value between 0.0 and 1.0 representing the ratio. Returns 0.0 if there are no valid outgoing links.</returns>
    double Calculate(IEnumerable<string> outgoingUrls, string startingHost);
}
