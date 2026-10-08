using Microsoft.EntityFrameworkCore;

namespace SchemaSync.Sample;

/// <summary>
/// The only schema definition the sample maintains. There are no migrations, snapshots or SQL files;
/// EFCore.SchemaSync deploys exactly this model at startup.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TodoList> Lists => Set<TodoList>();

    public DbSet<TodoItem> Items => Set<TodoItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("app");

        modelBuilder.Entity<TodoList>(list =>
        {
            list.ToTable("Lists");
            list.Property(l => l.Name).HasMaxLength(200);
            list.HasIndex(l => l.Name).IsUnique();
            list.Property(l => l.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        modelBuilder.Entity<TodoItem>(item =>
        {
            item.ToTable("Items");
            item.Property(i => i.Title).HasMaxLength(500);
            item.Property(i => i.Priority).HasDefaultValue(3);
            item.Property(i => i.IsDone).HasDefaultValue(false);
            item.Property(i => i.Notes).HasMaxLength(4000);
            item.HasOne(i => i.List).WithMany(l => l.Items).HasForeignKey(i => i.ListId).OnDelete(DeleteBehavior.Cascade);
            item.HasIndex(i => new { i.ListId, i.IsDone });
            item.HasIndex(i => i.DueDate).HasFilter("[DueDate] IS NOT NULL");
        });
    }
}

public sealed class TodoList
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<TodoItem> Items { get; set; } = [];
}

public sealed class TodoItem
{
    public int Id { get; set; }

    public int ListId { get; set; }

    public TodoList? List { get; set; }

    public required string Title { get; set; }

    public string? Notes { get; set; }

    public int Priority { get; set; }

    public bool IsDone { get; set; }

    public DateOnly? DueDate { get; set; }
}
