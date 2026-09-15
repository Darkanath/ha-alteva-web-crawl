using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alteva.Domain.Entities;

namespace Alteva.Infrastructure.Data;

/// <summary>
/// What the worker should do with a page message (checked before any delay or download).
/// </summary>
public enum PageGate
{
    /// <summary>The job is active and the page is still Queued: crawl it.</summary>
    Process,

    /// <summary>The job is gone, cancelled or finished, or the page is unknown: ack and drop.</summary>
    Discard,

    /// <summary>The job is active but the page was already finished (redelivery): republish its Queued children, then ack.</summary>
    AlreadyFinished
}

/// <summary>
/// Outcome of crawling one page, to be committed atomically.
/// </summary>
/// <param name="Status">Done, Failed or Skipped.</param>
/// <param name="LinkedUrls">All normalized outgoing links (become edges).</param>
/// <param name="ChildCandidates">Same-domain links eligible to be claimed at <c>Depth + 1</c>.</param>
public sealed record PageResult(
    Guid JobId,
    string Url,
    int Depth,
    int MaxDepth,
    int MaxPages,
    PageStatus Status,
    double? DomainLinkRatio,
    string? FailureReason,
    IReadOnlyList<string> LinkedUrls,
    IReadOnlyList<string> ChildCandidates);

/// <param name="Committed">False when the job was no longer active or the page no longer Queued; nothing was written.</param>
/// <param name="ClaimedChildren">Newly claimed child URLs; the caller publishes a message for each.</param>
public sealed record PageCommit(bool Committed, IReadOnlyList<string> ClaimedChildren);

/// <summary>
/// All crawl-state reads and writes for the recursive page crawl (architecture_notes.md, section 3a).
/// Assumes a single sequential worker.
/// </summary>
public interface ICrawlStateStore
{
    /// <summary>Inserts a Pending job and its Queued root page (depth 0) in one transaction.</summary>
    Task<Job> CreateJobAsync(string normalizedUrl, int maxDepth, CancellationToken cancellationToken);

    Task<PageGate> GetPageGateAsync(Guid jobId, string url, CancellationToken cancellationToken);

    /// <summary>True while the job is Pending or Running. Polled to abort an in-flight page on cancel.</summary>
    Task<bool> IsJobActiveAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: guards that the job is still active (marking it Running), records the page
    /// outcome and edges, claims new children within depth and page limits, and finishes the job
    /// (Failed if the root failed, Completed when no Queued pages remain).
    /// </summary>
    Task<PageCommit> CommitPageAsync(PageResult result, CancellationToken cancellationToken);

    /// <summary>Children linked from <paramref name="parentUrl"/> that are still Queued (republished on redelivery).</summary>
    Task<IReadOnlyList<(string Url, int Depth)>> GetQueuedChildrenAsync(Guid jobId, string parentUrl, CancellationToken cancellationToken);

    /// <summary>Cancels a Pending/Running job and marks its Queued pages Skipped. False if the job is missing or already terminal.</summary>
    Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Fails a Pending/Running job (e.g. its root message could not be published) and marks its Queued pages Skipped. False if the job is missing or already terminal.</summary>
    Task<bool> FailJobAsync(Guid jobId, string reason, CancellationToken cancellationToken);
}
