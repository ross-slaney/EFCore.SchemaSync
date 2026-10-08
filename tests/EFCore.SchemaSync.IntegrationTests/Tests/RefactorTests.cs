using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.EntityFrameworkCore;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>Renames and schema moves through the refactor log: data stays in place instead of drop-and-create.</summary>
[TestClass]
public sealed class RefactorTests
{
    private static readonly SchemaShape V1 = SchemaShape.V1 with { Phone = true, EmailIndex = true };

    private static async Task<TestDatabase> PopulatedV1Async()
    {
        var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(V1);
        await using var context = db.OpenShape(V1);
        context.Customers.AddRange(
            new EvolvingCustomer { Name = "Ada", Email = "ada@example.test", Phone = "555-0100" },
            new EvolvingCustomer { Name = "Grace", Phone = "555-0101" },
            new EvolvingCustomer { Name = "Linus" });
        await context.SaveChangesAsync();
        return db;
    }

    [TestMethod]
    public async Task Renaming_a_column_with_WasRenamedFrom_keeps_the_data()
    {
        await using var db = await PopulatedV1Async();
        var renamed = V1 with { PhoneColumn = "PhoneNumber", PhoneRenamedFrom = "Phone" };

        var result = await db.ApplyShapeAsync(renamed);

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.AreEqual(0, result.DataLossRisks.Count, "a refactor rename is not lossy");
        Assert.IsTrue(result.Changes.Any(c => c.Kind == SchemaChangeKind.Rename && c.ObjectType == "SimpleColumn" && c.ObjectName == "[dbo].[Customers].[PhoneNumber]"), result.Describe());
        Assert.IsFalse(result.Changes.Any(c => c.Kind == SchemaChangeKind.Alter), "no drop-and-add of the column");

        Assert.IsTrue(await db.ColumnExistsAsync("dbo.Customers", "PhoneNumber"));
        Assert.IsFalse(await db.ColumnExistsAsync("dbo.Customers", "Phone"));
        Assert.AreEqual(3, await db.CountAsync("dbo.Customers"));
        Assert.AreEqual("555-0100", await db.ScalarAsync<string>("SELECT PhoneNumber FROM dbo.Customers WHERE Name = N'Ada'"));
        Assert.AreEqual(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.__RefactorLog"), "DacFx records the applied operation");

        await using (var context = db.OpenShape(renamed))
        {
            var grace = await context.Customers.SingleAsync(c => c.Name == "Grace");
            Assert.AreEqual("555-0101", grace.Phone, "EF reads the renamed column");
        }

        var again = await db.ApplyShapeAsync(renamed);
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, again.Outcome, again.Describe());
        Assert.IsFalse(again.RetainedObjects.Any(r => r.Name.Contains("__RefactorLog", StringComparison.Ordinal)), "DacFx's own bookkeeping table is not reported as retained");

        var withRemoval = await db.ApplyShapeAsync(renamed, new SchemaSyncOptions { AllowObjectRemoval = true });
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, withRemoval.Outcome, withRemoval.Describe());
        Assert.IsTrue(await db.ObjectExistsAsync("dbo.__RefactorLog"), "the bookkeeping table survives object removal");
    }

    [TestMethod]
    public async Task Renaming_and_moving_a_table_keeps_the_data_and_renames_its_conventional_dependents()
    {
        var withAudit = V1 with { AuditLog = true };
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(withAudit);
        await using (var context = db.OpenShape(withAudit))
        {
            var ada = new EvolvingCustomer { Name = "Ada", Email = "ada@example.test", Phone = "555-0100" };
            context.Customers.AddRange(ada, new EvolvingCustomer { Name = "Grace" });
            context.Add(new AuditEntry { Customer = ada, Message = "created", At = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }

        var moved = withAudit with { CustomersTable = "Clients", CustomersSchema = "crm", CustomersRenamedFrom = "Customers", CustomersMovedFromSchema = "dbo" };

        var result = await db.ApplyShapeAsync(moved);

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.AreEqual(0, result.DataLossRisks.Count);
        Assert.IsTrue(result.Changes.All(c => c.Kind is SchemaChangeKind.Rename or SchemaChangeKind.MoveSchema), "only renames and moves: " + result.Describe());
        Assert.IsTrue(result.Changes.Any(c => c.Kind == SchemaChangeKind.MoveSchema && c.ObjectName == "[crm].[Clients]"), result.Describe());
        Assert.IsTrue(await db.ObjectExistsAsync("crm.Clients"));
        Assert.IsFalse(await db.ObjectExistsAsync("dbo.Customers"));
        Assert.AreEqual(2, await db.CountAsync("crm.Clients"));
        Assert.AreEqual("555-0100", await db.ScalarAsync<string>("SELECT Phone FROM crm.Clients WHERE Name = N'Ada'"));

        // EF-conventional dependents were renamed in place, not dropped and recreated.
        Assert.IsTrue((await SchemaInspector.IndexAsync(db, "crm.Clients", "PK_Clients"))!.IsPrimaryKey);
        Assert.IsNull(await SchemaInspector.IndexAsync(db, "crm.Clients", "PK_Customers"));
        Assert.IsNotNull(await SchemaInspector.IndexAsync(db, "crm.Clients", "IX_Clients_Email"));
        Assert.IsNull(await SchemaInspector.IndexAsync(db, "crm.Clients", "IX_Customers_Email"));
        Assert.IsNotNull(await SchemaInspector.ForeignKeyAsync(db, "FK_AuditEntries_Clients_CustomerId"), "the foreign key from the other table follows the principal's new name");
        Assert.IsNull(await SchemaInspector.ForeignKeyAsync(db, "FK_AuditEntries_Customers_CustomerId"));
        Assert.AreEqual(1, await db.CountAsync("audit.AuditEntries"));

        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, (await db.ApplyShapeAsync(moved)).Outcome);
    }

    [TestMethod]
    public async Task Explicit_refactor_log_renames_a_table_and_its_index_in_place()
    {
        await using var db = await PopulatedV1Async();
        var renamed = V1 with { CustomersTable = "Clients" };
        var options = new SchemaSyncOptions
        {
            RefactorLog = new RefactorLog()
                .RenameTable("dbo", "Customers", "Clients")
                .RenameIndex("dbo", "Clients", "IX_Customers_Email", "IX_Clients_Email")
                .RenameConstraint("dbo", "Clients", RefactorConstraintKind.PrimaryKey, "PK_Customers", "PK_Clients"),
        };

        var result = await db.ApplyShapeAsync(renamed, options);

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.AreEqual(0, result.DataLossRisks.Count);
        Assert.IsTrue(result.Changes.All(c => c.Kind == SchemaChangeKind.Rename), "everything is a rename: " + result.Describe());
        Assert.IsNotNull(await SchemaInspector.IndexAsync(db, "dbo.Clients", "IX_Clients_Email"));
        Assert.IsNull(await SchemaInspector.IndexAsync(db, "dbo.Clients", "IX_Customers_Email"));
        Assert.IsTrue((await SchemaInspector.IndexAsync(db, "dbo.Clients", "PK_Clients"))!.IsPrimaryKey);
        Assert.AreEqual(3, await db.CountAsync("dbo.Clients"));
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, (await db.ApplyShapeAsync(renamed, options)).Outcome);
    }

    [TestMethod]
    public async Task An_ssdt_refactorlog_file_is_honored()
    {
        await using var db = await PopulatedV1Async();
        var renamed = V1 with { PhoneColumn = "Mobile" };
        var path = Path.Combine(Path.GetTempPath(), $"schemasync-{Guid.NewGuid():N}.refactorlog");
        new RefactorLog().RenameColumn("dbo", "Customers", "Phone", "Mobile").Save(path);
        try
        {
            var result = await db.ApplyShapeAsync(renamed, new SchemaSyncOptions { RefactorLogPath = path });

            Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
            Assert.IsTrue(result.Changes.Any(c => c.Kind == SchemaChangeKind.Rename), result.Describe());
            Assert.AreEqual("555-0101", await db.ScalarAsync<string>("SELECT Mobile FROM dbo.Customers WHERE Name = N'Grace'"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Without_a_refactor_entry_a_rename_is_a_blocked_drop()
    {
        await using var db = await PopulatedV1Async();

        var blocked = await Assert.ThrowsExactlyAsync<SchemaChangesBlockedException>(() => db.ApplyShapeAsync(V1 with { PhoneColumn = "PhoneNumber" }));

        Assert.IsTrue(blocked.DataLossRisks.Any(r => r.Contains("[Phone]", StringComparison.Ordinal)), blocked.Message);
        Assert.IsTrue(await db.ColumnExistsAsync("dbo.Customers", "Phone"));
    }

    [TestMethod]
    public async Task Dry_run_previews_renames_without_changing_anything()
    {
        await using var db = await PopulatedV1Async();
        var renamed = V1 with { PhoneColumn = "PhoneNumber", PhoneRenamedFrom = "Phone" };

        var preview = await db.ApplyShapeAsync(renamed, new SchemaSyncOptions { DryRun = true });

        Assert.AreEqual(SchemaSyncOutcome.Previewed, preview.Outcome, preview.Describe());
        Assert.IsTrue(preview.Changes.Any(c => c.Kind == SchemaChangeKind.Rename), preview.Describe());
        StringAssert.Contains(preview.DeploymentScript!, "sp_rename");
        Assert.IsTrue(await db.ColumnExistsAsync("dbo.Customers", "Phone"));
        Assert.IsFalse(await db.ObjectExistsAsync("dbo.__RefactorLog"));
    }

    [TestMethod]
    public async Task A_fresh_database_ignores_renames_of_objects_that_never_existed()
    {
        await using var db = await TestDatabase.CreateAsync();
        var renamed = V1 with { PhoneColumn = "PhoneNumber", PhoneRenamedFrom = "Phone", CustomersTable = "Clients", CustomersRenamedFrom = "Customers" };

        var result = await db.ApplyShapeAsync(renamed);

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.IsFalse(result.Changes.Any(c => c.Kind == SchemaChangeKind.Rename), "nothing to rename on a fresh database: " + result.Describe());
        Assert.IsTrue(await db.ColumnExistsAsync("dbo.Clients", "PhoneNumber"));
        int embedded;
        await using (var context = db.OpenShape(renamed))
        {
            embedded = EFCore.SchemaSync.Conversion.SchemaConverter.Convert(context).RefactorOperations.Count;
        }

        Assert.AreEqual(4, embedded, "column, table, primary key and index renames are embedded");
        Assert.AreEqual(embedded, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.__RefactorLog"), "the keys are still recorded so the operations never run later");
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, (await db.ApplyShapeAsync(renamed)).Outcome);
    }
}
