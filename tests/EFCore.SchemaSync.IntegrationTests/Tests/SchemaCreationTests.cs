using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.EntityFrameworkCore;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>Creation of the mapped schema in an empty database, one assertion group per supported mapping category.</summary>
[TestClass]
public sealed class SchemaCreationTests
{
    [TestMethod]
    public async Task Empty_database_gets_every_mapped_object_and_a_second_apply_changes_nothing()
    {
        await using var db = await TestDatabase.CreateAsync();

        var result = await db.ApplyAsync<MatrixDbContext>();
        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.AreEqual(0, result.DataLossRisks.Count);
        Assert.AreEqual(0, result.RetainedObjects.Count);

        // Tables and custom schemas.
        var tables = await SchemaInspector.TablesAsync(db);
        CollectionAssert.IsSubsetOf(
            new[] { "dbo.Countries", "dbo.Customers", "dbo.Employees", "dbo.EmployeesHistory", "dbo.Payments", "dbo.Tickets", "sales.OrderLines", "sales.Orders" },
            tables);
        Assert.AreEqual(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.schemas WHERE name = 'sales'"));

        // Column types, nullability, lengths, precision.
        await AssertColumn(db, "dbo.Customers", "Name", "nvarchar", length: 200, nullable: false);
        await AssertColumn(db, "dbo.Customers", "Email", "nvarchar", length: 320, nullable: true);
        await AssertColumn(db, "dbo.Customers", "Code", "nchar", length: 10, nullable: false);
        await AssertColumn(db, "dbo.Customers", "Notes", "varchar", length: -1, nullable: true);
        await AssertColumn(db, "dbo.Customers", "Balance", "decimal", precision: 18, scale: 2, nullable: false);
        await AssertColumn(db, "dbo.Customers", "Rating", "float", nullable: false);
        await AssertColumn(db, "dbo.Customers", "Score", "real", nullable: false);
        await AssertColumn(db, "dbo.Customers", "Avatar", "varbinary", length: 1024, nullable: true);
        await AssertColumn(db, "dbo.Customers", "Birthday", "date", nullable: true);
        await AssertColumn(db, "dbo.Customers", "Alarm", "time", nullable: true);
        await AssertColumn(db, "dbo.Customers", "Duration", "time", nullable: false);
        await AssertColumn(db, "dbo.Customers", "Offset", "datetimeoffset", nullable: false);
        await AssertColumn(db, "dbo.Customers", "ExternalId", "uniqueidentifier", nullable: false);
        await AssertColumn(db, "dbo.Customers", "BigNumber", "bigint", nullable: false);
        await AssertColumn(db, "dbo.Customers", "SmallNumber", "smallint", nullable: false);
        await AssertColumn(db, "dbo.Customers", "TinyNumber", "tinyint", nullable: false);
        await AssertColumn(db, "dbo.Customers", "Kind", "nvarchar", length: 20, nullable: false);
        await AssertColumn(db, "dbo.Customers", "RowVersion", "timestamp", nullable: false);
        await AssertColumn(db, "dbo.Customers", "Address_Street", "nvarchar", length: 200, nullable: false);
        await AssertColumn(db, "dbo.Customers", "Address_City", "nvarchar", length: 100, nullable: true);
        await AssertColumn(db, "sales.OrderLines", "UnitPrice", "decimal", precision: 10, scale: 4, nullable: false);
        await AssertColumn(db, "dbo.Countries", "Code", "nchar", length: 2, nullable: false);

        var sparse = await SchemaInspector.ColumnAsync(db, "dbo.Customers", "SparseNote");
        Assert.IsTrue(sparse!.IsSparse, "SPARSE column");
        var collated = await SchemaInspector.ColumnAsync(db, "dbo.Customers", "CollatedName");
        Assert.AreEqual("Latin1_General_CS_AS", collated!.Collation, "column collation");

        // Identity columns.
        var id = await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Id");
        Assert.IsTrue(id!.IsIdentity);
        Assert.AreEqual("1/1", await SchemaInspector.IdentityAsync(db, "dbo.Customers"));
        Assert.AreEqual("1000/5", await SchemaInspector.IdentityAsync(db, "dbo.Tickets"), "identity seed and increment");

        // Defaults of every literal kind.
        await AssertDefault(db, "dbo.Customers", "Name", "(N'anonymous')");
        await AssertDefault(db, "dbo.Customers", "CreatedAt", "(getutcdate())");
        await AssertDefault(db, "dbo.Customers", "IsActive", "(CONVERT([bit],(1)))");
        await AssertDefault(db, "dbo.Customers", "Balance", "((0.0))");
        await AssertDefault(db, "dbo.Customers", "Weight", "((1.5000000000000000e+000))");
        await AssertDefault(db, "dbo.Customers", "Priority", "((-1))");
        await AssertDefault(db, "dbo.Customers", "BigNumber", "(CONVERT([bigint],(9000000000.)))");
        await AssertDefault(db, "dbo.Customers", "Since", "('2024-01-02T03:04:05.0000000Z')");
        await AssertDefault(db, "dbo.Customers", "Token", "(newid())");
        await AssertDefault(db, "dbo.Customers", "Kind", "(N'Retail')");
        await AssertDefault(db, "sales.Orders", "Number", "(NEXT VALUE FOR [sales].[OrderNumbers])");

        // Computed columns, virtual and persisted.
        var rounded = await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Rounded");
        Assert.IsTrue(rounded!.IsComputed && rounded.IsPersisted, "persisted computed column");
        Assert.AreEqual("(CONVERT([decimal](18,2),[Balance]*(2)))", rounded.Definition);
        var label = await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Label");
        Assert.IsTrue(label!.IsComputed && !label.IsPersisted, "virtual computed column");
        var total = await SchemaInspector.ColumnAsync(db, "sales.Orders", "Total");
        Assert.IsTrue(total!.IsComputed && total.IsPersisted);
        Assert.AreEqual("([Subtotal]+[Tax])", total.Definition);

        // Primary keys: clustered, non-clustered, composite, string.
        var pkCustomers = await SchemaInspector.IndexAsync(db, "dbo.Customers", "PK_Customers");
        Assert.IsTrue(pkCustomers!.IsPrimaryKey && pkCustomers.IsClustered);
        var pkTickets = await SchemaInspector.IndexAsync(db, "dbo.Tickets", "PK_Tickets");
        Assert.IsTrue(pkTickets!.IsPrimaryKey && !pkTickets.IsClustered, "non-clustered primary key");
        var pkLines = await SchemaInspector.IndexAsync(db, "sales.OrderLines", "PK_OrderLines");
        Assert.AreEqual("OrderId ASC, LineNumber ASC", pkLines!.KeyColumns, "composite primary key");
        Assert.AreEqual("Code ASC", (await SchemaInspector.IndexAsync(db, "dbo.Countries", "PK_Countries"))!.KeyColumns);

        // Foreign keys with each delete behavior, including a self reference across schemas.
        var fkOrders = await SchemaInspector.ForeignKeyAsync(db, "FK_Orders_Customers_CustomerId");
        Assert.AreEqual(new ForeignKeyInfo("dbo.Customers", "CASCADE", "CustomerId"), fkOrders);
        var fkLines = await SchemaInspector.ForeignKeyAsync(db, "FK_OrderLines_Orders_OrderId");
        Assert.AreEqual(new ForeignKeyInfo("sales.Orders", "NO_ACTION", "OrderId"), fkLines);
        var fkTickets = await SchemaInspector.ForeignKeyAsync(db, "FK_Tickets_Tickets_ParentTicketId");
        Assert.AreEqual(new ForeignKeyInfo("dbo.Tickets", "NO_ACTION", "ParentTicketId"), fkTickets);

        // Alternate key (unique constraint) and check constraints.
        var ak = await SchemaInspector.IndexAsync(db, "sales.Orders", "AK_Orders_Number");
        Assert.IsTrue(ak!.IsUnique && !ak.IsPrimaryKey);
        Assert.AreEqual("([Subtotal]>=(0))", await SchemaInspector.CheckConstraintAsync(db, "CK_Orders_Subtotal"));
        Assert.AreEqual("([Status]=(2) OR [Status]=(1) OR [Status]=(0))", await SchemaInspector.CheckConstraintAsync(db, "CK_Orders_Status"));

        // Indexes: unique filtered, composite descending with include and fill factor, plain.
        var emailIndex = await SchemaInspector.IndexAsync(db, "dbo.Customers", "IX_Customers_Email");
        Assert.IsTrue(emailIndex!.IsUnique);
        Assert.AreEqual("([Email] IS NOT NULL)", emailIndex.Filter);
        var nameIndex = await SchemaInspector.IndexAsync(db, "dbo.Customers", "IX_Customers_Name_CreatedAt");
        Assert.AreEqual("Name ASC, CreatedAt DESC", nameIndex!.KeyColumns);
        Assert.AreEqual("Balance", nameIndex.IncludedColumns);
        Assert.AreEqual(80, nameIndex.FillFactor);
        Assert.IsFalse(nameIndex.IsUnique);
        Assert.IsNotNull(await SchemaInspector.IndexAsync(db, "sales.Orders", "IX_Orders_CustomerId_PlacedAt"));
        Assert.IsNotNull(await SchemaInspector.IndexAsync(db, "dbo.Tickets", "IX_Tickets_ParentTicketId"));

        // Sequence, TPH discriminator, temporal table, comments.
        Assert.AreEqual("1000/1", await db.ScalarAsync<string>("SELECT CONVERT(nvarchar(50), start_value) + '/' + CONVERT(nvarchar(50), increment) FROM sys.sequences WHERE name = 'OrderNumbers' AND SCHEMA_NAME(schema_id) = 'sales'"));
        await AssertColumn(db, "dbo.Payments", "PaymentType", "nvarchar", length: 8, nullable: false);
        await AssertColumn(db, "dbo.Payments", "CardLast4", "nvarchar", length: 4, nullable: true);
        Assert.AreEqual(2, await db.ScalarAsync<int>("SELECT temporal_type FROM sys.tables WHERE name = 'Employees'"), "system-versioned temporal table");
        Assert.AreEqual(1, await db.ScalarAsync<int>("SELECT temporal_type FROM sys.tables WHERE name = 'EmployeesHistory'"), "history table");
        Assert.AreEqual("Customer master data", await SchemaInspector.ExtendedPropertyAsync(db, "dbo.Customers", null));
        Assert.AreEqual("Display name", await SchemaInspector.ExtendedPropertyAsync(db, "dbo.Customers", "Name"));

        // The model can be used through EF right away.
        await using (var context = db.OpenMatrix())
        {
            var customer = new Customer { Code = "C-1", Duration = TimeSpan.FromMinutes(1), Offset = DateTimeOffset.UtcNow, ExternalId = Guid.NewGuid(), Address = new Address { Street = "Main St" } };
            context.Customers.Add(customer);
            context.Orders.Add(new Order { Id = Guid.NewGuid(), Customer = customer, Subtotal = 10, Tax = 2, PlacedAt = DateTime.UtcNow });
            context.Employees.Add(new Employee { Name = "Ada" });
            await context.SaveChangesAsync();
            var order = await context.Orders.SingleAsync();
            Assert.AreEqual(12m, order.Total, "persisted computed column is read back");
            Assert.IsTrue(order.Number >= 1000, "sequence default applied");
        }

        // Idempotency across every category above.
        var again = await db.ApplyAsync<MatrixDbContext>();
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, again.Outcome, again.Describe());
        Assert.AreEqual(0, again.Changes.Count);
    }

