using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace Alteva.Domain.Services;

/// <summary>
/// Extracts href links from HTML using compiled regular expressions.
/// </summary>
public partial class HtmlLinkExtractor : IHtmlLinkExtractor
{
    // Regex matches <a ... href="..." ...> or href='...' or href=unquoted
    [GeneratedRegex(@"<a\b[^>]*?\bhref=(?:""(?<url>[^""]*)""|'(?<url>[^']*)'|(?<url>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HrefRegex();

    public IReadOnlyList<string> ExtractLinks(string htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent))
        {
            return Array.Empty<string>();
        }

        var matches = HrefRegex().Matches(htmlContent);
        var links = new List<string>(matches.Count);

        foreach (Match match in matches)
        {
            if (match.Success)
            {
                var urlGroup = match.Groups["url"];
                if (urlGroup.Success && !string.IsNullOrWhiteSpace(urlGroup.Value))
                {
                    // Attribute values are HTML-encoded: href="?a=1&amp;b=2" means "?a=1&b=2"
                    links.Add(WebUtility.HtmlDecode(urlGroup.Value).Trim());
                }
            }
        }

        return links;
    }
}
