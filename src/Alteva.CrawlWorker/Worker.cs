using System.Text;
using System.Text.Json;
using Alteva.CrawlWorker.Services;
using Alteva.Domain.Entities;
using Alteva.Domain.Models;
using Alteva.Infrastructure.Data;
using Alteva.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Alteva.CrawlWorker;

/// <summary>
/// Background worker service that consumes crawl requests from RabbitMQ,
/// orchestrates the CrawlerEngine, and persists results idempotently to SQL Server.
/// </summary>
public class Worker : BackgroundService
{
    private readonly IOptions<RabbitMQOptions> _rabbitOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly ILogger<Worker> _logger;

    private IConnection? _connection;
    private IModel? _channel;
    private const int MaxLoggedPayloadLength = 500;

    public Worker(
        IOptions<RabbitMQOptions> rabbitOptions,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<Worker> logger)
    {
        _rabbitOptions = rabbitOptions ?? throw new ArgumentNullException(nameof(rabbitOptions));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _hostApplicationLifetime = hostApplicationLifetime ?? throw new ArgumentNullException(nameof(hostApplicationLifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Alteva Crawl Worker service...");

        // Connect with retry to handle RabbitMQ container startup latency
        await ConnectWithRetryAsync(stoppingToken);

        if (_channel == null || stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var options = _rabbitOptions.Value;

        RabbitMQTopology.Declare(_channel, options);

        // One message at a time: prefetch 1, and the handler awaits the whole crawl before the
        // dispatcher delivers the next message.
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += (sender, ea) => ProcessMessageSafelyAsync(ea, stoppingToken);
        consumer.ConsumerCancelled += OnConsumerCancelledAsync;

        _channel.BasicConsume(
            queue: options.QueueName,
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation("Alteva Crawl Worker listening for messages on queue '{QueueName}'.", options.QueueName);

        // Keep the background task alive until cancellation requested
        var tcs = new TaskCompletionSource<bool>();
        stoppingToken.Register(() => tcs.TrySetResult(true));
        await tcs.Task;

        _logger.LogInformation("Alteva Crawl Worker shutting down gracefully...");
    }

    private async Task ProcessMessageSafelyAsync(BasicDeliverEventArgs ea, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessMessageAsync(ea, stoppingToken);
        }
        catch (Exception ex)
        {
            // ProcessMessageAsync already acks/nacks on every path it expects; this is a
            // last-resort guard so a truly unexpected exception can't crash the dispatch loop.
            _logger.LogError(ex, "Unexpected exception escaped ProcessMessageAsync for delivery {DeliveryTag}.", ea.DeliveryTag);
        }
    }

    private async Task ProcessMessageAsync(BasicDeliverEventArgs ea, CancellationToken stoppingToken)
    {
        var deliveryTag = ea.DeliveryTag;
        string rawJson = string.Empty;
        CrawlJobRequestedMessage? message = null;

        try
        {
            rawJson = Encoding.UTF8.GetString(ea.Body.ToArray());
            _logger.LogInformation("Received crawl job message. DeliveryTag={DeliveryTag}, Length={Length}", deliveryTag, rawJson.Length);

            message = JsonSerializer.Deserialize<CrawlJobRequestedMessage>(rawJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (message == null || message.JobId == Guid.Empty || string.IsNullOrWhiteSpace(message.InputUrl))
            {
                _logger.LogWarning("Poison message detected (invalid payload). Routing directly to DLQ. Raw: {RawJson}", TruncatePayloadForLogging(rawJson));

                // A JobId lets us record the failure even though the payload is otherwise unusable
                if (message != null && message.JobId != Guid.Empty)
                {
                    await MarkJobFailedAsync(message.JobId, "Message payload failed validation (missing or invalid InputUrl).");
                }

                // Reject without requeue -> routes to Dead Letter Queue
                NackMessage(deliveryTag, requeue: false);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var crawlerEngine = scope.ServiceProvider.GetRequiredService<ICrawlerEngine>();

            var job = await dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == message.JobId, stoppingToken);
            if (job == null)
            {
                _logger.LogWarning("Job {JobId} not found in database. Discarding message.", message.JobId);
                AckMessage(deliveryTag);
                return;
            }

            if (job.Status == JobStatus.Canceled)
            {
                _logger.LogInformation("Job {JobId} was already canceled. Skipping crawl.", message.JobId);
                AckMessage(deliveryTag);
                return;
            }

            // Reset the retry counter only for a genuinely fresh dispatch (Pending, or a
            // re-run after a prior terminal state). A redelivery of a message that is
            // already Running belongs to the SAME retry sequence started above, so the
            // counter must be left alone here or every requeued redelivery would wipe
            // it back to 0 and the retry limit could never be reached.
            if (job.Status != JobStatus.Running)
            {
                job.RetryCount = 0;
            }

            job.Status = JobStatus.Running;
            job.StartedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(stoppingToken);

            var maxDepth = message.MaxDepth > 0 ? message.MaxDepth : 2;
            var maxPages = _configuration.GetValue<int>("MAX_PAGES_SAFETY_LIMIT", 200);

            _logger.LogInformation("Executing crawl for Job {JobId}: {Url} (MaxDepth={MaxDepth}, MaxPages={MaxPages})",
                message.JobId, message.InputUrl, maxDepth, maxPages);

            var crawlResult = await crawlerEngine.CrawlAsync(message.JobId, message.InputUrl, maxDepth, maxPages, stoppingToken);

            if (crawlResult.Success)
            {
                // Persist results idempotently:
                // Clear any partial pages or edges from prior attempts for this JobId
                var existingPages = await dbContext.Pages.Where(p => p.JobId == message.JobId).ToListAsync(stoppingToken);
                if (existingPages.Count > 0)
                {
                    dbContext.Pages.RemoveRange(existingPages);
                }

                var existingEdges = await dbContext.Edges.Where(e => e.JobId == message.JobId).ToListAsync(stoppingToken);
                if (existingEdges.Count > 0)
                {
                    dbContext.Edges.RemoveRange(existingEdges);
                }

                dbContext.Pages.AddRange(crawlResult.Pages);
                dbContext.Edges.AddRange(crawlResult.Edges);

                job.Status = JobStatus.Completed;
                job.CompletedAt = DateTime.UtcNow;
                job.FailureReason = null;

                await dbContext.SaveChangesAsync(stoppingToken);

                AckMessage(deliveryTag);
                _logger.LogInformation("Job {JobId} completed successfully. Persisted {PageCount} pages and {EdgeCount} edges.",
                    message.JobId, crawlResult.Pages.Count, crawlResult.Edges.Count);
            }
            else
            {
                // Logical crawl failure (e.g. root URL unreachable, invalid HTML, HTTP error)
                _logger.LogWarning("Job {JobId} crawl failed logically: {ErrorMessage}", message.JobId, crawlResult.ErrorMessage);

                job.Status = JobStatus.Failed;
                job.CompletedAt = DateTime.UtcNow;
                job.FailureReason = crawlResult.ErrorMessage;

                await dbContext.SaveChangesAsync(stoppingToken);

                // Acknowledge because failure was recorded permanently in DB
                AckMessage(deliveryTag);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Execution canceled for delivery {DeliveryTag}. Requeuing message.", deliveryTag);
            NackMessage(deliveryTag, requeue: true);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Poison message detected (malformed JSON). Routing directly to DLQ. DeliveryTag={DeliveryTag}, Raw: {RawJson}",
                deliveryTag, TruncatePayloadForLogging(rawJson));

            // No JobId can be recovered from unparseable JSON, so there is no Job row to mark Failed here.
            // Malformed JSON is not a transient failure; reject without requeue -> routes to Dead Letter Queue
            NackMessage(deliveryTag, requeue: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing delivery {DeliveryTag}.", deliveryTag);

            if (message == null || message.JobId == Guid.Empty)
            {
                // Can't identify which Job this delivery belongs to, so there is no
                // row to track a retry count on. Route straight to DLQ rather than
                // requeueing a message we can never make progress on.
                _logger.LogError("Delivery {DeliveryTag} could not be attributed to a Job. Routing to DLQ.", deliveryTag);
                NackMessage(deliveryTag, requeue: false);
                return;
            }

            // The primary queue is a classic queue, so RabbitMQ never populates the
            // x-delivery-count header on redelivery. Track attempts on the Job row
            // itself instead so the retry budget survives across redeliveries.
            using var retryScope = _scopeFactory.CreateScope();
            var retryTracker = retryScope.ServiceProvider.GetRequiredService<IRetryTracker>();
            var retryCount = await retryTracker.RegisterFailureAsync(message.JobId, stoppingToken);

            if (retryTracker.IsExhausted(retryCount))
            {
                _logger.LogError("Job {JobId} delivery {DeliveryTag} exceeded max retry attempts ({MaxRetryAttempts}). Routing to DLQ.",
                    message.JobId, deliveryTag, retryTracker.MaxRetryAttempts);

                await MarkJobFailedAsync(message.JobId,
                    $"Exceeded max retry attempts ({retryTracker.MaxRetryAttempts}) after repeated transient failures: {ex.Message}");

                // Reject with requeue=false so RabbitMQ routes to Dead Letter Queue
                NackMessage(deliveryTag, requeue: false);
            }
            else
            {
                _logger.LogWarning("Job {JobId} delivery {DeliveryTag} encountered transient failure (attempt {Attempt}/{MaxAttempts}). Requeuing.",
                    message.JobId, deliveryTag, retryCount, retryTracker.MaxRetryAttempts);

                // Increment retry delay before requeue
                await Task.Delay(TimeSpan.FromSeconds(2 * retryCount), stoppingToken);
                NackMessage(deliveryTag, requeue: true);
            }
        }
    }

    private Task OnConsumerCancelledAsync(object sender, ConsumerEventArgs e)
    {
        _logger.LogError(
            "RabbitMQ consumer was cancelled unexpectedly (ConsumerTags={ConsumerTags}). This usually indicates an unrecoverable topology change (e.g. the queue was deleted). Stopping the worker host so the container can be restarted.",
            string.Join(",", e.ConsumerTags));

        _hostApplicationLifetime.StopApplication();
        return Task.CompletedTask;
    }

    private void OnConnectionShutdown(object? sender, ShutdownEventArgs e)
    {
        if (e.Initiator == ShutdownInitiator.Application)
        {
            _logger.LogInformation("RabbitMQ connection closed (ReplyCode={ReplyCode}, ReplyText={ReplyText}).", e.ReplyCode, e.ReplyText);
            return;
        }

        _logger.LogWarning(
            "RabbitMQ connection was lost unexpectedly (Initiator={Initiator}, ReplyCode={ReplyCode}, ReplyText={ReplyText}). Automatic recovery will attempt to reconnect.",
            e.Initiator, e.ReplyCode, e.ReplyText);
    }

    private void OnConnectionRecoverySucceeded(object? sender, EventArgs e)
    {
        _logger.LogInformation("RabbitMQ connection automatically recovered successfully.");
    }

    private void OnConnectionRecoveryError(object? sender, ConnectionRecoveryErrorEventArgs e)
    {
        _logger.LogError(e.Exception, "RabbitMQ automatic recovery failed after exhausting retry attempts. Stopping the worker host so the container can be restarted.");
        _hostApplicationLifetime.StopApplication();
    }

    private async Task MarkJobFailedAsync(Guid jobId, string failureReason)
    {
        try
        {
            // Use a fresh scope/DbContext (not the one active when the failure occurred) so this
            // still succeeds when the original failure was itself a database error, and use
            // CancellationToken.None so the failure is recorded even if the host is shutting down.
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var job = await dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);
            if (job == null)
            {
                return;
            }

            job.Status = JobStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            job.FailureReason = failureReason;

            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark Job {JobId} as Failed after dead-lettering.", jobId);
        }
    }

    private static string TruncatePayloadForLogging(string payload)
    {
        return payload.Length > MaxLoggedPayloadLength
            ? string.Concat(payload.AsSpan(0, MaxLoggedPayloadLength), "... [truncated]")
            : payload;
    }

    private void AckMessage(ulong deliveryTag)
    {
        _channel?.BasicAck(deliveryTag, multiple: false);
    }

    private void NackMessage(ulong deliveryTag, bool requeue)
    {
        _channel?.BasicNack(deliveryTag, multiple: false, requeue: requeue);
    }

    private async Task ConnectWithRetryAsync(CancellationToken stoppingToken)
    {
        var options = _rabbitOptions.Value;
        var factory = new ConnectionFactory
        {
            HostName = options.Host,
            Port = options.Port,
            UserName = options.Username,
            Password = options.Password,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };

        const int maxAttempts = 10;
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 1; attempt <= maxAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                _logger.LogInformation("Connecting to RabbitMQ at {Host}:{Port} (Attempt {Attempt}/{MaxAttempts})...",
                    options.Host, options.Port, attempt, maxAttempts);

                _connection = factory.CreateConnection("alteva-crawl-worker");
                _connection.ConnectionShutdown += OnConnectionShutdown;
                if (_connection is IAutorecoveringConnection recoverableConnection)
                {
                    recoverableConnection.RecoverySucceeded += OnConnectionRecoverySucceeded;
                    recoverableConnection.ConnectionRecoveryError += OnConnectionRecoveryError;
                }

                _channel = _connection.CreateModel();
                _logger.LogInformation("Successfully connected to RabbitMQ.");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning("RabbitMQ connection attempt {Attempt} failed: {Message}. Retrying in {Delay}s...",
                    attempt, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 10));
            }
        }
    }

    public override void Dispose()
    {
        _channel?.Close();
        _channel?.Dispose();
        _connection?.Close();
        _connection?.Dispose();
        base.Dispose();
    }
}
