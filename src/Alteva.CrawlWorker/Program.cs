using Alteva.CrawlWorker;
using Alteva.CrawlWorker.Services;
using Alteva.Domain.Services;
using Alteva.Infrastructure.Data;
using Alteva.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// ==========================================
// Database Persistence
// ==========================================
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found in configuration or environment.");
    options.UseSqlServer(connectionString);
});

// ==========================================
// Messaging & Broker Options
// ==========================================
builder.Services.Configure<RabbitMQOptions>(builder.Configuration.GetSection(RabbitMQOptions.SectionName));

// ==========================================
// Domain Services
// ==========================================
builder.Services.AddSingleton<IUrlNormalizer, UrlNormalizer>();
builder.Services.AddSingleton<IHtmlLinkExtractor, HtmlLinkExtractor>();
builder.Services.AddSingleton<IDomainLinkRatioCalculator, DomainLinkRatioCalculator>();

// ==========================================
// Crawler Engine & HTTP Client
// ==========================================
builder.Services.AddHttpClient<ICrawlerEngine, CrawlerEngine>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AltevaWebCrawler/1.0 (+https://github.com/Darkanath/ha-alteva-web-crawl)");
});

// ==========================================
// Retry Tracking (per-Job retry count, since classic queues don't set x-delivery-count)
// ==========================================
builder.Services.AddScoped<IRetryTracker, RetryTracker>();

// ==========================================
// Background Worker Hosted Service
// ==========================================
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
