using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace EFCore.SchemaSync.Tests.Models;

// ---------- Inheritance: TPT and TPC ----------
public abstract class Animal
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class Dog : Animal
{
    public string Breed { get; set; } = string.Empty;
}

public sealed class Cat : Animal
{
    public bool Indoor { get; set; }
}

public abstract class Vehicle
{
    public int Id { get; set; }

    public string Plate { get; set; } = string.Empty;
}

public sealed class Car : Vehicle
{
    public int Seats { get; set; }
}

public sealed class Truck : Vehicle
{
    public decimal Payload { get; set; }
}

// ---------- Many-to-many, table splitting, complex types, HiLo, primitive collections ----------
public sealed class Post
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public List<Tag> Tags { get; set; } = [];

    public List<string> Keywords { get; set; } = [];

    public PostStats Stats { get; set; } = new();

    public Money Price { get; set; } = new();
}

public sealed class Tag
{
    public int Id { get; set; }

    public string Label { get; set; } = string.Empty;

    public List<Post> Posts { get; set; } = [];
}

/// <summary>Shares the Posts table with <see cref="Post"/> (table splitting).</summary>
public sealed class PostStats
{
    public int Id { get; set; }

    public int Views { get; set; }

    public int Likes { get; set; }
}

/// <summary>Complex type mapped to columns.</summary>
public sealed class Money
{
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "EUR";
}

public sealed class Ticket2
{
    public long Id { get; set; }

    public string Subject { get; set; } = string.Empty;
}

public sealed class InheritanceAndRelationshipsContext(DbContextOptions<InheritanceAndRelationshipsContext> options) : DbContext(options)
{
    public DbSet<Animal> Animals => Set<Animal>();

    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    public DbSet<Post> Posts => Set<Post>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<Ticket2> Tickets => Set<Ticket2>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Animal>(e =>
        {
            e.UseTptMappingStrategy();
            e.Property(p => p.Name).HasMaxLength(100);
        });
        modelBuilder.Entity<Dog>(e => e.Property(p => p.Breed).HasMaxLength(50));
        modelBuilder.Entity<Cat>();

        modelBuilder.Entity<Vehicle>(e =>
        {
            e.UseTpcMappingStrategy();
            e.Property(p => p.Plate).HasMaxLength(20);
        });
        modelBuilder.Entity<Truck>(e => e.Property(p => p.Payload).HasPrecision(10, 2));
        modelBuilder.Entity<Car>();

        modelBuilder.Entity<Post>(e =>
        {
            e.ToTable("Posts");
            e.Property(p => p.Title).HasMaxLength(200);
            e.HasMany(p => p.Tags).WithMany(t => t.Posts);
            e.HasOne(p => p.Stats).WithOne().HasForeignKey<PostStats>(s => s.Id);
            e.ComplexProperty(p => p.Price, money =>
            {
                money.Property(m => m.Amount).HasPrecision(18, 2);
                money.Property(m => m.Currency).HasMaxLength(3).IsFixedLength();
            });
        });
        modelBuilder.Entity<PostStats>().ToTable("Posts");
        modelBuilder.Entity<Tag>(e => e.Property(p => p.Label).HasMaxLength(50));

        modelBuilder.Entity<Ticket2>(e =>
        {
            e.ToTable("HiLoTickets");
            e.Property(p => p.Id).UseHiLo("TicketIds");
            e.Property(p => p.Subject).HasMaxLength(100);
        });
    }
}

// ---------- JSON columns and vectors (SQL Server 2025 types when compatibility level 170) ----------
public sealed class Profile
{
    public string Theme { get; set; } = "light";

    public List<string> Interests { get; set; } = [];
}

public sealed class ShippingAddress
{
    public string Street { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;
}

public sealed class Document
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public Profile Settings { get; set; } = new();

    public ShippingAddress Address { get; set; } = new();
}

public sealed class Embedded
{
    public int Id { get; set; }

    public SqlVector<float> Embedding { get; set; }
}

public sealed class JsonContext(DbContextOptions<JsonContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Document>(e =>
        {
            e.Property(p => p.Title).HasMaxLength(200);
            e.OwnsOne(p => p.Settings, s => s.ToJson());
            e.ComplexProperty(p => p.Address, a => a.ToJson());
        });
}

public sealed class VectorContext(DbContextOptions<VectorContext> options) : DbContext(options)
{
    public DbSet<Embedded> Items => Set<Embedded>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Embedded>(e => e.Property(p => p.Embedding).HasColumnType("vector(3)"));
}
