using EFCore.SchemaSync.Conversion;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.Dac;

namespace EFCore.SchemaSync.Tests;

[TestClass]
public sealed class SchemaConverterTests
{
    [TestMethod]
    public void Matrix_model_converts_to_a_dacpac_containing_every_object_category()
    {
        using var context = TestContextFactory.Matrix();

        var package = SchemaConverter.Convert(context);

        var objects = package.Objects.Select(o => $"{o.ObjectType} {o.Name}").ToList();
        CollectionAssert.Contains(objects, "Schema [sales]");
        CollectionAssert.Contains(objects, "Table [dbo].[Customers]");
        CollectionAssert.Contains(objects, "Table [sales].[Orders]");
        CollectionAssert.Contains(objects, "Table [sales].[OrderLines]");
        CollectionAssert.Contains(objects, "Table [dbo].[Payments]");
        CollectionAssert.Contains(objects, "Table [dbo].[Tickets]");
        CollectionAssert.Contains(objects, "Table [dbo].[Countries]");
        CollectionAssert.Contains(objects, "Table [dbo].[Employees]");
        CollectionAssert.Contains(objects, "Table [dbo].[EmployeesHistory]");
        CollectionAssert.Contains(objects, "Sequence [sales].[OrderNumbers]");
        CollectionAssert.Contains(objects, "Index [dbo].[Customers].[IX_Customers_Email]");
        CollectionAssert.Contains(objects, "Index [dbo].[Customers].[IX_Customers_Name_CreatedAt]");
        CollectionAssert.Contains(objects, "Index [sales].[Orders].[IX_Orders_CustomerId_PlacedAt]");
        CollectionAssert.Contains(objects, "PrimaryKeyConstraint [dbo].[PK_Customers]");
        CollectionAssert.Contains(objects, "PrimaryKeyConstraint [sales].[PK_OrderLines]");
        CollectionAssert.Contains(objects, "ForeignKeyConstraint [sales].[FK_Orders_Customers_CustomerId]");
        CollectionAssert.Contains(objects, "ForeignKeyConstraint [dbo].[FK_Tickets_Tickets_ParentTicketId]");
        CollectionAssert.Contains(objects, "UniqueConstraint [sales].[AK_Orders_Number]");
        CollectionAssert.Contains(objects, "CheckConstraint [sales].[CK_Orders_Subtotal]");
        Assert.IsTrue(objects.Any(o => o.StartsWith("ExtendedProperty ", StringComparison.Ordinal) && o.Contains("[Customers]", StringComparison.Ordinal)), "table comment should become an extended property");
        Assert.IsTrue(objects.Any(o => o.StartsWith("ExtendedProperty ", StringComparison.Ordinal) && o.Contains("[Name]", StringComparison.Ordinal)), "column comment should become an extended property");
        Assert.AreEqual(0, package.SkippedSeedStatements);
        Assert.IsTrue(package.Content.Length > 0);
        Assert.AreEqual(nameof(MatrixDbContext), package.Name);
    }

    [TestMethod]
    public void Schema_creation_guard_becomes_a_declarative_create_schema()
    {
        using var context = TestContextFactory.Matrix();

        var package = SchemaConverter.Convert(context);

        StringAssert.Contains(package.NormalizedScript, "CREATE SCHEMA [sales];");
        Assert.IsFalse(package.NormalizedScript.Contains("SCHEMA_ID", StringComparison.OrdinalIgnoreCase), "the IF SCHEMA_ID guard must not reach DacFx");
        Assert.IsFalse(package.NormalizedScript.Contains("EXEC(", StringComparison.OrdinalIgnoreCase), "dynamic SQL must not reach DacFx");
    }

