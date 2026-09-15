using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Alteva.Infrastructure.Messaging;

/// <summary>
/// Production RabbitMQ publisher supporting durable exchanges, persistent delivery,
/// and automated dead-letter exchange (DLQ) topology declaration.
/// </summary>
public class RabbitMQMessagePublisher : IMessagePublisher, IDisposable
{
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
        _options = options?.Value ?? new RabbitMQOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task PublishAsync<T>(T message, string? routingKey = null, CancellationToken cancellationToken = default)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        EnsureConnected();

        lock (_syncLock)
        {
            var key = routingKey ?? _options.RoutingKey;
            var payload = JsonSerializer.SerializeToUtf8Bytes(message);

            var properties = _channel!.CreateBasicProperties();
            properties.Persistent = true;
            properties.DeliveryMode = 2;
            properties.ContentType = "application/json";
            properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            _channel.BasicPublish(
                exchange: _options.ExchangeName,
                routingKey: key,
                mandatory: false,
                basicProperties: properties,
                body: payload);

            _logger.LogInformation("Published event of type {MessageType} to exchange '{Exchange}' with routing key '{RoutingKey}'",
                typeof(T).Name, _options.ExchangeName, key);
        }

        return Task.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (_channel != null && _channel.IsOpen)
        {
            return;
        }

        lock (_syncLock)
        {
            if (_channel != null && _channel.IsOpen)
            {
                return;
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

            _logger.LogInformation("Connecting to RabbitMQ at {Host}:{Port}...", _options.Host, _options.Port);

            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                UserName = _options.Username,
                Password = _options.Password,
                DispatchConsumersAsync = true,
                AutomaticRecoveryEnabled = true
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            // 1. Declare Dead-Letter Exchange & Queue
            _channel.ExchangeDeclare(
                exchange: _options.DeadLetterExchange,
                type: ExchangeType.Direct,
                durable: true,
                autoDelete: false);

            _channel.QueueDeclare(
                queue: _options.DeadLetterQueue,
                durable: true,
                exclusive: false,
                autoDelete: false);

            _channel.QueueBind(
                queue: _options.DeadLetterQueue,
                exchange: _options.DeadLetterExchange,
                routingKey: _options.DeadLetterRoutingKey);

            // 2. Declare Main Exchange
            _channel.ExchangeDeclare(
                exchange: _options.ExchangeName,
                type: ExchangeType.Direct,
                durable: true,
                autoDelete: false);

            // 3. Declare Main Queue with DLQ arguments
            var queueArgs = new Dictionary<string, object>
            {
                { "x-dead-letter-exchange", _options.DeadLetterExchange },
                { "x-dead-letter-routing-key", _options.DeadLetterRoutingKey }
            };

            _channel.QueueDeclare(
                queue: _options.QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: queueArgs);

            _channel.QueueBind(
                queue: _options.QueueName,
                exchange: _options.ExchangeName,
                routingKey: _options.RoutingKey);

            _logger.LogInformation("RabbitMQ topology initialized successfully on exchange '{Exchange}' and queue '{Queue}'.",
                _options.ExchangeName, _options.QueueName);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _channel?.Close();
            _channel?.Dispose();
            _connection?.Close();
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing RabbitMQ publisher connection.");
        }
        finally
        {
            _disposed = true;
        }
    }
}
