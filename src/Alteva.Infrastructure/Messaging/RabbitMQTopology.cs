using System.Collections.Generic;
using RabbitMQ.Client;

namespace Alteva.Infrastructure.Messaging;

/// <summary>
/// Single definition of the crawl exchange/queue topology, declared by both the publisher and the
/// worker. Declarations must be identical everywhere, or RabbitMQ rejects them with PRECONDITION_FAILED.
/// </summary>
public static class RabbitMQTopology
{
    public static void Declare(IModel channel, RabbitMQOptions options)
    {
        // Dead-letter exchange & queue
        channel.ExchangeDeclare(options.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false);
        channel.QueueDeclare(options.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(options.DeadLetterQueue, options.DeadLetterExchange, options.DeadLetterRoutingKey);

        // Work exchange & queue, dead-lettering rejected messages
        channel.ExchangeDeclare(options.ExchangeName, ExchangeType.Direct, durable: true, autoDelete: false);
        channel.QueueDeclare(
            options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object>
            {
                { "x-dead-letter-exchange", options.DeadLetterExchange },
                { "x-dead-letter-routing-key", options.DeadLetterRoutingKey }
            });
        channel.QueueBind(options.QueueName, options.ExchangeName, options.RoutingKey);
    }
}
