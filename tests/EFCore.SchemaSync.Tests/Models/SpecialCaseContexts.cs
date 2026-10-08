using Microsoft.EntityFrameworkCore;

namespace EFCore.SchemaSync.Tests.Models;

public sealed class Widget
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Memory-optimized tables need database-level filegroup configuration: explicitly unsupported.</summary>
public sealed class MemoryOptimizedDbContext(DbContextOptions<MemoryOptimizedDbContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Widget>().ToTable("Widgets", t => t.IsMemoryOptimized());
}

/// <summary>HasData seed rows are skipped with a warning; the schema itself deploys.</summary>
public sealed class SeedDataDbContext(DbContextOptions<SeedDataDbContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Widget>().ToTable("Widgets").HasData(new Widget { Id = 1, Name = "Seeded" }, new Widget { Id = 2, Name = "Also seeded" });
}

public sealed class Left
{
    public int Id { get; set; }

    public int? RightId { get; set; }

    public Right? Right { get; set; }
}

public sealed class Right
{
    public int Id { get; set; }

    public int LeftId { get; set; }

    public Left? Left { get; set; }
}

/// <summary>Cyclic foreign keys make EF emit a separate ALTER TABLE ... ADD CONSTRAINT.</summary>
public sealed class CyclicDbContext(DbContextOptions<CyclicDbContext> options) : DbContext(options)
{
    public DbSet<Left> Lefts => Set<Left>();

    public DbSet<Right> Rights => Set<Right>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Left>().HasOne(l => l.Right).WithMany().HasForeignKey(l => l.RightId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Right>().HasOne(r => r.Left).WithMany().HasForeignKey(r => r.LeftId).OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary>A plain context with the default schema overridden, as many applications do.</summary>
public sealed class DefaultSchemaDbContext(DbContextOptions<DefaultSchemaDbContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("app");
        modelBuilder.Entity<Widget>(e =>
        {
            e.ToTable("Widgets");
            e.Property(p => p.Name).HasMaxLength(100);
            e.HasIndex(p => p.Name).IsUnique();
        });
    }
}
