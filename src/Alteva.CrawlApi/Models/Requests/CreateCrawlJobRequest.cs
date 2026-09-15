using System.ComponentModel.DataAnnotations;

namespace Alteva.CrawlApi.Models.Requests;

/// <summary>
/// Request model for submitting a new crawl job.
/// </summary>
public class CreateCrawlJobRequest
{
    /// <summary>
    /// The target absolute website URL to crawl.
    /// </summary>
    [Required(ErrorMessage = "URL is required.")]
    [Url(ErrorMessage = "Must be a valid absolute URL.")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Maximum crawl depth. Defaults to 2 if not specified.
    /// </summary>
    [Range(1, 10, ErrorMessage = "MaxDepth must be between 1 and 10.")]
    public int? MaxDepth { get; set; } = 2;
}
