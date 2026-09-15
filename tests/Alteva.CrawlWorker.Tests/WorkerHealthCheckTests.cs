using System.Threading.Tasks;
using Alteva.CrawlWorker.Health;
using Alteva.Infrastructure.Data;
using Alteva.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alteva.CrawlWorker.Tests;

public class WorkerHealthCheckTests
{
    [Fact]
    public async Task RabbitMqCheck_IsUnhealthy_WhenWorkerIsNotConsuming()
    {
        var worker = new Worker(
            Options.Create(new RabbitMQOptions()),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance),
            NullLogger<Worker>.Instance);

        var result = await new RabbitMqConsumerHealthCheck(worker).CheckHealthAsync(new HealthCheckContext());

        worker.IsConsuming.Should().BeFalse();
        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task DatabaseCheck_IsHealthy_WhenDatabaseReachable()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using var dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        var result = await new DatabaseHealthCheck(dbContext).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task DatabaseCheck_IsUnhealthy_WhenDatabaseUnreachable()
    {
        // Read-only mode on a file that does not exist: SQLite cannot open it
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=does-not-exist/alteva.db;Mode=ReadOnly")
            .Options;
        await using var dbContext = new AppDbContext(options);

        var result = await new DatabaseHealthCheck(dbContext).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
