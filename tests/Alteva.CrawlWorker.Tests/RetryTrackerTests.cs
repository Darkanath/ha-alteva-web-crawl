using System;
using System.Threading;
using System.Threading.Tasks;
using Alteva.CrawlWorker.Services;
using Alteva.Domain.Entities;
using Alteva.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Alteva.CrawlWorker.Tests;

/// <summary>
/// Covers the retry-exhaustion regression: on a classic RabbitMQ queue, RabbitMQ never
/// populates the "x-delivery-count" header, so a worker that reads that header always
/// sees 0 and requeues a failing message forever instead of ever reaching the DLQ.
/// RetryTracker fixes this by tracking attempts on the Job row itself.
/// </summary>
public class RetryTrackerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public RetryTrackerTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new AppDbContext(_options);
        context.Database.EnsureCreated();
    }

    private async Task<Guid> SeedRunningJobAsync()
    {
        var jobId = Guid.NewGuid();
        using var context = new AppDbContext(_options);
        context.Jobs.Add(new Job
        {
            Id = jobId,
            InputUrl = "https://example.com",
            MaxDepth = 2,
            Status = JobStatus.Running,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return jobId;
    }

    [Fact]
    public async Task RegisterFailureAsync_ShouldIncrementAndPersist_RetryCountOnTheJobRow()
    {
        var jobId = await SeedRunningJobAsync();

        using var context = new AppDbContext(_options);
        var tracker = new RetryTracker(context);

        (await tracker.RegisterFailureAsync(jobId, CancellationToken.None)).Should().Be(1);
        (await tracker.RegisterFailureAsync(jobId, CancellationToken.None)).Should().Be(2);

        using var verifyContext = new AppDbContext(_options);
        var job = await verifyContext.Jobs.FindAsync(jobId);
        job!.RetryCount.Should().Be(2);
    }

    [Fact]
    public async Task RetryExhaustion_ShouldRequeueBelowLimit_ThenExhaustAtMaxRetryAttempts_SoWorkerRoutesToDlq()
    {
        var jobId = await SeedRunningJobAsync();

        using var context = new AppDbContext(_options);
        var tracker = new RetryTracker(context);

        // Simulate consecutive redeliveries of the same failing message, the way a
        // classic queue actually redelivers (no broker-tracked delivery count).
        var retryCount = 0;
        for (var attempt = 1; attempt < tracker.MaxRetryAttempts; attempt++)
        {
            retryCount = await tracker.RegisterFailureAsync(jobId, CancellationToken.None);
            tracker.IsExhausted(retryCount).Should()
                .BeFalse($"attempt {attempt} is below MaxRetryAttempts ({tracker.MaxRetryAttempts}) and should requeue, not dead-letter");
        }

        // One more failure reaches the limit.
        retryCount = await tracker.RegisterFailureAsync(jobId, CancellationToken.None);

        retryCount.Should().Be(tracker.MaxRetryAttempts);
        tracker.IsExhausted(retryCount).Should()
            .BeTrue("the retry budget is exhausted, so the worker must nack with requeue:false and route the message to the DLQ");
    }

    [Fact]
    public async Task RegisterFailureAsync_ShouldForceExhaustion_WhenJobRowIsMissing()
    {
        using var context = new AppDbContext(_options);
        var tracker = new RetryTracker(context);

        var retryCount = await tracker.RegisterFailureAsync(Guid.NewGuid(), CancellationToken.None);

        retryCount.Should().Be(tracker.MaxRetryAttempts);
        tracker.IsExhausted(retryCount).Should().BeTrue("an untrackable job should route to DLQ rather than requeue forever");
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
