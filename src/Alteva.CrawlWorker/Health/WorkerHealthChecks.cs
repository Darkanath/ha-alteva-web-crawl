using System.Threading;
using System.Threading.Tasks;
using Alteva.Infrastructure.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Alteva.CrawlWorker.Health;

/// <summary>
/// Healthy while the worker is connected to RabbitMQ and consuming the crawl queue.
/// </summary>
public class RabbitMqConsumerHealthCheck(Worker worker) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(worker.IsConsuming
            ? HealthCheckResult.Healthy("Consuming the crawl queue.")
            : HealthCheckResult.Unhealthy("Not consuming the crawl queue (RabbitMQ connection or consumer is down)."));
}

/// <summary>
/// Healthy while the crawl database is reachable.
/// </summary>
public class DatabaseHealthCheck(AppDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await dbContext.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("Database reachable.")
            : HealthCheckResult.Unhealthy("Database unreachable.");
}
