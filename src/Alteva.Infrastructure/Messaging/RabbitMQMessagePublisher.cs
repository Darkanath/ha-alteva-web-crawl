using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Alteva.Infrastructure.Messaging;

/// <summary>
/// RabbitMQ publisher with persistent delivery and publisher confirms: <see cref="PublishAsync{T}"/>
/// returns only after the broker has accepted the message, and throws otherwise.
/// </summary>
public class RabbitMQMessagePublisher : IMessagePublisher, IDisposable
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    private readonly RabbitMQOptions _options;
    private readonly ILogger<RabbitMQMessagePublisher> _logger;
    private readonly object _syncLock = new();
    private IConnection? _connection;
    private IModel? _channel;
    private bool _disposed;

    public RabbitMQMessagePublisher(
        IOptions<RabbitMQOptions> options,
        ILogger<RabbitMQMessagePublisher> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task PublishAsync<T>(T message, string? routingKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var key = routingKey ?? _options.RoutingKey;
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);

        // The channel is shared by concurrent API requests and is not thread-safe; the lock also
        // keeps each confirm wait scoped to this one publish.
        lock (_syncLock)
        {
            var channel = EnsureConnected();

            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.ContentType = "application/json";
            properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            channel.BasicPublish(_options.ExchangeName, key, mandatory: false, properties, payload);

            // Throws if the broker nacks or does not confirm in time (and closes the channel,
            // so the next publish reconnects).
            channel.WaitForConfirmsOrDie(ConfirmTimeout);
        }

        _logger.LogInformation("Published {MessageType} to exchange '{Exchange}' with routing key '{RoutingKey}'",
            typeof(T).Name, _options.ExchangeName, key);

        return Task.CompletedTask;
    }

    // Callers must hold _syncLock.
    private IModel EnsureConnected()
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            throw new InvalidOperationException("RabbitMQ host is not configured. Please supply 'RabbitMQ__Host' via environment variables or .env.");
        }
        if (string.IsNullOrWhiteSpace(_options.Username) || string.IsNullOrWhiteSpace(_options.Password))
        {
            throw new InvalidOperationException("RabbitMQ credentials are not configured. Please supply 'RabbitMQ__Username' and 'RabbitMQ__Password' via environment variables or .env.");
        }
        if (string.IsNullOrWhiteSpace(_options.ExchangeName) || string.IsNullOrWhiteSpace(_options.QueueName))
        {
            throw new InvalidOperationException("RabbitMQ topology is not configured. Please supply 'RabbitMQ:ExchangeName' and 'RabbitMQ:QueueName' via appsettings.json.");
        }

        CloseConnection();

        _logger.LogInformation("Connecting to RabbitMQ at {Host}:{Port}...", _options.Host, _options.Port);

        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.Username,
            Password = _options.Password
        };

        _connection = factory.CreateConnection("alteva-publisher");
        _channel = _connection.CreateModel();
        _channel.ConfirmSelect();

        // Declaring the queue here too means a publish before the worker has started is still routed.
        RabbitMQTopology.Declare(_channel, _options);

        return _channel;
    }

    private void CloseConnection()
    {
        try
        {
            _channel?.Dispose();
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing RabbitMQ publisher connection.");
        }

        _channel = null;
        _connection = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_syncLock)
        {
            CloseConnection();
            _disposed = true;
        }
    }
}
