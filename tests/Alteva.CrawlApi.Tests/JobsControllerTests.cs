using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alteva.CrawlApi.Controllers;
using Alteva.CrawlApi.Models.Requests;
using Alteva.CrawlApi.Models.Responses;
using Alteva.Domain.Entities;
using Alteva.Domain.Models;
using Alteva.Domain.Services;
using Alteva.Infrastructure.Data;
using Alteva.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alteva.CrawlApi.Tests;

public class FakeMessagePublisher : IMessagePublisher
{
    public List<object> PublishedMessages { get; } = [];

    public bool ShouldFail { get; set; }

    public Task PublishAsync<T>(T message, string? routingKey = null, CancellationToken cancellationToken = default)
    {
        if (ShouldFail)
        {
            throw new InvalidOperationException("Broker unavailable");
        }

        if (message != null)
        {
            PublishedMessages.Add(message);
        }
        return Task.CompletedTask;
    }
}

public class JobsControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _dbContext;
    private readonly FakeMessagePublisher _publisher;
    private readonly UrlNormalizer _urlNormalizer;
    private readonly JobTreeBuilder _treeBuilder;
    private readonly JobsController _controller;

    public JobsControllerTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new AppDbContext(options);
        _dbContext.Database.EnsureCreated();

        _publisher = new FakeMessagePublisher();
        _urlNormalizer = new UrlNormalizer();
        _treeBuilder = new JobTreeBuilder();

        _controller = new JobsController(
            _dbContext,
            new CrawlStateStore(_dbContext),
            _publisher,
            _urlNormalizer,
            _treeBuilder,
            NullLogger<JobsController>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateJob_WithValidRequest_ReturnsAcceptedAndPublishesMessage()
    {
        var request = new CreateCrawlJobRequest
        {
            Url = "https://example.com",
            MaxDepth = 3
        };

        var result = await _controller.CreateJob(request, CancellationToken.None);

        var acceptedResult = result as AcceptedAtActionResult;
        acceptedResult.Should().NotBeNull();
        acceptedResult!.StatusCode.Should().Be(202);

        var response = acceptedResult.Value as CreateCrawlJobResponse;
        response.Should().NotBeNull();
        response!.JobId.Should().NotBeEmpty();

        // Verify DB persistence
        var savedJob = await _dbContext.Jobs.FindAsync(response.JobId);
        savedJob.Should().NotBeNull();
        savedJob!.InputUrl.Should().Be("https://example.com/");
        savedJob.MaxDepth.Should().Be(3);
        savedJob.Status.Should().Be(JobStatus.Pending);

        var rootPage = await _dbContext.Pages.SingleAsync(p => p.JobId == response.JobId);
        rootPage.Url.Should().Be("https://example.com/");
        rootPage.Depth.Should().Be(0);
        rootPage.Status.Should().Be(PageStatus.Queued);

        // Verify the root page message was published
        _publisher.PublishedMessages.Should().HaveCount(1);
        var message = _publisher.PublishedMessages[0] as CrawlPageMessage;
        message.Should().NotBeNull();
        message!.JobId.Should().Be(response.JobId);
        message.Url.Should().Be("https://example.com/");
        message.RootUrl.Should().Be("https://example.com/");
        message.Depth.Should().Be(0);
        message.MaxDepth.Should().Be(3);
    }

    [Fact]
    public async Task CreateJob_WhenRootCannotBePublished_Returns503AndFailsJob()
    {
        _publisher.ShouldFail = true;

        var result = await _controller.CreateJob(new CreateCrawlJobRequest { Url = "https://example.com" }, CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(503);

        var job = await _dbContext.Jobs.AsNoTracking().SingleAsync();
        job.Status.Should().Be(JobStatus.Failed);
        job.FailureReason.Should().Be(JobsController.EnqueueFailedReason);
        (await _dbContext.Pages.AsNoTracking().SingleAsync()).Status.Should().Be(PageStatus.Skipped);
    }

    [Fact]
    public async Task CreateJob_WithUnsupportedScheme_ReturnsBadRequest()
    {
        var request = new CreateCrawlJobRequest
        {
            Url = "ftp://files.example.com"
        };

        var result = await _controller.CreateJob(request, CancellationToken.None);

        var badRequest = result as BadRequestObjectResult;
        badRequest.Should().NotBeNull();
        badRequest!.StatusCode.Should().Be(400);

        _publisher.PublishedMessages.Should().BeEmpty();
        (await _dbContext.Jobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetJobById_WhenJobExists_ReturnsJobDetails()
    {
        var jobId = Guid.NewGuid();
        var job = new Job
        {
            Id = jobId,
            InputUrl = "https://example.com/",
            MaxDepth = 2,
            Status = JobStatus.Running,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            StartedAt = DateTime.UtcNow.AddMinutes(-4)
        };
        _dbContext.Jobs.Add(job);
        _dbContext.Pages.AddRange(
            new Page { Id = Guid.NewGuid(), JobId = jobId, Url = "https://example.com/", Status = PageStatus.Done, DomainLinkRatio = 1.0 },
            new Page { Id = Guid.NewGuid(), JobId = jobId, Url = "https://example.com/a", Status = PageStatus.Failed, Depth = 1 },
            new Page { Id = Guid.NewGuid(), JobId = jobId, Url = "https://example.com/b", Status = PageStatus.Queued, Depth = 1 });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.GetJobById(jobId, CancellationToken.None);

        var okResult = result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(200);
        var progress = (CrawlJobDetailsResponse)okResult.Value!;
        progress.PagesDiscovered.Should().Be(3);
        progress.PagesProcessed.Should().Be(2);

        var details = okResult.Value as CrawlJobDetailsResponse;
        details.Should().NotBeNull();
        details!.Id.Should().Be(jobId);
        details.Status.Should().Be(JobStatus.Running);
        details.Tree.Should().BeNull(); // Not completed yet
    }

    [Fact]
    public async Task GetJobById_WhenCompleted_ReturnsHierarchicalTree()
    {
        var jobId = Guid.NewGuid();
        var job = new Job
        {
            Id = jobId,
            InputUrl = "https://example.com/",
            MaxDepth = 2,
            Status = JobStatus.Completed,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = DateTime.UtcNow
        };
        _dbContext.Jobs.Add(job);

        _dbContext.Pages.AddRange(
            new Page { Id = Guid.NewGuid(), JobId = jobId, Url = "https://example.com/", Status = PageStatus.Done, DomainLinkRatio = 1.0 },
            new Page { Id = Guid.NewGuid(), JobId = jobId, Url = "https://example.com/about", Status = PageStatus.Done, DomainLinkRatio = 0.5, Depth = 1 }
        );

        _dbContext.Edges.Add(
            new Edge { JobId = jobId, ParentUrl = "https://example.com/", ChildUrl = "https://example.com/about" }
        );

        await _dbContext.SaveChangesAsync();

        var result = await _controller.GetJobById(jobId, CancellationToken.None);

        var okResult = result as OkObjectResult;
        okResult.Should().NotBeNull();

        var details = okResult!.Value as CrawlJobDetailsResponse;
        details.Should().NotBeNull();
        details!.Tree.Should().NotBeNull();
        details.Tree!.Url.Should().Be("https://example.com/");
        details.Tree.Children.Should().HaveCount(1);
        details.Tree.Children[0].Url.Should().Be("https://example.com/about");
    }

    [Fact]
    public async Task GetJobById_WhenNotFound_ReturnsNotFound()
    {
        var result = await _controller.GetJobById(Guid.NewGuid(), CancellationToken.None);

        var notFound = result as NotFoundObjectResult;
        notFound.Should().NotBeNull();
        notFound!.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetJobHistory_ReturnsPaginatedListSortedByCreatedAtDescending()
    {
        var now = DateTime.UtcNow;
        _dbContext.Jobs.AddRange(
            new Job { Id = Guid.NewGuid(), InputUrl = "https://site1.com", CreatedAt = now.AddMinutes(-30) },
            new Job { Id = Guid.NewGuid(), InputUrl = "https://site2.com", CreatedAt = now.AddMinutes(-10) },
            new Job { Id = Guid.NewGuid(), InputUrl = "https://site3.com", CreatedAt = now.AddMinutes(-5) }
        );
        await _dbContext.SaveChangesAsync();

        var result = await _controller.GetJobHistory(page: 1, pageSize: 2, CancellationToken.None);

        var okResult = result as OkObjectResult;
        okResult.Should().NotBeNull();

        var paginated = okResult!.Value as PaginatedListResponse<CrawlJobSummaryResponse>;
        paginated.Should().NotBeNull();
        paginated!.TotalCount.Should().Be(3);
        paginated.Page.Should().Be(1);
        paginated.PageSize.Should().Be(2);
        paginated.Items.Should().HaveCount(2);

        // Most recent first: site3 (-5m), then site2 (-10m)
        paginated.Items[0].InputUrl.Should().Be("https://site3.com");
        paginated.Items[1].InputUrl.Should().Be("https://site2.com");
    }

    [Fact]
    public async Task CancelJob_WhenPendingOrRunning_CancelsJobSuccessfully()
    {
        var jobId = Guid.NewGuid();
        var job = new Job
        {
            Id = jobId,
            InputUrl = "https://example.com",
            Status = JobStatus.Running
        };
        _dbContext.Jobs.Add(job);
        await _dbContext.SaveChangesAsync();

        _dbContext.Pages.Add(new Page { Id = Guid.NewGuid(), JobId = jobId, Url = "https://example.com/", Status = PageStatus.Queued });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.CancelJob(jobId, CancellationToken.None);

        var okResult = result as OkObjectResult;
        okResult.Should().NotBeNull();

        var updated = await _dbContext.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        updated.Status.Should().Be(JobStatus.Canceled);
        updated.CompletedAt.Should().NotBeNull();
        (await _dbContext.Pages.AsNoTracking().SingleAsync(p => p.JobId == jobId)).Status.Should().Be(PageStatus.Skipped);
    }

    [Fact]
    public async Task CancelJob_WhenAlreadyCompleted_ReturnsBadRequest()
    {
        var jobId = Guid.NewGuid();
        var job = new Job
        {
            Id = jobId,
            InputUrl = "https://example.com",
            Status = JobStatus.Completed
        };
        _dbContext.Jobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var result = await _controller.CancelJob(jobId, CancellationToken.None);

        var badRequest = result as BadRequestObjectResult;
        badRequest.Should().NotBeNull();
        badRequest!.StatusCode.Should().Be(400);
    }
}
