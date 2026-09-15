using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alteva.Domain.Entities;
using Alteva.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Alteva.Infrastructure.Tests;

public class CrawlStateStoreTests : IDisposable
{
    private const string Root = "https://example.com/";
    private const int MaxPages = 200;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public CrawlStateStoreTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var context = new AppDbContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // A fresh context per operation, like a DI scope per message/request.
    private async Task<T> WithStore<T>(Func<CrawlStateStore, Task<T>> action)
    {
        await using var context = new AppDbContext(_options);
        return await action(new CrawlStateStore(context));
    }

    private async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> query)
    {
        await using var context = new AppDbContext(_options);
        return await query(context);
    }

    private static PageResult Done(Guid jobId, string url, int depth, int maxDepth, IReadOnlyList<string> children, int maxPages = MaxPages) =>
        new(jobId, url, depth, maxDepth, maxPages, PageStatus.Done, 1.0, null, children, children);

    [Fact]
    public async Task CreateJob_InsertsPendingJobWithQueuedRootPage()
    {
        var job = await WithStore(s => s.CreateJobAsync(Root, 2, CancellationToken.None));

        job.Status.Should().Be(JobStatus.Pending);
        var root = await WithDb(db => db.Pages.SingleAsync(p => p.JobId == job.Id));
        root.Url.Should().Be(Root);
        root.Depth.Should().Be(0);
        root.Status.Should().Be(PageStatus.Queued);
        (await WithStore(s => s.GetPageGateAsync(job.Id, Root, CancellationToken.None))).Should().Be(PageGate.Process);
    }

    [Fact]
    public async Task CommitPage_RecordsOutcome_ClaimsNewChildrenOnce_AndStartsJob()
    {
        var job = await WithStore(s => s.CreateJobAsync(Root, 2, CancellationToken.None));
        var links = new[] { "https://example.com/a", "https://example.com/b", "https://example.com/a", Root };

        var commit = await WithStore(s => s.CommitPageAsync(Done(job.Id, Root, 0, 2, links), CancellationToken.None));

        commit.Committed.Should().BeTrue();
        commit.ClaimedChildren.Should().Equal("https://example.com/a", "https://example.com/b");

        var pages = await WithDb(db => db.Pages.Where(p => p.JobId == job.Id).ToListAsync());
        pages.Single(p => p.Url == Root).Status.Should().Be(PageStatus.Done);
        pages.Where(p => p.Url != Root).Should().OnlyContain(p => p.Status == PageStatus.Queued && p.Depth == 1);
        (await WithDb(db => db.Edges.CountAsync(e => e.JobId == job.Id))).Should().Be(3);

        var savedJob = await WithDb(db => db.Jobs.SingleAsync(j => j.Id == job.Id));
        savedJob.Status.Should().Be(JobStatus.Running);
        savedJob.StartedAt.Should().NotBeNull();

        (await WithStore(s => s.GetPageGateAsync(job.Id, Root, CancellationToken.None))).Should().Be(PageGate.AlreadyFinished);
    }

    [Fact]
    public async Task CommitPage_TreatsUrlsDifferingOnlyByCaseAsDifferentPages()
    {
        var job = await WithStore(s => s.CreateJobAsync(Root, 2, CancellationToken.None));

        var commit = await WithStore(s => s.CommitPageAsync(
            Done(job.Id, Root, 0, 2, ["https://example.com/About", "https://example.com/about"]), CancellationToken.None));

        commit.ClaimedChildren.Should().HaveCount(2);
    }

    [Fact]
    public async Task CommitPage_AtMaxDepth_ClaimsNothing_AndCompletesJobWhenNoQueuedPagesRemain()
    {
        var job = await WithStore(s => s.CreateJobAsync(Root, 1, CancellationToken.None));
        await WithStore(s => s.CommitPageAsync(Done(job.Id, Root, 0, 1, ["https://example.com/a"]), CancellationToken.None));

        var commit = await WithStore(s => s.CommitPageAsync(Done(job.Id, "https://example.com/a", 1, 1, ["https://example.com/deeper"]), CancellationToken.None));

        commit.ClaimedChildren.Should().BeEmpty();
        var savedJob = await WithDb(db => db.Jobs.SingleAsync(j => j.Id == job.Id));
        savedJob.Status.Should().Be(JobStatus.Completed);
        savedJob.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CommitPage_StopsClaimingAtPageLimit()
    {
        var job = await WithStore(s => s.CreateJobAsync(Root, 2, CancellationToken.None));

        var commit = await WithStore(s => s.CommitPageAsync(
            Done(job.Id, Root, 0, 2, ["https://example.com/1", "https://example.com/2", "https://example.com/3"], maxPages: 3),
            CancellationToken.None));

        commit.ClaimedChildren.Should().Equal("https://example.com/1", "https://example.com/2");
        (await WithDb(db => db.Pages.CountAsync(p => p.JobId == job.Id))).Should().Be(3);
    }

    [Fact]
    public async Task CommitPage_FailedRoot_FailsJob()
    {
        var job = await WithStore(s => s.CreateJobAsync(Root, 2, CancellationToken.None));

        await WithStore(s => s.CommitPageAsync(
            new PageResult(job.Id, Root, 0, 2, MaxPages, PageStatus.Failed, null, "HTTP 404", [], []), CancellationToken.None));

        var savedJob = await WithDb(db => db.Jobs.SingleAsync(j => j.Id == job.Id));
        savedJob.Status.Should().Be(JobStatus.Failed);
        savedJob.FailureReason.Should().Be("HTTP 404");
    }

    [Fact]
    public async Task CancelJob_SkipsQueuedPages_DiscardsMessages_AndRejectsLaterCommits()
    {
        var job = await WithStore(s => s.CreateJobAsync(Root, 2, CancellationToken.None));
        await WithStore(s => s.CommitPageAsync(Done(job.Id, Root, 0, 2, ["https://example.com/a"]), CancellationToken.None));

        (await WithStore(s => s.CancelJobAsync(job.Id, CancellationToken.None))).Should().BeTrue();

        (await WithStore(s => s.IsJobActiveAsync(job.Id, CancellationToken.None))).Should().BeFalse();
        (await WithStore(s => s.GetPageGateAsync(job.Id, "https://example.com/a", CancellationToken.None))).Should().Be(PageGate.Discard);

        var queuedPage = await WithDb(db => db.Pages.SingleAsync(p => p.JobId == job.Id && p.Depth == 1));
        queuedPage.Status.Should().Be(PageStatus.Skipped);
        queuedPage.FailureReason.Should().Be(CrawlStateStore.CanceledReason);

        // An in-flight page whose commit lands after the cancel writes nothing
        var commit = await WithStore(s => s.CommitPageAsync(Done(job.Id, "https://example.com/a", 1, 2, ["https://example.com/b"]), CancellationToken.None));
        commit.Committed.Should().BeFalse();
        (await WithDb(db => db.Pages.CountAsync(p => p.JobId == job.Id))).Should().Be(2);
        (await WithDb(db => db.Jobs.SingleAsync(j => j.Id == job.Id))).Status.Should().Be(JobStatus.Canceled);

        (await WithStore(s => s.CancelJobAsync(job.Id, CancellationToken.None))).Should().BeFalse();
    }

    [Fact]
    public async Task FailJob_FailsActiveJobWithReason_AndSkipsQueuedPages()
    {
        var job = await WithStore(s => s.CreateJobAsync(Root, 2, CancellationToken.None));

        (await WithStore(s => s.FailJobAsync(job.Id, "queue down", CancellationToken.None))).Should().BeTrue();

        var savedJob = await WithDb(db => db.Jobs.SingleAsync(j => j.Id == job.Id));
        savedJob.Status.Should().Be(JobStatus.Failed);
        savedJob.FailureReason.Should().Be("queue down");
        savedJob.CompletedAt.Should().NotBeNull();
        (await WithDb(db => db.Pages.SingleAsync(p => p.JobId == job.Id))).Status.Should().Be(PageStatus.Skipped);
        (await WithStore(s => s.FailJobAsync(job.Id, "again", CancellationToken.None))).Should().BeFalse();
    }

    [Fact]
    public async Task GetQueuedChildren_ReturnsOnlyStillQueuedChildrenOfParent()
    {
        var job = await WithStore(s => s.CreateJobAsync(Root, 3, CancellationToken.None));
        await WithStore(s => s.CommitPageAsync(Done(job.Id, Root, 0, 3, ["https://example.com/a", "https://example.com/b"]), CancellationToken.None));
        await WithStore(s => s.CommitPageAsync(Done(job.Id, "https://example.com/a", 1, 3, ["https://example.com/c"]), CancellationToken.None));

        var rootChildren = await WithStore(s => s.GetQueuedChildrenAsync(job.Id, Root, CancellationToken.None));

        rootChildren.Should().Equal(("https://example.com/b", 1));
    }

    [Fact]
    public async Task GetPageGate_UnknownJobOrPage_Discards()
    {
        var job = await WithStore(s => s.CreateJobAsync(Root, 2, CancellationToken.None));

        (await WithStore(s => s.GetPageGateAsync(Guid.NewGuid(), Root, CancellationToken.None))).Should().Be(PageGate.Discard);
        (await WithStore(s => s.GetPageGateAsync(job.Id, "https://example.com/unknown", CancellationToken.None))).Should().Be(PageGate.Discard);
    }
}
