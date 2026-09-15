using System;

namespace Alteva.CrawlApi.Models.Responses;

/// <summary>
/// Response returned upon successful submission of a crawl job.
/// </summary>
public class CreateCrawlJobResponse
{
    public Guid JobId { get; set; }
}
