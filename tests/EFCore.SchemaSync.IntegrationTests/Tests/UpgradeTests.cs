using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.EntityFrameworkCore;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>Upgrades of populated databases: data is preserved and every upgrade converges on the second apply.</summary>
[TestClass]
public sealed class UpgradeTests
{
    private static async Task<TestDatabase> PopulatedV1Async(int rows = 3)
    {
        var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);
        await using var context = db.OpenShape(SchemaShape.V1);
        for (var i = 1; i <= rows; i++)
        {
            context.Customers.Add(new EvolvingCustomer { Name = $"Customer {i}", Email = i % 2 == 0 ? $"c{i}@example.test" : null });
        }

        await context.SaveChangesAsync();
        return db;
    }

    [TestMethod]
    public async Task Adding_a_table_with_a_foreign_key_and_an_index_keeps_existing_rows()
    {
        await using var db = await PopulatedV1Async();
        var shape = SchemaShape.V1 with { AuditLog = true, EmailIndex = true };

        var result = await db.ApplyShapeAsync(shape);

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        var changes = result.Changes.Select(c => c.ToString()).ToList();
        CollectionAssert.Contains(changes, "Create Schema [audit]");
        CollectionAssert.Contains(changes, "Create Table [audit].[AuditEntries]");
        CollectionAssert.Contains(changes, "Create Index [audit].[AuditEntries].[IX_AuditEntries_CustomerId_At]");
        CollectionAssert.Contains(changes, "Create ForeignKeyConstraint [audit].[FK_AuditEntries_Customers_CustomerId]");
        CollectionAssert.Contains(changes, "Create Index [dbo].[Customers].[IX_Customers_Email]");
        Assert.AreEqual(3, await db.CountAsync("dbo.Customers"));

        await using (var context = db.OpenShape(shape))
        {
            var first = await context.Customers.OrderBy(c => c.Id).FirstAsync();
            context.Add(new AuditEntry { CustomerId = first.Id, Message = "created", At = DateTime.UtcNow });
            await context.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateException>(async () =>
            {
                context.Add(new AuditEntry { CustomerId = 999_999, Message = "orphan", At = DateTime.UtcNow });
                await context.SaveChangesAsync();
            });
        }

        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, (await db.ApplyShapeAsync(shape)).Outcome);
    }

    [TestMethod]
    public async Task Widening_a_column_keeps_data()
    {
        await using var db = await PopulatedV1Async();
        var longName = new string('x', 100);
        await db.ExecuteAsync($"UPDATE dbo.Customers SET Name = N'{longName}' WHERE Id = 1");
        var shape = SchemaShape.V1 with { NameLength = 200 };

        var result = await db.ApplyShapeAsync(shape);

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.AreEqual(0, result.DataLossRisks.Count, "widening is not lossy");
        Assert.AreEqual(200, (await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Name"))!.Length);
        Assert.AreEqual(longName, await db.ScalarAsync<string>("SELECT Name FROM dbo.Customers WHERE Id = 1"));
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, (await db.ApplyShapeAsync(shape)).Outcome);
    }

    [TestMethod]
    public async Task Adding_a_required_column_with_a_default_fills_existing_rows()
    {
        await using var db = await PopulatedV1Async();
        var shape = SchemaShape.V1 with { Tier = true };

        var result = await db.ApplyShapeAsync(shape);

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        var tier = await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Tier");
        Assert.IsFalse(tier!.IsNullable);
        Assert.AreEqual("((1))", tier.DefaultDefinition);
        Assert.AreEqual(3, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customers WHERE Tier = 1"));
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, (await db.ApplyShapeAsync(shape)).Outcome);
    }

    [TestMethod]
    public async Task Changing_a_default_replaces_the_constraint_and_converges()
    {
        await using var db = await PopulatedV1Async();
        var shape = SchemaShape.V1 with { PriorityDefault = 7 };

        var result = await db.ApplyShapeAsync(shape);

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.IsTrue(result.Changes.Any(c => c.ObjectType == "DefaultConstraint" && c.Kind == SchemaChangeKind.Create), result.Describe());
        Assert.AreEqual("((7))", (await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Priority"))!.DefaultDefinition);
        await db.ExecuteAsync("INSERT INTO dbo.Customers (Name) VALUES (N'later')");
        Assert.AreEqual(7, await db.ScalarAsync<int>("SELECT Priority FROM dbo.Customers WHERE Name = N'later'"));
        Assert.AreEqual(3, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customers WHERE Priority = 3"), "existing rows keep their values");
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, (await db.ApplyShapeAsync(shape)).Outcome);
    }

    [TestMethod]
    public async Task Dry_run_previews_changes_and_the_script_without_modifying_anything()
    {
        await using var db = await PopulatedV1Async();
        var shape = SchemaShape.V1 with { Phone = true, AuditLog = true };

        var preview = await db.ApplyShapeAsync(shape, new SchemaSyncOptions { DryRun = true });

        Assert.AreEqual(SchemaSyncOutcome.Previewed, preview.Outcome, preview.Describe());
        Assert.IsTrue(preview.Changes.Any(c => c.ObjectName == "[audit].[AuditEntries]"), preview.Describe());
        Assert.IsTrue(preview.Changes.Any(c => c.Kind == SchemaChangeKind.Alter && c.ObjectName == "[dbo].[Customers]"), preview.Describe());
        Assert.IsNotNull(preview.DeploymentScript);
        StringAssert.Contains(preview.DeploymentScript, "CREATE TABLE [audit].[AuditEntries]");
        StringAssert.Contains(preview.DeploymentScript, "ALTER TABLE [dbo].[Customers]");
        StringAssert.Contains(preview.DeploymentScript, "[Phone]");

        Assert.IsFalse(await db.ColumnExistsAsync("dbo.Customers", "Phone"), "dry run must not alter the table");
        Assert.IsFalse(await db.ObjectExistsAsync("audit.AuditEntries"), "dry run must not create tables");
        Assert.AreEqual(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.schemas WHERE name = 'audit'"), "dry run must not create schemas");
        Assert.AreEqual(3, await db.CountAsync("dbo.Customers"));

        var applied = await db.ApplyShapeAsync(shape, new SchemaSyncOptions { IncludeDeploymentScript = true });
        Assert.AreEqual(SchemaSyncOutcome.Applied, applied.Outcome);
        Assert.IsNotNull(applied.DeploymentScript, "IncludeDeploymentScript returns the script that was executed");
        Assert.IsTrue(await db.ColumnExistsAsync("dbo.Customers", "Phone"));
    }

    [TestMethod]
    public async Task Dry_run_of_a_lossy_change_reports_the_risk_instead_of_throwing()
    {
        await using var db = await PopulatedV1Async();
        var shape = SchemaShape.V1 with { NameLength = 50 };

        var preview = await db.ApplyShapeAsync(shape, new SchemaSyncOptions { DryRun = true });

        Assert.AreEqual(SchemaSyncOutcome.Previewed, preview.Outcome, preview.Describe());
        Assert.IsTrue(preview.DataLossRisks.Count > 0, "narrowing Name is flagged");
        StringAssert.Contains(preview.DataLossRisks[0], "Name");
        Assert.AreEqual(100, (await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Name"))!.Length, "nothing changed");
    }

    [TestMethod]
    public async Task Dry_run_on_a_current_database_reports_no_changes()
    {
        await using var db = await PopulatedV1Async();

        var preview = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { DryRun = true });

        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, preview.Outcome, preview.Describe());
        Assert.IsNull(preview.DeploymentScript);
    }
}
