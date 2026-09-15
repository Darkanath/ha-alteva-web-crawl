using System.Text;
using System.Text.Json;
using Alteva.CrawlWorker.Services;
using Alteva.Domain.Models;
using Alteva.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Alteva.CrawlWorker;

/// <summary>
/// Single RabbitMQ consumer for <see cref="CrawlPageMessage"/>s. Handles one message at a time via
/// <see cref="PageCrawlHandler"/> and owns all ack/nack decisions (architecture_notes.md §3.6).
/// </summary>
public class Worker : BackgroundService
{
    private readonly IOptions<RabbitMQOptions> _rabbitOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly ILogger<Worker> _logger;

    private IConnection? _connection;
    private IModel? _channel;
    private AsyncEventingBasicConsumer? _consumer;
    private const int MaxLoggedPayloadLength = 500;

    /// <summary>
    /// Wait before requeuing a message after a failure, so an outage (database, broker) does not
    /// spin the single worker. Requeued messages keep their queue position.
    /// </summary>
    public static readonly TimeSpan FailureRequeueDelay = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public Worker(
        IOptions<RabbitMQOptions> rabbitOptions,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<Worker> logger)
    {
        _rabbitOptions = rabbitOptions ?? throw new ArgumentNullException(nameof(rabbitOptions));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _hostApplicationLifetime = hostApplicationLifetime ?? throw new ArgumentNullException(nameof(hostApplicationLifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// True while the channel is open and the consumer is registered with RabbitMQ (reported by /health).
    /// </summary>
    public bool IsConsuming => _channel is { IsOpen: true } && _consumer is { IsRunning: true };

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
        _consumer = consumer;

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
            // ProcessMessageAsync acks/nacks on every expected path; this last-resort guard keeps an
            // unexpected exception (e.g. the ack itself failing) from crashing the dispatch loop.
            _logger.LogError(ex, "Unexpected exception handling delivery {DeliveryTag}.", ea.DeliveryTag);
        }
    }

    private async Task ProcessMessageAsync(BasicDeliverEventArgs ea, CancellationToken stoppingToken)
    {
        var message = TryDeserialize(ea);
        if (message == null)
        {
            // Malformed or old-contract payload: the only thing that is dead-lettered.
            NackMessage(ea.DeliveryTag, requeue: false);
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PageCrawlHandler>().HandleAsync(message, stoppingToken);
            AckMessage(ea.DeliveryTag);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Shutting down while handling {Url} (job {JobId}). Requeuing.", message.Url, message.JobId);
            NackMessage(ea.DeliveryTag, requeue: true);
        }
        catch (Exception ex) when (!ea.Redelivered)
        {
            // First failure: retry once.
            _logger.LogWarning(ex, "Failed handling {Url} (job {JobId}). Requeuing for one retry.", message.Url, message.JobId);
            await RequeueAfterDelayAsync(ea, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed handling {Url} (job {JobId}) on redelivery. Marking the page Failed.", message.Url, message.JobId);
            await FailPageOrRequeueAsync(ea, message, ex, stoppingToken);
        }
    }

    /// <summary>
    /// Second failure: record the page as Failed so the job can finish. If even that is impossible, the
    /// cause is infrastructure (database or broker down), not the page: requeue and try again later
    /// rather than dead-lettering, which would leave the job Running forever.
    /// </summary>
    private async Task FailPageOrRequeueAsync(BasicDeliverEventArgs ea, CrawlPageMessage message, Exception failure, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<PageCrawlHandler>();
            var outcome = await handler.FailPageAsync(message, $"Processing failed after retry: {failure.Message}", stoppingToken);
            if (outcome != FailPageOutcome.PageAlreadyFinished)
            {
                AckMessage(ea.DeliveryTag);
                return;
            }

            _logger.LogError("Job {JobId}: {Url} is finished but re-publishing its children keeps failing. Requeuing.", message.JobId, message.Url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: Could not mark {Url} Failed (infrastructure unavailable). Requeuing.", message.JobId, message.Url);
        }

        await RequeueAfterDelayAsync(ea, stoppingToken);
    }

    private async Task RequeueAfterDelayAsync(BasicDeliverEventArgs ea, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FailureRequeueDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: requeue immediately.
        }

        NackMessage(ea.DeliveryTag, requeue: true);
    }

    private CrawlPageMessage? TryDeserialize(BasicDeliverEventArgs ea)
    {
        var rawJson = Encoding.UTF8.GetString(ea.Body.Span);
        try
        {
            var message = JsonSerializer.Deserialize<CrawlPageMessage>(rawJson, JsonOptions);
            if (message != null
                && message.JobId != Guid.Empty
                && !string.IsNullOrWhiteSpace(message.Url)
                && !string.IsNullOrWhiteSpace(message.RootUrl)
                && message.Depth >= 0
                && message.Depth <= message.MaxDepth)
            {
                return message;
            }
        }
        catch (JsonException)
        {
        }

        _logger.LogWarning("Poison message (invalid CrawlPageMessage) routed to the dead-letter queue. DeliveryTag={DeliveryTag}, Raw: {RawJson}",
            ea.DeliveryTag, TruncatePayloadForLogging(rawJson));
        return null;
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
