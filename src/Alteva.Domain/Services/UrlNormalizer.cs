using System;

namespace Alteva.Domain.Services;

/// <summary>
/// Default implementation of <see cref="IUrlNormalizer"/> enforcing strict RFC 3986 rules,
/// fragment stripping, relative resolution, and HTTP/HTTPS scheme filtering.
/// </summary>
public class UrlNormalizer : IUrlNormalizer
{
    private static readonly string[] SupportedSchemes = [Uri.UriSchemeHttp, Uri.UriSchemeHttps];

    /// <inheritdoc />
    public string? Normalize(string rawUrl, string? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        rawUrl = rawUrl.Trim();

        // Filter out immediate non-http pseudo-schemes
        if (rawUrl.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            rawUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            rawUrl.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
            rawUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Uri? resolvedUri;

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var baseUri))
            {
                return null;
            }

            if (!Uri.TryCreate(baseUri, rawUrl, out resolvedUri))
            {
                return null;
            }
        }
        else
        {
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out resolvedUri))
            {
                return null;
            }
        }

        // Scheme must be http or https
        if (!SupportedSchemes.Contains(resolvedUri.Scheme.ToLowerInvariant()))
        {
            return null;
        }

        // Construct normalized URL:
        // 1. Lowercase scheme and host
        // 2. Preserve path (strip trailing slash if path != "/")
        // 3. Preserve query string (if present)
        // 4. Strip fragment (#...)
        var builder = new UriBuilder(resolvedUri)
        {
            Scheme = resolvedUri.Scheme.ToLowerInvariant(),
            Host = resolvedUri.Host.ToLowerInvariant(),
            Fragment = string.Empty // Strip fragments
        };

        // Remove standard default ports from URL representation
        if ((builder.Scheme == "http" && builder.Port == 80) ||
            (builder.Scheme == "https" && builder.Port == 443))
        {
            builder.Port = -1;
        }

        // Normalize path: if path ends with / and length > 1, trim trailing slash
        var path = builder.Path;
        if (path.Length > 1 && path.EndsWith('/'))
        {
            builder.Path = path.TrimEnd('/');
        }

        // Clear empty query (? with nothing)
        if (builder.Query == "?")
        {
            builder.Query = string.Empty;
        }

        // AbsoluteUri, not ToString(): ToString() unescapes percent-encoding (e.g. "%26" -> "&"), changing the URL
        return builder.Uri.AbsoluteUri;
    }

    /// <inheritdoc />
    public string ExtractHost(string jobUrl)
    {
        if (string.IsNullOrWhiteSpace(jobUrl))
        {
            throw new ArgumentException("Job URL cannot be empty.", nameof(jobUrl));
        }

        if (!Uri.TryCreate(jobUrl.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid job URL '{jobUrl}'.", nameof(jobUrl));
        }

        return uri.Host.ToLowerInvariant();
    }

    /// <inheritdoc />
    public bool IsSameDomain(string candidateUrl, string targetHost)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl) || string.IsNullOrWhiteSpace(targetHost))
        {
            return false;
        }

        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var candidateHost = uri.Host.ToLowerInvariant();
        targetHost = targetHost.Trim().ToLowerInvariant();

        // Host matches exact or is a valid subdomain (e.g. blog.example.com vs example.com)
        return candidateHost.Equals(targetHost, StringComparison.OrdinalIgnoreCase) ||
               candidateHost.EndsWith("." + targetHost, StringComparison.OrdinalIgnoreCase);
    }
}
