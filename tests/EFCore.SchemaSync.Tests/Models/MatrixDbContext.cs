using Microsoft.EntityFrameworkCore;

namespace EFCore.SchemaSync.Tests.Models;

/// <summary>
/// Exercises every mapping category the library supports: custom schemas, every common column type, nullability,
/// lengths and precision, fixed-length and non-Unicode strings, collation, sparse and rowversion columns, identity
/// (default and seeded), primary keys (clustered, non-clustered, composite, string), foreign keys with each delete
/// behavior and a self reference, alternate keys, check constraints, constant and SQL defaults of every literal kind,
/// virtual and persisted computed columns, plain/unique/filtered/composite/descending/include indexes with fill
/// factor, sequences, owned types, TPH inheritance, temporal tables and comments.
/// </summary>
public sealed class MatrixDbContext(DbContextOptions<MatrixDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<Country> Countries => Set<Country>();

    public DbSet<Employee> Employees => Set<Employee>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<int>("OrderNumbers", "sales").StartsAt(1000).IncrementsBy(1);

        modelBuilder.Entity<Customer>(e =>
        {
            e.ToTable("Customers", t => t.HasComment("Customer master data"));
            e.Property(p => p.Name).HasMaxLength(200).HasDefaultValue("anonymous").HasComment("Display name");
            e.Property(p => p.Email).HasMaxLength(320);
            e.HasIndex(p => p.Email).IsUnique().HasFilter("[Email] IS NOT NULL");
            e.Property(p => p.Code).HasMaxLength(10).IsFixedLength();
            e.Property(p => p.Notes).IsUnicode(false);
            e.Property(p => p.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            e.Property(p => p.IsActive).HasDefaultValue(true);
            e.Property(p => p.Balance).HasPrecision(18, 2).HasDefaultValue(0m);
            e.Property(p => p.Weight).HasDefaultValue(1.5);
            e.Property(p => p.Score).HasDefaultValue(2.5f);
            e.Property(p => p.Priority).HasDefaultValue(-1);
            e.Property(p => p.BigNumber).HasDefaultValue(9_000_000_000L);
            e.Property(p => p.Since).HasDefaultValue(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            e.Property(p => p.Token).HasDefaultValueSql("NEWID()");
            e.Property(p => p.Kind).HasConversion<string>().HasMaxLength(20).HasDefaultValue(CustomerKind.Retail);
            e.Property(p => p.RowVersion).IsRowVersion();
            e.Property(p => p.Avatar).HasMaxLength(1024);
            e.Property(p => p.SparseNote).IsSparse();
            e.Property(p => p.CollatedName).HasMaxLength(100).UseCollation("Latin1_General_CS_AS");
            e.Property(p => p.Rounded).HasPrecision(18, 2).HasComputedColumnSql("CAST([Balance] * 2 AS decimal(18,2))", stored: true);
            e.Property(p => p.Label).HasMaxLength(250).HasComputedColumnSql("[Code] + N': ' + [Name]", stored: false);
            e.OwnsOne(p => p.Address, a =>
            {
                a.Property(x => x.Street).HasMaxLength(200);
                a.Property(x => x.City).HasMaxLength(100);
            });
            e.HasIndex(p => new { p.Name, p.CreatedAt }).IsDescending(false, true).IncludeProperties(p => new { p.Balance }).HasFillFactor(80);
        });

        modelBuilder.Entity<Order>(e =>
        {
            e.ToTable("Orders", "sales", t =>
            {
                t.HasCheckConstraint("CK_Orders_Subtotal", "[Subtotal] >= 0");
                t.HasCheckConstraint("CK_Orders_Status", "[Status] IN (0, 1, 2)");
            });
            e.Property(p => p.Number).HasDefaultValueSql("NEXT VALUE FOR [sales].[OrderNumbers]");
            e.HasAlternateKey(p => p.Number);
            e.Property(p => p.Subtotal).HasColumnType("decimal(18,2)");
            e.Property(p => p.Tax).HasColumnType("decimal(18,2)");
            e.Property(p => p.Total).HasColumnType("decimal(18,2)").HasComputedColumnSql("[Subtotal] + [Tax]", stored: true);
            e.HasOne(p => p.Customer).WithMany(c => c.Orders).HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.CustomerId, p.PlacedAt });
        });

        modelBuilder.Entity<OrderLine>(e =>
        {
            e.ToTable("OrderLines", "sales");
            e.HasKey(p => new { p.OrderId, p.LineNumber });
            e.HasOne(p => p.Order).WithMany(o => o.Lines).HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.Property(p => p.ProductSku).HasMaxLength(50);
            e.Property(p => p.UnitPrice).HasPrecision(10, 4);
            e.Property(p => p.LineTotal).HasPrecision(18, 4).HasComputedColumnSql("[Quantity] * [UnitPrice]", stored: false);
        });

        modelBuilder.Entity<Payment>(e =>
        {
            e.ToTable("Payments");
            e.HasDiscriminator<string>("PaymentType").HasValue<CardPayment>("card").HasValue<BankPayment>("bank");
            e.Property(p => p.Amount).HasPrecision(18, 2);
        });
        modelBuilder.Entity<CardPayment>(e => e.Property(p => p.CardLast4).HasMaxLength(4));
        modelBuilder.Entity<BankPayment>(e => e.Property(p => p.Iban).HasMaxLength(34));

        modelBuilder.Entity<Ticket>(e =>
        {
            e.ToTable("Tickets");
            e.Property(p => p.Id).UseIdentityColumn(1000, 5);
            e.HasKey(p => p.Id).IsClustered(false);
            e.HasOne(p => p.ParentTicket).WithMany().HasForeignKey(p => p.ParentTicketId).OnDelete(DeleteBehavior.ClientSetNull);
            e.Property(p => p.Title).HasMaxLength(100);
        });

        modelBuilder.Entity<Country>(e =>
        {
            e.ToTable("Countries");
            e.HasKey(p => p.Code);
            e.Property(p => p.Code).HasMaxLength(2).IsFixedLength();
            e.Property(p => p.Name).HasMaxLength(100);
        });

        modelBuilder.Entity<Employee>(e =>
        {
            e.ToTable("Employees", t => t.IsTemporal());
            e.Property(p => p.Name).HasMaxLength(100);
        });
    }
}

