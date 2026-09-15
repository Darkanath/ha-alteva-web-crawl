using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alteva.CrawlWorker.Services;

/// <summary>
/// Tracks transient processing failures per Job so the worker can decide when to
/// stop requeueing a message and route it to the dead-letter queue instead.
/// Retry attempts are persisted on the Job row rather than relying on the
/// RabbitMQ "x-delivery-count" header, which is only populated for quorum queues.
/// </summary>
public interface IRetryTracker
{
    /// <summary>
    /// Maximum number of transient failures allowed before a message is dead-lettered.
    /// </summary>
    int MaxRetryAttempts { get; }

    /// <summary>
    /// Records a transient processing failure for the given job and returns the
    /// updated retry count. If the job cannot be found, the max attempt count is
    /// returned so the caller routes straight to the dead-letter queue rather than
    /// requeueing indefinitely for an untrackable job.
    /// </summary>
    Task<int> RegisterFailureAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns true when the given retry count has reached or exceeded the max attempts.
    /// </summary>
    bool IsExhausted(int retryCount);
}
