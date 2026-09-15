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

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
