using System.Text.Json.Serialization;
using Alteva.Domain.Services;
using Alteva.Infrastructure.Data;
using Alteva.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ==========================================
// Service Registrations
// ==========================================

// Database Persistence
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found in configuration or environment.");
    options.UseSqlServer(connectionString);
});
builder.Services.AddScoped<ICrawlStateStore, CrawlStateStore>();

// RabbitMQ Options & Messaging Publisher
builder.Services.Configure<RabbitMQOptions>(builder.Configuration.GetSection(RabbitMQOptions.SectionName));
builder.Services.AddSingleton<IMessagePublisher, RabbitMQMessagePublisher>();

// Domain Services
builder.Services.AddSingleton<IUrlNormalizer, UrlNormalizer>();
builder.Services.AddSingleton<IDomainLinkRatioCalculator, DomainLinkRatioCalculator>();
builder.Services.AddSingleton<IJobTreeBuilder, JobTreeBuilder>();

// CORS policy for Frontend SPA
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Enums (job and page statuses) serialize as strings
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// ==========================================
// HTTP Request Pipeline
// ==========================================

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();

// Needed for WebApplicationFactory in integration tests
public partial class Program { }
