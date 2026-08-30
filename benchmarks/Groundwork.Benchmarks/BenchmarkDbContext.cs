using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Groundwork.Benchmarks;

public sealed class BenchmarkDbContext(DbContextOptions<BenchmarkDbContext> options) : DbContext(options)
{
    public DbSet<BenchmarkItem> Items => Set<BenchmarkItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<BenchmarkItem>();
        item.ToTable("benchmark_items");
        item.HasKey(value => value.Id);
        item.Property(value => value.Id).HasColumnName("id").IsRequired();
        item.Property(value => value.Category).HasColumnName("category").IsRequired();
        item.Property(value => value.Sequence).HasColumnName("sequence").IsRequired();
        item.Property(value => value.Payload).HasColumnName("payload").IsRequired();
        item.HasIndex(value => new { value.Category, value.Id })
            .HasDatabaseName("ix_benchmark_items_category_id");
    }

    internal static BenchmarkDbContext Create(DbConnection connection)
    {
        var options = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseSqlite(connection)
            .UseModel(BenchmarkDbContextModel.Instance)
            .Options;
        return new BenchmarkDbContext(options);
    }
}

public sealed class BenchmarkItem
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string Payload { get; set; } = string.Empty;
}

internal sealed class BenchmarkDbContextFactory : IDesignTimeDbContextFactory<BenchmarkDbContext>
{
    public BenchmarkDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseSqlite("Data Source=groundwork-benchmark-design.db")
            .Options);
}
