using Alteva.Domain.Entities;
using Alteva.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Alteva.Infrastructure.Data;

/// <summary>
/// The main Entity Framework Core DbContext for the Alteva Web Crawler.
/// Responsible for persisting Jobs, Pages, and Edges to SQL Server.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// The collection of Crawl Jobs.
    /// </summary>
    public DbSet<Job> Jobs { get; set; }

    /// <summary>
    /// The collection of discovered HTML pages and their computed Domain Link Ratios.
    /// </summary>
    public DbSet<Page> Pages { get; set; }

    /// <summary>
    /// The collection of directed links (edges) between pages.
    /// </summary>
    public DbSet<Edge> Edges { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ==========================================
        // Job Configuration
        // ==========================================
        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasKey(e => e.Id);
            
            entity.Property(e => e.InputUrl)
                .IsRequired()
                .HasMaxLength(2048);

            // Store the enum as a string for readability in the DB
            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(50);
        });

        // ==========================================
        // Page Configuration
        // ==========================================
        modelBuilder.Entity<Page>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Url)
                .IsRequired()
                .HasMaxLength(2048);

            entity.Property(e => e.UrlHash)
                .HasMaxLength(UrlHasher.HashLength)
                .IsFixedLength();

            // CRITICAL: Idempotency constraint.
            // A specific URL should only have one Page record per Job.
            // Keyed on the URL hash: the URL itself can exceed SQL Server's index key size and
            // would compare case-insensitively under the default collation.
            entity.HasIndex(e => new { e.JobId, e.UrlHash })
                .IsUnique();

            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(20);

            entity.Property(e => e.FailureReason)
                .HasMaxLength(2048);

            // Supports the job-completion check: "any Queued pages left for this job?"
            entity.HasIndex(e => new { e.JobId, e.Status });

            // Setup foreign key relationship
            entity.HasOne<Job>()
                .WithMany()
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ==========================================
        // Edge Configuration
        // ==========================================
        modelBuilder.Entity<Edge>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ParentUrl)
                .IsRequired()
                .HasMaxLength(2048);

            entity.Property(e => e.ChildUrl)
                .IsRequired()
                .HasMaxLength(2048);

            // No unique index: a page's edges are deduplicated in memory and written in the same
            // transaction that marks the page Done, and a Done page is never processed again.
            // (A URL-based unique index would also exceed SQL Server's 1700-byte key limit.)

            // Setup foreign key relationship
            entity.HasOne<Job>()
                .WithMany()
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
