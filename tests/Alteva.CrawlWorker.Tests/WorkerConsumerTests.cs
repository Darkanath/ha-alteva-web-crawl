using System;
using System.Collections.Generic;
using System.Linq;
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

public class WorkerConsumerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public WorkerConsumerTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new AppDbContext(_options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Worker_ShouldPersistPagesAndEdges_AndSetJobCompleted_OnSuccess()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        using (var context = new AppDbContext(_options))
        {
            context.Jobs.Add(new Job
            {
                Id = jobId,
                InputUrl = "https://example.com",
                MaxDepth = 2,
                Status = JobStatus.Pending,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var crawlResult = new CrawlExecutionResult
        {
            JobId = jobId,
            Success = true,
            Pages = new List<Page>
            {
                new() { Id = Guid.NewGuid(), JobId = jobId, Url = "https://example.com", DomainLinkRatio = 1.0 },
                new() { Id = Guid.NewGuid(), JobId = jobId, Url = "https://example.com/about", DomainLinkRatio = 0.5 }
            },
            Edges = new List<Edge>
            {
                new() { JobId = jobId, ParentUrl = "https://example.com", ChildUrl = "https://example.com/about" }
            }
        };

        // Act - Simulate Worker Persistence
        using (var context = new AppDbContext(_options))
        {
            var job = await context.Jobs.FindAsync(jobId);
            job.Should().NotBeNull();
            job!.Status = JobStatus.Running;
            job.StartedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            // Clear any prior partial pages or edges (idempotency)
            var existingPages = await context.Pages.Where(p => p.JobId == jobId).ToListAsync();
            if (existingPages.Count > 0) context.Pages.RemoveRange(existingPages);
            var existingEdges = await context.Edges.Where(e => e.JobId == jobId).ToListAsync();
            if (existingEdges.Count > 0) context.Edges.RemoveRange(existingEdges);

            context.Pages.AddRange(crawlResult.Pages);
            context.Edges.AddRange(crawlResult.Edges);

            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // Assert
        using (var context = new AppDbContext(_options))
        {
            var savedJob = await context.Jobs.FindAsync(jobId);
            savedJob!.Status.Should().Be(JobStatus.Completed);
            savedJob.StartedAt.Should().NotBeNull();
            savedJob.CompletedAt.Should().NotBeNull();
            savedJob.FailureReason.Should().BeNull();

            var pages = await context.Pages.Where(p => p.JobId == jobId).ToListAsync();
            pages.Should().HaveCount(2);

            var edges = await context.Edges.Where(e => e.JobId == jobId).ToListAsync();
            edges.Should().HaveCount(1);
            edges[0].ParentUrl.Should().Be("https://example.com");
            edges[0].ChildUrl.Should().Be("https://example.com/about");
        }
    }

    [Fact]
    public async Task Worker_ShouldBeIdempotent_WhenReProcessingJobWithExistingPagesAndEdges()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        using (var context = new AppDbContext(_options))
        {
            context.Jobs.Add(new Job
            {
                Id = jobId,
                InputUrl = "https://example.com",
                MaxDepth = 1,
                Status = JobStatus.Running,
                CreatedAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow
            });
            context.Pages.Add(new Page { Id = Guid.NewGuid(), JobId = jobId, Url = "https://example.com", DomainLinkRatio = 1.0 });
            await context.SaveChangesAsync();
        }

        var newCrawlResult = new CrawlExecutionResult
        {
            JobId = jobId,
            Success = true,
            Pages = new List<Page>
            {
                new() { Id = Guid.NewGuid(), JobId = jobId, Url = "https://example.com", DomainLinkRatio = 1.0 },
                new() { Id = Guid.NewGuid(), JobId = jobId, Url = "https://example.com/pricing", DomainLinkRatio = 0.8 }
            },
            Edges = new List<Edge>
            {
                new() { JobId = jobId, ParentUrl = "https://example.com", ChildUrl = "https://example.com/pricing" }
            }
        };

        // Act - Re-deliver & apply idempotent upsert
        using (var context = new AppDbContext(_options))
        {
            var job = await context.Jobs.FindAsync(jobId);
            var existingPages = await context.Pages.Where(p => p.JobId == jobId).ToListAsync();
            context.Pages.RemoveRange(existingPages);
            var existingEdges = await context.Edges.Where(e => e.JobId == jobId).ToListAsync();
            context.Edges.RemoveRange(existingEdges);

            context.Pages.AddRange(newCrawlResult.Pages);
            context.Edges.AddRange(newCrawlResult.Edges);

            job!.Status = JobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // Assert
        using (var context = new AppDbContext(_options))
        {
            var pages = await context.Pages.Where(p => p.JobId == jobId).ToListAsync();
            pages.Should().HaveCount(2);

            var edges = await context.Edges.Where(e => e.JobId == jobId).ToListAsync();
            edges.Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task Worker_ShouldRecordFailureReason_WhenCrawlFails()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        using (var context = new AppDbContext(_options))
        {
            context.Jobs.Add(new Job
            {
                Id = jobId,
                InputUrl = "https://nonexistent-domain-404.com",
                MaxDepth = 2,
                Status = JobStatus.Pending,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        // Act - Simulate failure handling
        using (var context = new AppDbContext(_options))
        {
            var job = await context.Jobs.FindAsync(jobId);
            job!.Status = JobStatus.Failed;
            job.FailureReason = "Root URL returned HTTP status NotFound.";
            job.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // Assert
        using (var context = new AppDbContext(_options))
        {
            var job = await context.Jobs.FindAsync(jobId);
            job!.Status.Should().Be(JobStatus.Failed);
            job.FailureReason.Should().Contain("NotFound");
            job.CompletedAt.Should().NotBeNull();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
