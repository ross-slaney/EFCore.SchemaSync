using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.Extensions.Logging;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>Undeclared renames come back with the exact line that declares them.</summary>
[TestClass]
public sealed class RenameHintTests
{
    private static readonly SchemaShape V1 = SchemaShape.V1 with { Phone = true };

    private static async Task<TestDatabase> PopulatedAsync()
    {
        var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(V1);
        await db.ExecuteAsync("INSERT INTO dbo.Customers (Name, Email, Phone) VALUES (N'Ada', N'ada@example.test', N'555-0100'), (N'Grace', NULL, N'555-0101')");
        return db;
    }

    [TestMethod]
    public async Task A_blocked_column_rename_suggests_WasRenamedFrom_and_the_suggestion_works()
    {
        await using var db = await PopulatedAsync();

        var blocked = await Assert.ThrowsExactlyAsync<SchemaChangesBlockedException>(() => db.ApplyShapeAsync(V1 with { PhoneColumn = "PhoneNumber" }));

        var hint = blocked.RenameHints.Single();
        Assert.AreEqual(RenameHintKind.Column, hint.Kind);
        Assert.AreEqual("[dbo].[Customers].[Phone]", hint.OldName);
        Assert.AreEqual("[dbo].[Customers].[PhoneNumber]", hint.NewName);
        Assert.AreEqual("modelBuilder.Entity<EvolvingCustomer>().Property(e => e.Phone).WasRenamedFrom(\"Phone\");", hint.Suggestion);
        StringAssert.Contains(blocked.Message, hint.Suggestion);

        // Doing exactly what the hint says turns the blocked drop into an in-place rename.
        var applied = await db.ApplyShapeAsync(V1 with { PhoneColumn = "PhoneNumber", PhoneRenamedFrom = "Phone" });
        Assert.AreEqual(SchemaSyncOutcome.Applied, applied.Outcome, applied.Describe());
        Assert.AreEqual(0, applied.RenameHints.Count);
        Assert.AreEqual("555-0100", await db.ScalarAsync<string>("SELECT PhoneNumber FROM dbo.Customers WHERE Name = N'Ada'"));
    }

    [TestMethod]
    public async Task Two_renames_of_the_same_type_are_ambiguous_and_produce_no_hint()
    {
        await using var db = await PopulatedAsync();

        var blocked = await Assert.ThrowsExactlyAsync<SchemaChangesBlockedException>(() => db.ApplyShapeAsync(V1 with { PhoneColumn = "PhoneNumber", EmailColumn = "EmailAddress" }));

        Assert.AreEqual(0, blocked.RenameHints.Count, string.Join(" | ", blocked.RenameHints));
        Assert.IsFalse(blocked.Message.Contains("WasRenamedFrom", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task A_dropped_column_next_to_a_new_column_of_another_type_produces_no_hint()
    {
        await using var db = await PopulatedAsync();

        var blocked = await Assert.ThrowsExactlyAsync<SchemaChangesBlockedException>(() => db.ApplyShapeAsync(SchemaShape.V1 with { Tier = true }));

        Assert.AreEqual(0, blocked.RenameHints.Count, string.Join(" | ", blocked.RenameHints));
    }

    [TestMethod]
    public async Task An_undeclared_entity_rename_warns_with_the_suggestion()
    {
        await using var db = await PopulatedAsync();

        var result = await db.ApplyShapeAsync(V1 with { CustomersTable = "Clients" });

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        var hint = result.RenameHints.Single();
        Assert.AreEqual(RenameHintKind.Table, hint.Kind);
        Assert.AreEqual("[dbo].[Customers]", hint.OldName);
        Assert.AreEqual("[dbo].[Clients]", hint.NewName);
        Assert.AreEqual("modelBuilder.Entity<EvolvingCustomer>().WasRenamedFrom(\"Customers\");", hint.Suggestion);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains(hint.Suggestion, StringComparison.Ordinal)), string.Join(" | ", result.Warnings));
        Assert.IsTrue(db.Logs.Library.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("possible undeclared rename", StringComparison.Ordinal)));
        Assert.AreEqual(0, await db.CountAsync("dbo.Clients"), "without the declaration the data stays in the old table");
        Assert.AreEqual(2, await db.CountAsync("dbo.Customers"));
    }

    [TestMethod]
    public async Task An_undeclared_entity_rename_and_move_suggests_the_previous_schema()
    {
        await using var db = await PopulatedAsync();

        var preview = await db.ApplyShapeAsync(V1 with { CustomersTable = "Clients", CustomersSchema = "crm" }, new SchemaSyncOptions { DryRun = true });

        Assert.AreEqual(SchemaSyncOutcome.Previewed, preview.Outcome);
        Assert.AreEqual("modelBuilder.Entity<EvolvingCustomer>().WasRenamedFrom(\"Customers\", \"dbo\");", preview.RenameHints.Single().Suggestion);
        Assert.IsFalse(await db.ObjectExistsAsync("crm.Clients"), "dry run changed nothing");
    }

    [TestMethod]
    public async Task A_column_rename_on_an_empty_table_proceeds_with_a_warning()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(V1);

        var result = await db.ApplyShapeAsync(V1 with { PhoneColumn = "Mobile" });

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.AreEqual("[dbo].[Customers].[Mobile]", result.RenameHints.Single().NewName);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("WasRenamedFrom(\"Phone\")", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Ordinary_changes_produce_no_hints()
    {
        await using var db = await PopulatedAsync();

        var result = await db.ApplyShapeAsync(V1 with { AuditLog = true, EmailIndex = true });

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.AreEqual(0, result.RenameHints.Count, "a new table with different columns is not a rename");
        Assert.IsFalse(result.Warnings.Any(w => w.Contains("rename", StringComparison.OrdinalIgnoreCase)));
    }
}
