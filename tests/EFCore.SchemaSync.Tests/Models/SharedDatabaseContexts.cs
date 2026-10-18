using Microsoft.EntityFrameworkCore;

namespace EFCore.SchemaSync.Tests.Models;

public sealed class Invoice
{
    public int Id { get; set; }

    public string Number { get; set; } = string.Empty;
}

public sealed class Shipment
{
    public int Id { get; set; }

    public string Carrier { get; set; } = string.Empty;
}

public sealed class Membership
{
    public int Id { get; set; }

    public string Role { get; set; } = string.Empty;
}

/// <summary>Owns schema "billing".</summary>
public sealed class BillingContext(DbContextOptions<BillingContext> options) : DbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("billing");
        modelBuilder.Entity<Invoice>(e =>
        {
            e.ToTable("Invoices");
            e.Property(p => p.Number).HasMaxLength(30);
            e.HasIndex(p => p.Number).IsUnique();
        });
    }
}

/// <summary>Owns schema "shipping".</summary>
public sealed class ShippingContext(DbContextOptions<ShippingContext> options) : DbContext(options)
{
    public DbSet<Shipment> Shipments => Set<Shipment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("shipping");
        modelBuilder.Entity<Shipment>(e =>
        {
            e.ToTable("Shipments");
            e.Property(p => p.Carrier).HasMaxLength(50);
        });
    }
}

/// <summary>
/// Maps a table another library owns (like SqlOS does for its own tables): <c>dbo.Memberships</c> is excluded from
/// migrations, so the context reads it but never deploys, reports or drops it.
/// </summary>
public sealed class ForeignTableContext(DbContextOptions<ForeignTableContext> options) : DbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<Membership> Memberships => Set<Membership>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>(e =>
        {
            e.ToTable("Invoices");
            e.Property(p => p.Number).HasMaxLength(30);
        });
        modelBuilder.Entity<Membership>(e =>
        {
            e.ToTable("Memberships", t => t.ExcludeFromMigrations());
            e.Property(p => p.Role).HasMaxLength(20);
        });
    }
}