    [TestMethod]
    public void Expressions_are_canonicalized_to_what_sql_server_stores()
    {
        using var context = TestContextFactory.Matrix();

        var script = SchemaConverter.Convert(context).NormalizedScript;

        StringAssert.Contains(script, "DEFAULT (CONVERT([bit], (1)))", "bool default: CAST becomes CONVERT with a bracketed type and parenthesized literal");
        StringAssert.Contains(script, "DEFAULT ((1.5000000000000000e+000))", "double default: scientific notation with 17 significant digits");
        StringAssert.Contains(script, "DEFAULT (CONVERT([real], (2.5)))", "float default: EF emits CAST(2.5 AS real)");
        StringAssert.Contains(script, "DEFAULT ((-1))", "negative int default");
        StringAssert.Contains(script, "DEFAULT (CONVERT([bigint], (9000000000.)))", "bigint literal beyond int range gets a trailing period");
        StringAssert.Contains(script, "DEFAULT ((0.0))", "decimal default");
        StringAssert.Contains(script, "AS (CONVERT([decimal](18,2), [Balance] * (2))) PERSISTED", "computed column CAST becomes CONVERT");
        StringAssert.Contains(script, "([Status]=(2) OR [Status]=(1) OR [Status]=(0))", "IN list in a check constraint becomes a reversed OR chain");
        StringAssert.Contains(script, "CHECK ([Subtotal] >= (0))", "comparison literal is parenthesized");
        StringAssert.Contains(script, "WHERE [Email] IS NOT NULL", "index filters are preserved");
        StringAssert.Contains(script, "IDENTITY(1000, 5)", "identity seed and increment are not expressions and stay untouched");
        StringAssert.Contains(script, "decimal(18,2) NOT NULL", "data type parameters stay untouched");
        StringAssert.Contains(script, "WITH (FILLFACTOR = 80)", "index options stay untouched");
    }

