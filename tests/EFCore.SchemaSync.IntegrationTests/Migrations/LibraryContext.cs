using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFCore.SchemaSync.IntegrationTests.Migrations;

/// <summary>
/// An application that used EF Core migrations until now. The two migrations below are what <c>dotnet ef migrations add</c>
/// produces for this model (written out by hand so the test project needs no design-time tooling).
/// </summary>
public sealed class LibraryContext(DbContextOptions<LibraryContext> options) : DbContext(options)
{
    public DbSet<Author> Authors => Set<Author>();

    public DbSet<Book> Books => Set<Book>();

    /// <summary>When true the model contains a property no migration added yet (the "first change after switching" case).</summary>
    public static bool IncludeSubtitle { get; set; }

    public static DbContextOptions<LibraryContext> Options(string connectionString)
        => new DbContextOptionsBuilder<LibraryContext>()
            .UseSqlServer(connectionString)
            // Migrations normally ship with a model snapshot; these hand-written ones don't, which EF would report as pending changes.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

    private readonly bool _includeSubtitle = IncludeSubtitle;

    internal bool HasSubtitle => _includeSubtitle;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.ReplaceService<IModelCacheKeyFactory, SubtitleAwareCacheKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Author>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(100);
        });
        modelBuilder.Entity<Book>(e =>
        {
            e.Property(p => p.Title).HasMaxLength(200);
            e.Property(p => p.Isbn).HasMaxLength(20);
            e.HasIndex(p => p.Isbn).IsUnique().HasFilter("[Isbn] IS NOT NULL");
            e.Property(p => p.InStock).HasDefaultValue(true);
            if (!_includeSubtitle)
            {
                e.Ignore(p => p.Subtitle);
            }
            else
            {
                e.Property(p => p.Subtitle).HasMaxLength(200);
            }
        });
    }
}

internal sealed class SubtitleAwareCacheKey : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => (context.GetType(), context is LibraryContext library && library.HasSubtitle, designTime);
}

public sealed class Author
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<Book> Books { get; set; } = [];
}

public sealed class Book
{
    public int Id { get; set; }

    public int AuthorId { get; set; }

    public Author? Author { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Subtitle { get; set; }

    public string? Isbn { get; set; }

    public bool InStock { get; set; }
}
