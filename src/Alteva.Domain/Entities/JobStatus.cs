namespace Alteva.Domain.Entities;

/// <summary>
/// Represents the current status of a web crawl job.
/// </summary>
public enum JobStatus
{
    /// <summary>
    /// The job has been created and queued but is not yet being processed.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// A worker has picked up the job and is currently processing it.
    /// </summary>
    Running = 1,

    /// <summary>
    /// The job completed successfully without encountering fatal errors.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// The job encountered a fatal error (e.g., maximum retries exceeded, malformed URL).
    /// </summary>
    Failed = 3,

    /// <summary>
    /// The job was manually canceled by the user before completion.
    /// </summary>
    Canceled = 4
}