    [TestMethod]
    public void Temporal_table_dynamic_sql_is_resolved_into_a_create_table()
    {
        using var context = TestContextFactory.Matrix();

        var script = SchemaConverter.Convert(context).NormalizedScript;

        StringAssert.Contains(script, "PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])");
        StringAssert.Contains(script, "SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[EmployeesHistory])");
        Assert.IsFalse(script.Contains("@historyTableSchema", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Comments_become_named_extended_property_statements()
    {
        using var context = TestContextFactory.Matrix();

        var script = SchemaConverter.Convert(context).NormalizedScript;

        StringAssert.Contains(script, "EXECUTE sp_addextendedproperty @name = N'MS_Description', @value = N'Customer master data', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'Customers';");
        StringAssert.Contains(script, "@level2type = N'COLUMN', @level2name = N'Name';");
    }

    [TestMethod]
    public void Memory_optimized_tables_are_rejected_explicitly()
    {
        using var context = new MemoryOptimizedDbContext(TestContextFactory.SqlServerOptions<MemoryOptimizedDbContext>());

        var ex = Assert.ThrowsExactly<UnsupportedSchemaException>(() => SchemaConverter.Convert(context));

        Assert.AreEqual(SchemaSyncStage.ConvertModel, ex.Stage);
        Assert.IsTrue(ex.UnsupportedConstructs.Any(c => c.Contains("memory-optimized", StringComparison.OrdinalIgnoreCase)), string.Join(" | ", ex.UnsupportedConstructs));
        Assert.IsTrue(ex.UnsupportedConstructs.Any(c => c.Contains("[Widgets]", StringComparison.Ordinal)), "the offending table is named");
        StringAssert.Contains(ex.Message, "memory-optimized");
    }

    [TestMethod]
    public void Seed_data_is_skipped_with_a_warning_and_the_schema_still_converts()
    {
        using var context = new SeedDataDbContext(TestContextFactory.SqlServerOptions<SeedDataDbContext>());

        var package = SchemaConverter.Convert(context);

        Assert.AreEqual(1, package.SkippedSeedStatements, "EF batches seed rows into one INSERT");
        Assert.IsTrue(package.Warnings.Any(w => w.Contains("HasData", StringComparison.Ordinal) && w.Contains("1 INSERT", StringComparison.Ordinal)), string.Join(" | ", package.Warnings));
        Assert.IsTrue(package.Objects.Any(o => o.ObjectType == "Table" && o.Name == "[dbo].[Widgets]"));
        Assert.IsFalse(package.NormalizedScript.Contains("INSERT", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(package.NormalizedScript.Contains("IDENTITY_INSERT", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Cyclic_foreign_keys_keep_the_separate_alter_table_constraint()
    {
        using var context = new CyclicDbContext(TestContextFactory.SqlServerOptions<CyclicDbContext>());

        var package = SchemaConverter.Convert(context);

        var fks = package.Objects.Where(o => o.ObjectType == "ForeignKeyConstraint").Select(o => o.Name).ToList();
        CollectionAssert.Contains(fks, "[dbo].[FK_Lefts_Rights_RightId]");
        CollectionAssert.Contains(fks, "[dbo].[FK_Rights_Lefts_LeftId]");
        StringAssert.Contains(package.NormalizedScript, "ALTER TABLE [Lefts] ADD CONSTRAINT [FK_Lefts_Rights_RightId]");
    }

    [TestMethod]
    public void Default_schema_models_convert()
    {
        using var context = new DefaultSchemaDbContext(TestContextFactory.SqlServerOptions<DefaultSchemaDbContext>());

        var package = SchemaConverter.Convert(context);

        Assert.IsTrue(package.Objects.Any(o => o.ObjectType == "Schema" && o.Name == "[app]"));
        Assert.IsTrue(package.Objects.Any(o => o.ObjectType == "Table" && o.Name == "[app].[Widgets]"));
        Assert.IsTrue(package.Objects.Any(o => o.ObjectType == "Index" && o.Name == "[app].[Widgets].[IX_Widgets_Name]"));
    }

    [TestMethod]
    public void Non_sql_server_provider_is_rejected_at_the_resolve_stage()
    {
        var options = new DbContextOptionsBuilder<DefaultSchemaDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var context = new DefaultSchemaDbContext(options);

        var ex = Assert.ThrowsExactly<SchemaSyncException>(() => SchemaConverter.Convert(context));

        Assert.AreEqual(SchemaSyncStage.ResolveContext, ex.Stage);
        StringAssert.Contains(ex.Message, "Microsoft.EntityFrameworkCore.Sqlite");
    }

    [TestMethod]
    public void Unsupported_statements_are_all_reported_at_once()
    {
        const string script = """
            CREATE TABLE [Things] ([Id] int NOT NULL, CONSTRAINT [PK_Things] PRIMARY KEY ([Id]));
            GO
            CREATE VIEW [dbo].[vThings] AS SELECT [Id] FROM [Things];
            GO
            PRINT N'hello';
            GO
            IF OBJECT_ID(N'[Things]') IS NOT NULL EXEC(N'ALTER TABLE [Things] ADD [Extra] int NULL;');
            GO
            """;

        var ex = Assert.ThrowsExactly<UnsupportedSchemaException>(() => SchemaConverter.ConvertScript(script, "Test"));

        Assert.AreEqual(3, ex.UnsupportedConstructs.Count, string.Join(" | ", ex.UnsupportedConstructs));
        Assert.IsTrue(ex.UnsupportedConstructs.Any(c => c.StartsWith("View definition", StringComparison.Ordinal)));
        Assert.IsTrue(ex.UnsupportedConstructs.Any(c => c.Contains("PRINT", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(ex.UnsupportedConstructs.Any(c => c.StartsWith("Conditional (IF)", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Invalid_dacfx_model_fails_at_the_build_stage_with_details()
    {
        const string script = """
            CREATE TABLE [Orders] ([Id] int NOT NULL, [CustomerId] int NOT NULL,
                CONSTRAINT [PK_Orders] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_Orders_Missing] FOREIGN KEY ([CustomerId]) REFERENCES [Customers] ([Id]));
            GO
            """;

        var ex = Assert.ThrowsExactly<SchemaSyncException>(() => SchemaConverter.ConvertScript(script, "Test"));

        Assert.AreEqual(SchemaSyncStage.BuildPackage, ex.Stage);
        StringAssert.Contains(ex.Message, "SQL71501");
        StringAssert.Contains(ex.Message, "[Customers]");
    }

    [TestMethod]
    public void Dacpac_bytes_load_as_a_dac_package()
    {
        using var context = TestContextFactory.Matrix();
        var package = SchemaConverter.Convert(context);

        using var dacPackage = DacPackage.Load(package.OpenRead(), DacSchemaModelStorageType.Memory);

        Assert.AreEqual(nameof(MatrixDbContext), dacPackage.Name);
        Assert.AreEqual("Sql160", dacPackage.TargetPlatform.ToString());
    }

    [TestMethod]
    public void Package_can_be_saved_to_disk()
    {
        using var context = TestContextFactory.Matrix();
        var package = SchemaConverter.Convert(context);
        var path = Path.Combine(Path.GetTempPath(), $"schemasync-{Guid.NewGuid():N}.dacpac");

        try
        {
            package.Save(path);
            using var loaded = DacPackage.Load(path);
            Assert.AreEqual(nameof(MatrixDbContext), loaded.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
