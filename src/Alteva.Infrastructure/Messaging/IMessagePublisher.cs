using System.Threading;
using System.Threading.Tasks;

namespace Alteva.Infrastructure.Messaging;

/// <summary>
/// Abstraction for publishing event messages to the message broker.
/// </summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Publishes a message to the broker under the specified routing key.
    /// </summary>
    /// <typeparam name="T">Message payload type.</typeparam>
    /// <param name="message">The object to serialize and send.</param>
    /// <param name="routingKey">Optional routing key (defaults to type-specific default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAsync<T>(T message, string? routingKey = null, CancellationToken cancellationToken = default);
}