public enum CustomerKind
{
    Retail,
    Wholesale,
}

public enum OrderStatus
{
    New,
    Paid,
    Shipped,
}

public sealed class Address
{
    public string Street { get; set; } = string.Empty;

    public string? City { get; set; }
}

public sealed class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string Code { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool IsActive { get; set; }

    public decimal Balance { get; set; }

    public double Rating { get; set; }

    public float Score { get; set; }

    public double Weight { get; set; }

    public int Priority { get; set; }

    public long BigNumber { get; set; }

    public DateTime Since { get; set; }

    public Guid Token { get; set; }

    public CustomerKind Kind { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public byte[]? Avatar { get; set; }

    public DateOnly? Birthday { get; set; }

    public TimeOnly? Alarm { get; set; }

    public TimeSpan Duration { get; set; }

    public DateTimeOffset Offset { get; set; }

    public Guid ExternalId { get; set; }

    public short SmallNumber { get; set; }

    public byte TinyNumber { get; set; }

    public string? SparseNote { get; set; }

    public string? CollatedName { get; set; }

    public decimal Rounded { get; set; }

    public string? Label { get; set; }

    public Address Address { get; set; } = new();

    public List<Order> Orders { get; set; } = [];
}

public sealed class Order
{
    public Guid Id { get; set; }

    public int CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public int Number { get; set; }

    public decimal Subtotal { get; set; }

    public decimal Tax { get; set; }

    public decimal Total { get; set; }

    public OrderStatus Status { get; set; }

    public DateTime PlacedAt { get; set; }

    public List<OrderLine> Lines { get; set; } = [];
}

public sealed class OrderLine
{
    public Guid OrderId { get; set; }

    public int LineNumber { get; set; }

    public Order? Order { get; set; }

    public string ProductSku { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal LineTotal { get; set; }
}

public abstract class Payment
{
    public int Id { get; set; }

    public decimal Amount { get; set; }
}

public sealed class CardPayment : Payment
{
    public string CardLast4 { get; set; } = string.Empty;
}

public sealed class BankPayment : Payment
{
    public string Iban { get; set; } = string.Empty;
}

public sealed class Ticket
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? ParentTicketId { get; set; }

    public Ticket? ParentTicket { get; set; }
}

public sealed class Country
{
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class Employee
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
