using System.Collections.Generic;

namespace Alteva.Domain.Services;

/// <summary>
/// Service responsible for parsing HTML and extracting all anchor hyperlink URLs.
/// </summary>
public interface IHtmlLinkExtractor
{
    /// <summary>
    /// Extracts all raw href values from HTML content.
    /// </summary>
    /// <param name="htmlContent">The HTML body as string.</param>
    /// <returns>A collection of raw href link strings.</returns>
    IReadOnlyList<string> ExtractLinks(string htmlContent);
}
