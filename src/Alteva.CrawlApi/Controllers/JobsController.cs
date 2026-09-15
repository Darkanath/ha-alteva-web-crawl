using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alteva.CrawlApi.Models.Requests;
using Alteva.CrawlApi.Models.Responses;
using Alteva.Domain.Entities;
using Alteva.Domain.Models;
using Alteva.Domain.Services;
using Alteva.Infrastructure.Data;
using Alteva.Infrastructure.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Alteva.CrawlApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class JobsController(
    AppDbContext dbContext,
    IMessagePublisher messagePublisher,
    IUrlNormalizer urlNormalizer,
    IJobTreeBuilder treeBuilder,
    ILogger<JobsController> logger) : ControllerBase
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IMessagePublisher _messagePublisher = messagePublisher ?? throw new ArgumentNullException(nameof(messagePublisher));
    private readonly IUrlNormalizer _urlNormalizer = urlNormalizer ?? throw new ArgumentNullException(nameof(urlNormalizer));
    private readonly IJobTreeBuilder _treeBuilder = treeBuilder ?? throw new ArgumentNullException(nameof(treeBuilder));
    private readonly ILogger<JobsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Submits a new crawl job.
    /// </summary>
    /// <param name="request">Job creation options including target URL and optional max depth.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The newly created job ID.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(CreateCrawlJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateJob(
        [FromBody] CreateCrawlJobRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var normalizedUrl = _urlNormalizer.Normalize(request.Url);
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid URL",
                Detail = "The provided URL is invalid or uses an unsupported scheme. Only HTTP and HTTPS are supported.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var job = new Job
        {
            Id = Guid.NewGuid(),
            InputUrl = normalizedUrl,
            MaxDepth = request.MaxDepth ?? 2,
            Status = JobStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Jobs.Add(job);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Job {JobId} registered with status {Status} for URL {Url}", job.Id, job.Status, job.InputUrl);

        // Publish event to message broker for background processing
        try
        {
            await _messagePublisher.PublishAsync(new CrawlJobRequestedMessage
            {
                JobId = job.Id,
                InputUrl = job.InputUrl,
                MaxDepth = job.MaxDepth,
                SubmittedAt = job.CreatedAt
            }, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish job event for Job {JobId}. Job remains in Pending status.", job.Id);
            // We still return 202 Accepted because the job record exists in the DB and can be picked up by a recovery worker
        }

        return AcceptedAtAction(
            nameof(GetJobById),
            new { id = job.Id },
            new CreateCrawlJobResponse { JobId = job.Id });
    }

    /// <summary>
    /// Retrieves the status, details, and hierarchical results of a specific crawl job.
    /// </summary>
    /// <param name="id">The unique GUID of the job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(CrawlJobDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobById(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var job = await _dbContext.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Job Not Found",
                Detail = $"Job with ID '{id}' was not found.",
                Status = StatusCodes.Status404NotFound
            });
        }

        JobTreeNode? tree = null;
        if (job.Status == JobStatus.Completed)
        {
            var pages = await _dbContext.Pages
                .AsNoTracking()
                .Where(p => p.JobId == id)
                .ToListAsync(cancellationToken);

            var edges = await _dbContext.Edges
                .AsNoTracking()
                .Where(e => e.JobId == id)
                .ToListAsync(cancellationToken);

            tree = _treeBuilder.BuildTree(job.InputUrl, pages, edges);
        }

        var response = new CrawlJobDetailsResponse
        {
            Id = job.Id,
            InputUrl = job.InputUrl,
            MaxDepth = job.MaxDepth,
            Status = job.Status,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            FailureReason = job.FailureReason,
            Tree = tree
        };

        return Ok(response);
    }

    /// <summary>
    /// Returns a paginated history of past crawl jobs, sorted most recent first.
    /// </summary>
    /// <param name="page">Page number (1-based, default: 1).</param>
    /// <param name="pageSize">Page size (default: 20, max: 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(PaginatedListResponse<CrawlJobSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetJobHistory(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _dbContext.Jobs.AsNoTracking();

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(j => j.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(j => new CrawlJobSummaryResponse
            {
                Id = j.Id,
                InputUrl = j.InputUrl,
                MaxDepth = j.MaxDepth,
                Status = j.Status,
                CreatedAt = j.CreatedAt,
                StartedAt = j.StartedAt,
                CompletedAt = j.CompletedAt,
                FailureReason = j.FailureReason
            })
            .ToListAsync(cancellationToken);

        return Ok(new PaginatedListResponse<CrawlJobSummaryResponse>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    /// <summary>
    /// Cancels an active or pending crawl job.
    /// </summary>
    /// <param name="id">The unique GUID of the job to cancel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CancelJob(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var job = await _dbContext.Jobs.FindAsync([id], cancellationToken);
        if (job == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Job Not Found",
                Detail = $"Job with ID '{id}' was not found.",
                Status = StatusCodes.Status404NotFound
            });
        }

        if (job.Status == JobStatus.Completed || job.Status == JobStatus.Failed || job.Status == JobStatus.Canceled)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid Operation",
                Detail = $"Job with ID '{id}' is already in terminal state '{job.Status}' and cannot be canceled.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        job.Status = JobStatus.Canceled;
        job.CompletedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Job {JobId} was canceled by user.", id);

        return Ok(new { message = "Job canceled successfully." });
    }

    /// <summary>
    /// Deletes a crawl job and all associated pages and edges.
    /// Only allows deletion of jobs in terminal states (Completed, Failed, Canceled).
    /// </summary>
    /// <param name="id">The unique GUID of the job to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteJob(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var job = await _dbContext.Jobs.FindAsync([id], cancellationToken);
        if (job == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Job Not Found",
                Detail = $"Job with ID '{id}' was not found.",
                Status = StatusCodes.Status404NotFound
            });
        }

        if (job.Status == JobStatus.Pending || job.Status == JobStatus.Running)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid Operation",
                Detail = $"Job with ID '{id}' is currently '{job.Status}'. Active jobs must be canceled before they can be deleted.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        _dbContext.Jobs.Remove(job);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Job {JobId} and its associated data were deleted by user.", id);

        return NoContent();
    }
}