    [TestMethod]
    public async Task A_database_created_by_EnsureCreated_is_recognized_as_current()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using (var context = db.OpenMatrix())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var result = await db.ApplyAsync<MatrixDbContext>();

        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, result.Outcome, result.Describe());
        Assert.AreEqual(0, result.Changes.Count);
    }

    [TestMethod]
    public async Task Database_with_a_non_default_collation_deploys_and_converges()
    {
        await using var db = await TestDatabase.CreateAsync(collation: "Latin1_General_100_CI_AS_SC");

        var first = await db.ApplyAsync<MatrixDbContext>();
        var second = await db.ApplyAsync<MatrixDbContext>();

        Assert.AreEqual(SchemaSyncOutcome.Applied, first.Outcome, first.Describe());
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, second.Outcome, second.Describe());
        Assert.AreEqual("Latin1_General_100_CI_AS_SC", await db.ScalarAsync<string>("SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Customers') AND name = 'Name'"));
    }

    [TestMethod]
    public async Task Model_with_a_default_schema_deploys_into_that_schema()
    {
        await using var db = await TestDatabase.CreateAsync();

        var result = await db.ApplyAsync<DefaultSchemaDbContext>();

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.IsTrue(await db.ObjectExistsAsync("app.Widgets"));
        Assert.IsTrue((await SchemaInspector.IndexAsync(db, "app.Widgets", "IX_Widgets_Name"))!.IsUnique);
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, (await db.ApplyAsync<DefaultSchemaDbContext>()).Outcome);
    }

    [TestMethod]
    public async Task Seed_data_is_not_deployed_but_the_schema_is_and_the_result_warns()
    {
        await using var db = await TestDatabase.CreateAsync();

        var result = await db.ApplyAsync<SeedDataDbContext>();

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.IsTrue(await db.ObjectExistsAsync("dbo.Widgets"));
        Assert.AreEqual(0, await db.CountAsync("dbo.Widgets"), "HasData rows are not inserted");
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("HasData", StringComparison.Ordinal)), string.Join(" | ", result.Warnings));
    }

    [TestMethod]
    public async Task Cyclic_foreign_keys_deploy_and_converge()
    {
        await using var db = await TestDatabase.CreateAsync();

        var first = await db.ApplyAsync<CyclicDbContext>();
        var second = await db.ApplyAsync<CyclicDbContext>();

        Assert.AreEqual(SchemaSyncOutcome.Applied, first.Outcome);
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, second.Outcome, second.Describe());
        Assert.IsNotNull(await SchemaInspector.ForeignKeyAsync(db, "FK_Lefts_Rights_RightId"));
        Assert.IsNotNull(await SchemaInspector.ForeignKeyAsync(db, "FK_Rights_Lefts_LeftId"));
    }

    private static async Task AssertColumn(TestDatabase db, string table, string column, string dataType, int? length = null, byte? precision = null, byte? scale = null, bool? nullable = null)
    {
        var info = await SchemaInspector.ColumnAsync(db, table, column);
        Assert.IsNotNull(info, $"{table}.{column} should exist");
        Assert.AreEqual(dataType, info.DataType, $"{table}.{column} type");
        if (length is { } expectedLength)
        {
            Assert.AreEqual(expectedLength, info.Length, $"{table}.{column} length");
        }

        if (precision is { } expectedPrecision)
        {
            Assert.AreEqual(expectedPrecision, info.Precision, $"{table}.{column} precision");
            Assert.AreEqual(scale, info.Scale, $"{table}.{column} scale");
        }

        if (nullable is { } expectedNullable)
        {
            Assert.AreEqual(expectedNullable, info.IsNullable, $"{table}.{column} nullability");
        }
    }

    private static async Task AssertDefault(TestDatabase db, string table, string column, string expectedDefinition)
    {
        var info = await SchemaInspector.ColumnAsync(db, table, column);
        Assert.AreEqual(expectedDefinition, info?.DefaultDefinition, $"{table}.{column} default");
    }
}
