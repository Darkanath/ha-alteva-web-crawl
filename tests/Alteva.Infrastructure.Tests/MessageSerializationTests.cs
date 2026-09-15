using System;
using System.Collections.Generic;
using System.Text.Json;
using Alteva.Domain.Models;
using Alteva.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alteva.Infrastructure.Tests;

public class MessageSerializationTests
{
    [Fact]
    public void CrawlJobRequestedMessage_ShouldSerializeAndDeserializeAccurately()
    {
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var original = new CrawlJobRequestedMessage
        {
            JobId = jobId,
            InputUrl = "https://example.com/test",
            MaxDepth = 3,
            SubmittedAt = now
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(original);
        var deserialized = JsonSerializer.Deserialize<CrawlJobRequestedMessage>(bytes);

        deserialized.Should().NotBeNull();
        deserialized!.JobId.Should().Be(jobId);
        deserialized.InputUrl.Should().Be("https://example.com/test");
        deserialized.MaxDepth.Should().Be(3);
    }

    [Fact]
    public void CrawlPageMessage_ShouldSerializeAndDeserializeAccurately()
    {
        var jobId = Guid.NewGuid();
        var original = new CrawlPageMessage
        {
            JobId = jobId,
            Url = "https://example.com/docs/intro",
            Depth = 1,
            MaxDepth = 3
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(original);
        var deserialized = JsonSerializer.Deserialize<CrawlPageMessage>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        deserialized.Should().NotBeNull();
        deserialized!.JobId.Should().Be(jobId);
        deserialized.Url.Should().Be("https://example.com/docs/intro");
        deserialized.Depth.Should().Be(1);
        deserialized.MaxDepth.Should().Be(3);
    }

    [Fact]
    public void RabbitMQOptions_ShouldNotContainHardcodedCredentialsOrHostsByDefault()
    {
        var options = new RabbitMQOptions();

        // Enforce that secrets and environment targets are NOT hardcoded in source
        options.Host.Should().BeEmpty();
        options.Username.Should().BeEmpty();
        options.Password.Should().BeEmpty();
        options.ExchangeName.Should().BeEmpty();
        options.QueueName.Should().BeEmpty();
        options.Port.Should().Be(5672); // Default AMQP port
    }

    [Fact]
    public void RabbitMQOptions_CanBeBoundFromConfigurationDictionary()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["RabbitMQ:Host"] = "rabbitmq.internal",
            ["RabbitMQ:Port"] = "5672",
            ["RabbitMQ:Username"] = "custom_user",
            ["RabbitMQ:Password"] = "custom_pass",
            ["RabbitMQ:ExchangeName"] = "custom.exchange",
            ["RabbitMQ:QueueName"] = "custom.queue",
            ["RabbitMQ:RoutingKey"] = "custom.key",
            ["RabbitMQ:DeadLetterExchange"] = "custom.dlx",
            ["RabbitMQ:DeadLetterQueue"] = "custom.dlq",
            ["RabbitMQ:DeadLetterRoutingKey"] = "custom.poison"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var options = new RabbitMQOptions();
        configuration.GetSection(RabbitMQOptions.SectionName).Bind(options);

        options.Host.Should().Be("rabbitmq.internal");
        options.Username.Should().Be("custom_user");
        options.Password.Should().Be("custom_pass");
        options.ExchangeName.Should().Be("custom.exchange");
        options.QueueName.Should().Be("custom.queue");
        options.DeadLetterExchange.Should().Be("custom.dlx");
    }
}
