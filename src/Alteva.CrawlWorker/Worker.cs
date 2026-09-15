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
    private const int MaxRetryAttempts = 3;
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

        // Ensure RabbitMQ topology is declared
        DeclareTopology(_channel, options);

        // Fair dispatch: prefetch 1 message at a time per worker instance
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (sender, ea) =>
        {
            await ProcessMessageAsync(ea, stoppingToken);
        };
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

    private async Task ProcessMessageAsync(BasicDeliverEventArgs ea, CancellationToken stoppingToken)
    {
        var deliveryTag = ea.DeliveryTag;
        string rawJson = string.Empty;

        try
        {
            rawJson = Encoding.UTF8.GetString(ea.Body.ToArray());
            _logger.LogInformation("Received crawl job message. DeliveryTag={DeliveryTag}, Length={Length}", deliveryTag, rawJson.Length);

            var message = JsonSerializer.Deserialize<CrawlJobRequestedMessage>(rawJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (message == null || message.JobId == Guid.Empty || string.IsNullOrWhiteSpace(message.InputUrl))
            {
                _logger.LogWarning("Poison message detected (invalid payload). Routing directly to DLQ. Raw: {RawJson}", TruncatePayloadForLogging(rawJson));
                // Reject without requeue -> routes to Dead Letter Queue
                _channel?.BasicNack(deliveryTag, multiple: false, requeue: false);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var crawlerEngine = scope.ServiceProvider.GetRequiredService<ICrawlerEngine>();

            var job = await dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == message.JobId, stoppingToken);
            if (job == null)
            {
                _logger.LogWarning("Job {JobId} not found in database. Discarding message.", message.JobId);
                _channel?.BasicAck(deliveryTag, multiple: false);
                return;
            }

            if (job.Status == JobStatus.Canceled)
            {
                _logger.LogInformation("Job {JobId} was already canceled. Skipping crawl.", message.JobId);
                _channel?.BasicAck(deliveryTag, multiple: false);
                return;
            }

            // Transition Job to Running
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

                _channel?.BasicAck(deliveryTag, multiple: false);
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
                _channel?.BasicAck(deliveryTag, multiple: false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Execution canceled for delivery {DeliveryTag}. Requeuing message.", deliveryTag);
            _channel?.BasicNack(deliveryTag, multiple: false, requeue: true);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Poison message detected (malformed JSON). Routing directly to DLQ. DeliveryTag={DeliveryTag}, Raw: {RawJson}",
                deliveryTag, TruncatePayloadForLogging(rawJson));

            // Malformed JSON is not a transient failure; reject without requeue -> routes to Dead Letter Queue
            _channel?.BasicNack(deliveryTag, multiple: false, requeue: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing delivery {DeliveryTag}.", deliveryTag);

            var retryCount = GetRetryCount(ea.BasicProperties);
            if (retryCount >= MaxRetryAttempts)
            {
                _logger.LogError("Job delivery {DeliveryTag} exceeded max retry attempts ({MaxRetryAttempts}). Routing to DLQ.",
                    deliveryTag, MaxRetryAttempts);

                // Reject with requeue=false so RabbitMQ routes to Dead Letter Queue
                _channel?.BasicNack(deliveryTag, multiple: false, requeue: false);
            }
            else
            {
                _logger.LogWarning("Job delivery {DeliveryTag} encountered transient failure (attempt {Attempt}/{MaxAttempts}). Requeuing.",
                    deliveryTag, retryCount + 1, MaxRetryAttempts);

                // Increment retry delay before requeue
                await Task.Delay(TimeSpan.FromSeconds(2 * (retryCount + 1)), stoppingToken);
                _channel?.BasicNack(deliveryTag, multiple: false, requeue: true);
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

    private static string TruncatePayloadForLogging(string payload)
    {
        return payload.Length > MaxLoggedPayloadLength
            ? string.Concat(payload.AsSpan(0, MaxLoggedPayloadLength), "... [truncated]")
            : payload;
    }

    private static int GetRetryCount(IBasicProperties? properties)
    {
        if (properties?.Headers != null && properties.Headers.TryGetValue("x-delivery-count", out var countObj))
        {
            if (countObj is long l) return (int)l;
            if (countObj is int i) return i;
        }
        return 0;
    }

    private void DeclareTopology(IModel channel, RabbitMQOptions options)
    {
        // 1. Declare Dead Letter Exchange & Queue
        channel.ExchangeDeclare(
            exchange: options.DeadLetterExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false);

        channel.QueueDeclare(
            queue: options.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false);

        channel.QueueBind(
            queue: options.DeadLetterQueue,
            exchange: options.DeadLetterExchange,
            routingKey: options.DeadLetterRoutingKey);

        // 2. Declare Primary Work Exchange & Queue with DLX configuration
        channel.ExchangeDeclare(
            exchange: options.ExchangeName,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false);

        var queueArguments = new Dictionary<string, object>
        {
            { "x-dead-letter-exchange", options.DeadLetterExchange },
            { "x-dead-letter-routing-key", options.DeadLetterRoutingKey }
        };

        channel.QueueDeclare(
            queue: options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArguments);

        channel.QueueBind(
            queue: options.QueueName,
            exchange: options.ExchangeName,
            routingKey: options.RoutingKey);
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
