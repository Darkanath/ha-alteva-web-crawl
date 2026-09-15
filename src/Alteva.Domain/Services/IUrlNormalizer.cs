namespace Alteva.Domain.Services;

/// <summary>
/// Service for normalizing URLs, resolving relative paths, stripping fragments,
/// and filtering non-HTTP/HTTPS schemes.
/// </summary>
public interface IUrlNormalizer
{
    /// <summary>
    /// Normalizes a candidate URL found on a parent page against the parent page's base URL.
    /// Returns null if the URL is invalid, non-HTTP(S) (e.g. mailto:, tel:, javascript:), or unsupported.
    /// </summary>
    /// <param name="rawUrl">The raw URL string found in an href or user input.</param>
    /// <param name="baseUrl">The base URL of the parent page (or null for root input URLs).</param>
    /// <returns>A normalized absolute URL string, or null if the URL should be ignored.</returns>
    string? Normalize(string rawUrl, string? baseUrl = null);

    /// <summary>
    /// Extracts the canonical domain/host from a starting job URL.
    /// </summary>
    /// <param name="jobUrl">The initial job URL.</param>
    /// <returns>The normalized host string (e.g., "example.com").</returns>
    string ExtractHost(string jobUrl);

    /// <summary>
    /// Determines whether a given URL belongs to the target domain/host.
    /// </summary>
    /// <param name="candidateUrl">The absolute URL to check.</param>
    /// <param name="targetHost">The host of the initial job URL.</param>
    /// <returns>True if the candidate URL is within the target host/domain.</returns>
    bool IsSameDomain(string candidateUrl, string targetHost);
}
