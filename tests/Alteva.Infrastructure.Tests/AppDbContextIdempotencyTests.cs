using System;
using System.Threading.Tasks;
using Alteva.Domain.Entities;
using Alteva.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Alteva.Infrastructure.Tests;

public class AppDbContextIdempotencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public AppDbContextIdempotencyTests()
    {
        // Open an in-memory SQLite connection so schema & unique indexes are preserved for the lifetime of this test
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new AppDbContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task AppDbContext_CanCreateAndRetrieveJob()
    {
        var jobId = Guid.NewGuid();
        using (var context = new AppDbContext(_options))
        {
            var job = new Job
            {
                Id = jobId,
                InputUrl = "https://example.com",
                MaxDepth = 2,
                Status = JobStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };
            context.Jobs.Add(job);
            await context.SaveChangesAsync();
        }

        using (var context = new AppDbContext(_options))
        {
            var savedJob = await context.Jobs.FindAsync(jobId);
            savedJob.Should().NotBeNull();
            savedJob!.InputUrl.Should().Be("https://example.com");
            savedJob.Status.Should().Be(JobStatus.Pending);
        }
    }

    [Fact]
    public async Task Pages_UniqueIndex_PreventsDuplicateUrlPerJob_EnsuringIdempotency()
    {
        var jobId = Guid.NewGuid();
        using var context = new AppDbContext(_options);
        
        var job = new Job
        {
            Id = jobId,
            InputUrl = "https://example.com",
            Status = JobStatus.Running
        };
        context.Jobs.Add(job);
        await context.SaveChangesAsync();

        var page1 = new Page
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            Url = "https://example.com/about",
            DomainLinkRatio = 0.8
        };
        context.Pages.Add(page1);
        await context.SaveChangesAsync();

        // Attempting to add the exact same (JobId, Url) must fail due to unique constraint
        var page2 = new Page
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            Url = "https://example.com/about",
            DomainLinkRatio = 0.8
        };
        context.Pages.Add(page2);

        var act = async () => await context.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.WithInnerException<SqliteException>()
            .WithMessage("*UNIQUE constraint failed: Pages.JobId, Pages.Url*");
    }

    [Fact]
    public async Task Edges_UniqueIndex_PreventsDuplicateEdgePerJob_EnsuringIdempotency()
    {
        var jobId = Guid.NewGuid();
        using var context = new AppDbContext(_options);
        
        var job = new Job
        {
            Id = jobId,
            InputUrl = "https://example.com",
            Status = JobStatus.Running
        };
        context.Jobs.Add(job);
        await context.SaveChangesAsync();

        var edge1 = new Edge
        {
            JobId = jobId,
            ParentUrl = "https://example.com/",
            ChildUrl = "https://example.com/about"
        };
        context.Edges.Add(edge1);
        await context.SaveChangesAsync();

        // Duplicate edge for same job
        var edge2 = new Edge
        {
            JobId = jobId,
            ParentUrl = "https://example.com/",
            ChildUrl = "https://example.com/about"
        };
        context.Edges.Add(edge2);

        var act = async () => await context.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.WithInnerException<SqliteException>()
            .WithMessage("*UNIQUE constraint failed: Edges.JobId, Edges.ParentUrl, Edges.ChildUrl*");
    }

    [Fact]
    public async Task CascadeDelete_WhenJobIsDeleted_PagesAndEdgesAreAlsoDeleted()
    {
        var jobId = Guid.NewGuid();
        using (var context = new AppDbContext(_options))
        {
            var job = new Job
            {
                Id = jobId,
                InputUrl = "https://example.com",
                Status = JobStatus.Completed
            };
            context.Jobs.Add(job);
            context.Pages.Add(new Page { Id = Guid.NewGuid(), JobId = jobId, Url = "https://example.com/", DomainLinkRatio = 1.0 });
            context.Edges.Add(new Edge { JobId = jobId, ParentUrl = "https://example.com/", ChildUrl = "https://example.com/a" });
            await context.SaveChangesAsync();
        }

        using (var context = new AppDbContext(_options))
        {
            var job = await context.Jobs.FindAsync(jobId);
            context.Jobs.Remove(job!);
            await context.SaveChangesAsync();
        }

        using (var context = new AppDbContext(_options))
        {
            var pages = await context.Pages.ToListAsync();
            var edges = await context.Edges.ToListAsync();
            pages.Should().BeEmpty();
            edges.Should().BeEmpty();
        }
    }
}
