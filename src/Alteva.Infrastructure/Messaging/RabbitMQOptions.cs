namespace Alteva.Infrastructure.Messaging;

/// <summary>
/// Strongly-typed configuration options for connecting to RabbitMQ.
/// Secret and environment-specific values (Host, Username, Password, Port) are injected via .env / environment variables.
/// Topology names (ExchangeName, QueueName, RoutingKey, DeadLetterExchange, etc.) are loaded from configuration files (appsettings.json).
/// No default credentials or hosts are hardcoded in code.
/// </summary>
public class RabbitMQOptions
{
    public const string SectionName = "RabbitMQ";

    /// <summary>
    /// RabbitMQ host name or IP (e.g. localhost or rabbitmq container). Acquired from environment.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// AMQP protocol port. Defaults to 5672 or acquired from environment.
    /// </summary>
    public int Port { get; set; } = 5672;

    /// <summary>
    /// RabbitMQ authentication username. Acquired from environment (.env).
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// RabbitMQ authentication password. Acquired from environment (.env).
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Main message exchange name. Defined in appsettings.json.
    /// </summary>
    public string ExchangeName { get; set; } = string.Empty;

    /// <summary>
    /// Queue name for incoming crawl jobs. Defined in appsettings.json.
    /// </summary>
    public string QueueName { get; set; } = string.Empty;

    /// <summary>
    /// Default routing key for crawl jobs. Defined in appsettings.json.
    /// </summary>
    public string RoutingKey { get; set; } = string.Empty;

    /// <summary>
    /// Dead-letter exchange name. Defined in appsettings.json.
    /// </summary>
    public string DeadLetterExchange { get; set; } = string.Empty;

    /// <summary>
    /// Dead-letter queue name for poison messages. Defined in appsettings.json.
    /// </summary>
    public string DeadLetterQueue { get; set; } = string.Empty;

    /// <summary>
    /// Routing key for dead-lettered messages. Defined in appsettings.json.
    /// </summary>
    public string DeadLetterRoutingKey { get; set; } = string.Empty;
}
