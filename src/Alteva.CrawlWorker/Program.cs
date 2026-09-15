using Alteva.CrawlWorker;
using Alteva.CrawlWorker.Health;
using Alteva.CrawlWorker.Services;
using Alteva.Domain.Services;
using Alteva.Infrastructure.Data;
using Alteva.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

// A minimal web host, only so the worker can serve /health; all crawling happens in the Worker hosted service.
var builder = WebApplication.CreateBuilder(args);

// ==========================================
// Database Persistence & Crawl State
// ==========================================
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found in configuration or environment.");
    options.UseSqlServer(connectionString);
});
builder.Services.AddScoped<ICrawlStateStore, CrawlStateStore>();

// ==========================================
// Messaging (consumer in Worker, publisher for claimed children)
// ==========================================
builder.Services.Configure<RabbitMQOptions>(builder.Configuration.GetSection(RabbitMQOptions.SectionName));
builder.Services.AddSingleton<IMessagePublisher, RabbitMQMessagePublisher>();

// ==========================================
// Domain Services
// ==========================================
builder.Services.AddSingleton<IUrlNormalizer, UrlNormalizer>();
builder.Services.AddSingleton<IHtmlLinkExtractor, HtmlLinkExtractor>();
builder.Services.AddSingleton<IDomainLinkRatioCalculator, DomainLinkRatioCalculator>();

// ==========================================
// Page Crawling
// ==========================================
builder.Services.AddHttpClient<PageCrawler>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AltevaWebCrawler/1.0 (+https://github.com/Darkanath/ha-alteva-web-crawl)");
});
builder.Services.AddScoped<PageCrawlHandler>();

// Singleton so the health check can observe the consumer
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());

// ==========================================
// Health
// ==========================================
// Short timeout: an unreachable database would otherwise hold /health for the full SQL connect timeout
builder.Services.AddHealthChecks()
    .AddCheck<RabbitMqConsumerHealthCheck>("rabbitmq")
    .AddCheck<DatabaseHealthCheck>("database", timeout: TimeSpan.FromSeconds(3));

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();
