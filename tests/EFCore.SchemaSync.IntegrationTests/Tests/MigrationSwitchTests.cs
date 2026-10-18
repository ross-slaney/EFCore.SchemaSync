using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.IntegrationTests.Migrations;
using Microsoft.EntityFrameworkCore;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>The steps of docs/migrating-from-ef-migrations.md, executed against a database built by EF Core migrations.</summary>
[TestClass]
[DoNotParallelize]
public sealed class MigrationSwitchTests
{
    private static async Task<TestDatabase> MigratedAsync()
    {
        LibraryContext.IncludeSubtitle = false;
        var db = await TestDatabase.CreateAsync();
        await using var context = new LibraryContext(LibraryContext.Options(db.ConnectionString));
        await context.Database.MigrateAsync();
        context.Authors.Add(new Author { Name = "Ursula", Books = [new Book { Title = "The Dispossessed", Isbn = "978-0060512750" }] });
        await context.SaveChangesAsync();
        return db;
    }

    private static async Task<SchemaSyncResult> SwitchAsync(TestDatabase db, SchemaSyncOptions? options = null)
    {
        await using var context = new LibraryContext(LibraryContext.Options(db.ConnectionString));
        return await SchemaSync.ApplyAsync(context, options ?? new SchemaSyncOptions());
    }

    [TestMethod]
    public async Task A_database_built_by_migrations_is_already_current_and_keeps_its_history_table()
    {
        await using var db = await MigratedAsync();
        Assert.AreEqual(2, await db.CountAsync("dbo.__EFMigrationsHistory"));

        var verified = await SwitchAsync(db, new SchemaSyncOptions { DryRun = true });
        var first = await SwitchAsync(db);

        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, verified.Outcome, "step 3 of the guide: the dry run shows nothing to do: " + verified.Describe());
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, first.Outcome, first.Describe());
        Assert.IsTrue(first.RetainedObjects.Any(r => r.Name == "[dbo].[__EFMigrationsHistory]"), "the history table is reported, never touched: " + first.Describe());
        Assert.AreEqual(2, await db.CountAsync("dbo.__EFMigrationsHistory"));
        Assert.AreEqual(1, await db.CountAsync("dbo.Books"));
    }

    [TestMethod]
    public async Task The_first_model_change_after_switching_is_applied_without_a_migration()
    {
        await using var db = await MigratedAsync();
        LibraryContext.IncludeSubtitle = true;
        try
        {
            var result = await SwitchAsync(db);

            Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
            Assert.AreEqual("Alter Table [dbo].[Books]", result.Changes.Single().ToString());
            Assert.IsTrue(await db.ColumnExistsAsync("dbo.Books", "Subtitle"));
            Assert.AreEqual("The Dispossessed", await db.ScalarAsync<string>("SELECT Title FROM dbo.Books"));
        }
        finally
        {
            LibraryContext.IncludeSubtitle = false;
        }
    }

    [TestMethod]
    public async Task The_history_table_can_be_removed_once_you_are_sure()
    {
        await using var db = await MigratedAsync();

        var blocked = await Assert.ThrowsExactlyAsync<SchemaChangesBlockedException>(() => SwitchAsync(db, new SchemaSyncOptions { AllowObjectRemoval = true }));
        Assert.IsTrue(blocked.DataLossRisks.Any(r => r.Contains("__EFMigrationsHistory", StringComparison.Ordinal)), "the history table has rows, so removal alone is not enough");

        var removed = await SwitchAsync(db, new SchemaSyncOptions { AllowObjectRemoval = true, AllowDataLoss = true });
        Assert.IsTrue(removed.Changes.Any(c => c.Kind == SchemaChangeKind.Drop && c.ObjectName == "[dbo].[__EFMigrationsHistory]"), removed.Describe());
        Assert.IsFalse(await db.ObjectExistsAsync("dbo.__EFMigrationsHistory"));
        Assert.AreEqual(1, await db.CountAsync("dbo.Books"), "application data is untouched");
    }

    [TestMethod]
    public async Task Dropping_the_history_table_by_hand_is_equivalent()
    {
        await using var db = await MigratedAsync();

        await db.ExecuteAsync("DROP TABLE dbo.__EFMigrationsHistory");
        var result = await SwitchAsync(db);

        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, result.Outcome, result.Describe());
        Assert.AreEqual(0, result.RetainedObjects.Count);
    }
}
