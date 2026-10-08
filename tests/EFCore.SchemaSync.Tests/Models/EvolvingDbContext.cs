using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFCore.SchemaSync.Tests.Models;

/// <summary>
/// Describes which "version" of the evolving model to build. Each flag toggles one schema change so tests can
/// move a populated database between versions and assert what the library does.
/// </summary>
public sealed record SchemaShape
{
    /// <summary>Map the Customers table at all (false simulates removing an entity from the model).</summary>
    public bool Customers { get; init; } = true;

    /// <summary>Nullable column added to a populated table.</summary>
    public bool Phone { get; init; }

    /// <summary>Required column with a default added to a populated table.</summary>
    public bool Tier { get; init; }

    /// <summary>Required column without a default: fails on a populated table (deployment error).</summary>
    public bool Mandatory { get; init; }

    /// <summary>Max length of Customers.Name; shrinking it is a lossy change.</summary>
    public int NameLength { get; init; } = 100;

    /// <summary>Filtered index on Customers.Email.</summary>
    public bool EmailIndex { get; init; }

    /// <summary>Unique index on Customers.Email: fails to deploy when existing rows share an email.</summary>
    public bool UniqueEmailIndex { get; init; }

    /// <summary>Second table with a foreign key to Customers.</summary>
    public bool AuditLog { get; init; }

    /// <summary>Default value for Customers.Priority (null = no default).</summary>
    public int? PriorityDefault { get; init; } = 3;

    /// <summary>Column name of the Phone property (rename it to exercise refactor operations).</summary>
    public string PhoneColumn { get; init; } = "Phone";

    /// <summary>Previous column name of Phone, declared with <c>WasRenamedFrom</c>.</summary>
    public string? PhoneRenamedFrom { get; init; }

    /// <summary>Table name of the Customers entity.</summary>
    public string CustomersTable { get; init; } = "Customers";

    /// <summary>Schema of the Customers table (null = dbo).</summary>
    public string? CustomersSchema { get; init; }

    /// <summary>Previous table name of Customers, declared with <c>WasRenamedFrom</c>.</summary>
    public string? CustomersRenamedFrom { get; init; }

    /// <summary>Previous schema of Customers, declared with <c>WasRenamedFrom</c>/<c>WasMovedFromSchema</c>.</summary>
    public string? CustomersMovedFromSchema { get; init; }

    public static SchemaShape V1 => new();
}

public sealed class EvolvingCustomer
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public int Tier { get; set; }

    public int Mandatory { get; set; }

    public int Priority { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }

    public int CustomerId { get; set; }

    public EvolvingCustomer? Customer { get; set; }

    public string Message { get; set; } = string.Empty;

    public DateTime At { get; set; }
}

public sealed class EvolvingDbContext(DbContextOptions<EvolvingDbContext> options, SchemaShape shape) : DbContext(options)
{
    public SchemaShape Shape { get; } = shape;

    public DbSet<EvolvingCustomer> Customers => Set<EvolvingCustomer>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.ReplaceService<IModelCacheKeyFactory, ShapeModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (!Shape.Customers)
        {
            modelBuilder.Ignore<EvolvingCustomer>();
            modelBuilder.Ignore<AuditEntry>();
            modelBuilder.Entity<Marker>().ToTable("Markers");
            return;
        }

        modelBuilder.Entity<EvolvingCustomer>(e =>
        {
            if (Shape.CustomersSchema is null)
            {
                e.ToTable(Shape.CustomersTable);
            }
            else
            {
                e.ToTable(Shape.CustomersTable, Shape.CustomersSchema);
            }

            if (Shape.CustomersRenamedFrom is not null)
            {
                e.WasRenamedFrom(Shape.CustomersRenamedFrom, Shape.CustomersMovedFromSchema);
            }
            else if (Shape.CustomersMovedFromSchema is not null)
            {
                e.WasMovedFromSchema(Shape.CustomersMovedFromSchema);
            }

            e.Property(p => p.Name).HasMaxLength(Shape.NameLength);
            e.Property(p => p.Email).HasMaxLength(320);

            if (Shape.Phone)
            {
                var phone = e.Property(p => p.Phone).HasMaxLength(30).HasColumnName(Shape.PhoneColumn);
                if (Shape.PhoneRenamedFrom is not null)
                {
                    phone.WasRenamedFrom(Shape.PhoneRenamedFrom);
                }
            }
            else
            {
                e.Ignore(p => p.Phone);
            }

            if (Shape.Tier)
            {
                e.Property(p => p.Tier).HasDefaultValue(1);
            }
            else
            {
                e.Ignore(p => p.Tier);
            }

            if (!Shape.Mandatory)
            {
                e.Ignore(p => p.Mandatory);
            }

            if (Shape.PriorityDefault is { } priority)
            {
                e.Property(p => p.Priority).HasDefaultValue(priority);
            }

            if (Shape.UniqueEmailIndex)
            {
                e.HasIndex(p => p.Email).IsUnique();
            }
            else if (Shape.EmailIndex)
            {
                e.HasIndex(p => p.Email).HasFilter("[Email] IS NOT NULL");
            }
        });

        if (Shape.AuditLog)
        {
            modelBuilder.Entity<AuditEntry>(e =>
            {
                e.ToTable("AuditEntries", "audit");
                e.Property(p => p.Message).HasMaxLength(1000);
                e.HasOne(p => p.Customer).WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(p => new { p.CustomerId, p.At });
            });
        }
        else
        {
            modelBuilder.Ignore<AuditEntry>();
        }

        modelBuilder.Entity<Marker>().ToTable("Markers");
    }

    /// <summary>Always-present table so a "remove every entity" version still has a model to deploy.</summary>
    public sealed class Marker
    {
        public int Id { get; set; }
    }

    private sealed class ShapeModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => context is EvolvingDbContext evolving ? (context.GetType(), evolving.Shape, designTime) : (context.GetType(), designTime);
    }
}
