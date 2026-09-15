using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alteva.Domain.Entities;
using Alteva.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Alteva.Infrastructure.Data;

public class CrawlStateStore(AppDbContext dbContext) : ICrawlStateStore
{
    public const string CanceledReason = "Job canceled";

    public async Task<Job> CreateJobAsync(string normalizedUrl, int maxDepth, CancellationToken cancellationToken)
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            InputUrl = normalizedUrl,
            MaxDepth = maxDepth,
            Status = JobStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        dbContext.Jobs.Add(job);
        dbContext.Pages.Add(new Page { Id = Guid.NewGuid(), JobId = job.Id, Url = normalizedUrl, Depth = 0 });
        await dbContext.SaveChangesAsync(cancellationToken);

        return job;
    }

    public async Task<PageGate> GetPageGateAsync(Guid jobId, string url, CancellationToken cancellationToken)
    {
        if (!await IsJobActiveAsync(jobId, cancellationToken))
        {
            return PageGate.Discard;
        }

        var urlHash = UrlHasher.Hash(url);
        var status = await dbContext.Pages
            .Where(p => p.JobId == jobId && p.UrlHash == urlHash)
            .Select(p => (PageStatus?)p.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status switch
        {
            null => PageGate.Discard,
            PageStatus.Queued => PageGate.Process,
            _ => PageGate.AlreadyFinished
        };
    }

    public Task<bool> IsJobActiveAsync(Guid jobId, CancellationToken cancellationToken)
    {
        return dbContext.Jobs.AnyAsync(
            j => j.Id == jobId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running),
            cancellationToken);
    }

    public async Task<PageCommit> CommitPageAsync(PageResult result, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;

        // Guard first: on SQL Server this also locks the job row until commit, so a concurrent
        // cancel waits for this transaction instead of interleaving with it.
        var activated = await dbContext.Jobs
            .Where(j => j.Id == result.JobId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.StartedAt, j => j.StartedAt ?? now), cancellationToken);
        if (activated == 0)
        {
            return new PageCommit(false, []);
        }

        var urlHash = UrlHasher.Hash(result.Url);
        var page = await dbContext.Pages
            .FirstOrDefaultAsync(p => p.JobId == result.JobId && p.UrlHash == urlHash, cancellationToken);
        if (page is not { Status: PageStatus.Queued })
        {
            return new PageCommit(false, []);
        }

        page.Status = result.Status;
        page.DomainLinkRatio = result.DomainLinkRatio;
        page.FailureReason = result.FailureReason;

        foreach (var childUrl in result.LinkedUrls.Distinct(StringComparer.Ordinal))
        {
            dbContext.Edges.Add(new Edge { JobId = result.JobId, ParentUrl = result.Url, ChildUrl = childUrl });
        }

        var claimed = new List<string>();
        if (result.Status == PageStatus.Done && result.Depth < result.MaxDepth)
        {
            // Bounded by MaxPages, so loading the job's URLs is cheap and keeps the comparison
            // case-sensitive (the Url column's collation is not).
            var existingUrls = (await dbContext.Pages
                    .Where(p => p.JobId == result.JobId)
                    .Select(p => p.Url)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

            var remainingBudget = result.MaxPages - existingUrls.Count;
            foreach (var candidate in result.ChildCandidates)
            {
                if (remainingBudget <= 0)
                {
                    break;
                }

                if (existingUrls.Add(candidate))
                {
                    dbContext.Pages.Add(new Page { Id = Guid.NewGuid(), JobId = result.JobId, Url = candidate, Depth = result.Depth + 1 });
                    claimed.Add(candidate);
                    remainingBudget--;
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (result.Depth == 0 && result.Status == PageStatus.Failed)
        {
            await dbContext.Jobs
                .Where(j => j.Id == result.JobId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Failed)
                    .SetProperty(j => j.FailureReason, result.FailureReason)
                    .SetProperty(j => j.CompletedAt, now), cancellationToken);
        }
        else if (!await dbContext.Pages.AnyAsync(p => p.JobId == result.JobId && p.Status == PageStatus.Queued, cancellationToken))
        {
            await dbContext.Jobs
                .Where(j => j.Id == result.JobId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Completed)
                    .SetProperty(j => j.CompletedAt, now), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new PageCommit(true, claimed);
    }

    public async Task<IReadOnlyList<(string Url, int Depth)>> GetQueuedChildrenAsync(Guid jobId, string parentUrl, CancellationToken cancellationToken)
    {
        // Filter URLs in memory: SQL string comparison is case-insensitive under the default collation.
        var childUrls = (await dbContext.Edges
                .Where(e => e.JobId == jobId && e.ParentUrl == parentUrl)
                .Select(e => new { e.ParentUrl, e.ChildUrl })
                .ToListAsync(cancellationToken))
            .Where(e => e.ParentUrl == parentUrl)
            .Select(e => e.ChildUrl)
            .ToHashSet(StringComparer.Ordinal);

        var queuedPages = await dbContext.Pages
            .Where(p => p.JobId == jobId && p.Status == PageStatus.Queued)
            .Select(p => new { p.Url, p.Depth })
            .ToListAsync(cancellationToken);

        return queuedPages
            .Where(p => childUrls.Contains(p.Url))
            .Select(p => (p.Url, p.Depth))
            .ToList();
    }

    public async Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;

        var canceled = await dbContext.Jobs
            .Where(j => j.Id == jobId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Canceled)
                .SetProperty(j => j.CompletedAt, now), cancellationToken);
        if (canceled == 0)
        {
            return false;
        }

        await dbContext.Pages
            .Where(p => p.JobId == jobId && p.Status == PageStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PageStatus.Skipped)
                .SetProperty(p => p.FailureReason, CanceledReason), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
