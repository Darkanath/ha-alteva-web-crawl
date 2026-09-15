using System;

namespace Alteva.Domain.Entities;

/// <summary>
/// Represents a single web crawl job requested by a user.
/// Acts as the aggregate root for Pages and Edges associated with this crawl.
/// </summary>
public class Job
{
    /// <summary>
    /// Unique identifier for the job (GUID).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The starting URL provided by the user. Must be an absolute URL.
    /// </summary>
    public string InputUrl { get; set; } = string.Empty;

    /// <summary>
    /// The maximum link depth the crawler should traverse. 
    /// Default is usually 2, as per requirements.
    /// </summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>
    /// The current operational state of the job.
    /// </summary>
    public JobStatus Status { get; set; } = JobStatus.Pending;

    /// <summary>
    /// UTC timestamp indicating when the job was originally requested.
    /// Used for sorting history (most recent first).
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp indicating when a worker first began processing the job.
    /// Null if the job is still pending.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// UTC timestamp indicating when the job reached a terminal state (Completed, Failed, Canceled).
    /// Null if the job is still pending or running.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Contains details about why a job failed (e.g., DNS resolution failed, 404 on root).
    /// Null if the job has not failed.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Number of transient processing failures recorded for this job's current delivery.
    /// Tracked on the row itself rather than a broker header, since the classic RabbitMQ
    /// queue used for crawl jobs does not populate x-delivery-count. Used by the worker
    /// to decide when to stop requeueing and route the message to the dead-letter queue.
    /// </summary>
    public int RetryCount { get; set; }
}
