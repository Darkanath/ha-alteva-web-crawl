using System;
using Alteva.Domain.Services;

namespace Alteva.Domain.Entities;

/// <summary>
/// Represents a single parsed HTML page discovered during a crawl job.
/// </summary>
public class Page
{
    private string _url = string.Empty;

    /// <summary>
    /// Unique identifier for this page record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The ID of the Job this page belongs to.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// The normalized absolute URL of the page. Setting it also sets <see cref="UrlHash"/>.
    /// </summary>
    public string Url
    {
        get => _url;
        set
        {
            _url = value;
            UrlHash = UrlHasher.Hash(value);
        }
    }

    /// <summary>
    /// SHA-256 of <see cref="Url"/> (see <see cref="UrlHasher"/>). Part of the composite unique
    /// key (JobId, UrlHash), which ensures a URL is claimed only once per job.
    /// </summary>
    public byte[] UrlHash { get; private set; } = UrlHasher.Hash(string.Empty);

    /// <summary>
    /// The calculated ratio of outgoing links that remain on the same domain
    /// versus the total number of outgoing links on this page.
    /// Null until the page has been fetched and parsed (<see cref="PageStatus.Done"/>).
    /// </summary>
    public double? DomainLinkRatio { get; set; }

    /// <summary>
    /// Link distance from the job's root URL (root = 0). Messages are processed FIFO by a single
    /// worker, so a page is always first discovered at its shortest depth.
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    /// Current crawl state of this page. Rows are inserted as <see cref="PageStatus.Queued"/> when claimed.
    /// </summary>
    public PageStatus Status { get; set; } = PageStatus.Queued;

    /// <summary>
    /// Why the page ended up <see cref="PageStatus.Failed"/> or <see cref="PageStatus.Skipped"/>.
    /// </summary>
    public string? FailureReason { get; set; }
}
