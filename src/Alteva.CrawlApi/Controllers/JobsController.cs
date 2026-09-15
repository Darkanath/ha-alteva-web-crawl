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
    ICrawlStateStore crawlStateStore,
    IMessagePublisher messagePublisher,
    IUrlNormalizer urlNormalizer,
    IJobTreeBuilder treeBuilder,
    ILogger<JobsController> logger) : ControllerBase
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ICrawlStateStore _crawlStateStore = crawlStateStore ?? throw new ArgumentNullException(nameof(crawlStateStore));
    private readonly IMessagePublisher _messagePublisher = messagePublisher ?? throw new ArgumentNullException(nameof(messagePublisher));
    private readonly IUrlNormalizer _urlNormalizer = urlNormalizer ?? throw new ArgumentNullException(nameof(urlNormalizer));
    private readonly IJobTreeBuilder _treeBuilder = treeBuilder ?? throw new ArgumentNullException(nameof(treeBuilder));
    private readonly ILogger<JobsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public const string EnqueueFailedReason = "The crawl could not be queued (message broker unavailable).";

    /// <summary>
    /// Submits a new crawl job.
    /// </summary>
    /// <param name="request">Job creation options including target URL and optional max depth.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The newly created job ID.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(CreateCrawlJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
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

        var job = await _crawlStateStore.CreateJobAsync(normalizedUrl, request.MaxDepth ?? 2, cancellationToken);

        _logger.LogInformation("Job {JobId} registered with status {Status} for URL {Url}", job.Id, job.Status, job.InputUrl);

        // Publish the root page; the worker recursively publishes its children
        try
        {
            await _messagePublisher.PublishAsync(new CrawlPageMessage
            {
                JobId = job.Id,
                Url = job.InputUrl,
                Depth = 0,
                MaxDepth = job.MaxDepth,
                RootUrl = job.InputUrl
            }, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            // Nothing will ever process the job without its root message, so end it rather than leave it Pending.
            _logger.LogError(ex, "Failed to publish the root page for Job {JobId}. Marking the job Failed.", job.Id);
            await _crawlStateStore.FailJobAsync(job.Id, EnqueueFailedReason, CancellationToken.None);

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Crawl Queue Unavailable",
                Detail = $"Job '{job.Id}' could not be queued and was marked Failed. Please try again later.",
                Status = StatusCodes.Status503ServiceUnavailable
            });
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

        var pageCounts = await _dbContext.Pages
            .Where(p => p.JobId == id)
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        JobTreeNode? tree = null;
        if (job.Status == JobStatus.Completed)
        {
            var pages = await _dbContext.Pages
                .AsNoTracking()
                .Where(p => p.JobId == id)
                .ToListAsync(cancellationToken);

            // Insertion order = discovery order, which the tree uses to pick each page's parent
            var edges = await _dbContext.Edges
                .AsNoTracking()
                .Where(e => e.JobId == id)
                .OrderBy(e => e.Id)
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
            PagesDiscovered = pageCounts.Sum(c => c.Count),
            PagesProcessed = pageCounts.Where(c => c.Status != PageStatus.Queued).Sum(c => c.Count),
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
        var status = await _dbContext.Jobs
            .Where(j => j.Id == id)
            .Select(j => (JobStatus?)j.Status)
            .FirstOrDefaultAsync(cancellationToken);
        if (status == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Job Not Found",
                Detail = $"Job with ID '{id}' was not found.",
                Status = StatusCodes.Status404NotFound
            });
        }

        // Atomic: only a Pending/Running job is cancelled, and its Queued pages are skipped with it
        if (!await _crawlStateStore.CancelJobAsync(id, cancellationToken))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid Operation",
                Detail = $"Job with ID '{id}' is already in terminal state '{status}' and cannot be canceled.",
                Status = StatusCodes.Status400BadRequest
            });
        }

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
