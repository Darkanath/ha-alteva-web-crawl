using System;
using System.Threading;
using System.Threading.Tasks;
using Alteva.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Alteva.CrawlWorker.Services;

public class RetryTracker(AppDbContext dbContext) : IRetryTracker
{
    public const int DefaultMaxRetryAttempts = 3;

    public int MaxRetryAttempts { get; } = DefaultMaxRetryAttempts;

    public async Task<int> RegisterFailureAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job == null)
        {
            return MaxRetryAttempts;
        }

        job.RetryCount += 1;
        await dbContext.SaveChangesAsync(cancellationToken);
        return job.RetryCount;
    }

    public bool IsExhausted(int retryCount) => retryCount >= MaxRetryAttempts;
}
