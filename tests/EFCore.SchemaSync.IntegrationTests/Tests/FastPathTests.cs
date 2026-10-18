using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>Unchanged restarts skip DacFx; every kind of change brings the full comparison back.</summary>
[TestClass]
public sealed class FastPathTests
{
    private static int ConversionCount(TestDatabase db)
        => db.Logs.Library.Count(e => e.Message.Contains("into a DACPAC", StringComparison.Ordinal));

    [TestMethod]
    public async Task An_unchanged_restart_skips_the_comparison_and_the_state_table_stays_invisible()
    {
        await using var db = await TestDatabase.CreateAsync();

        var first = await db.ApplyShapeAsync(SchemaShape.V1);
        var second = await db.ApplyShapeAsync(SchemaShape.V1);

        Assert.AreEqual(SchemaSyncOutcome.Applied, first.Outcome);
        Assert.IsFalse(first.ComparisonSkipped);
        Assert.IsFalse(first.Changes.Any(c => c.ObjectName.Contains("__SchemaSyncState", StringComparison.Ordinal)), first.Describe());
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, second.Outcome, second.Describe());
        Assert.IsTrue(second.ComparisonSkipped, second.Describe());
        Assert.AreEqual(0, second.Changes.Count);
        Assert.AreEqual(1, ConversionCount(db), "the second start never built a DACPAC");
        Assert.IsTrue(db.Logs.Library.Any(e => e.Message.Contains("comparison skipped", StringComparison.Ordinal)));
        Assert.IsTrue(await db.ObjectExistsAsync("dbo.__SchemaSyncState"));
        Assert.AreEqual(1, await db.CountAsync("dbo.__SchemaSyncState"));
        Assert.AreEqual(typeof(EvolvingDbContext).FullName, await db.ScalarAsync<string>("SELECT ContextKey FROM dbo.__SchemaSyncState"));

        var removal = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { AllowObjectRemoval = true });
        Assert.IsFalse(removal.ComparisonSkipped, "a different option set compares again");
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, removal.Outcome, removal.Describe());
        Assert.IsTrue(await db.ObjectExistsAsync("dbo.__SchemaSyncState"), "object removal never drops the library's own table");
        Assert.IsFalse(removal.RetainedObjects.Any(r => r.Name.Contains("__SchemaSyncState", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task A_model_change_runs_the_full_comparison_then_skips_again()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);
        await db.ApplyShapeAsync(SchemaShape.V1);

        var changed = await db.ApplyShapeAsync(SchemaShape.V1 with { Phone = true });
        var after = await db.ApplyShapeAsync(SchemaShape.V1 with { Phone = true });

        Assert.AreEqual(SchemaSyncOutcome.Applied, changed.Outcome, changed.Describe());
        Assert.IsFalse(changed.ComparisonSkipped);
        Assert.IsTrue(await db.ColumnExistsAsync("dbo.Customers", "Phone"));
        Assert.IsTrue(after.ComparisonSkipped);
    }

    [TestMethod]
    [DataRow("index", "CREATE INDEX IX_Oob ON dbo.Customers (Name)", SchemaSyncOutcome.NoChangesNeeded)]
    [DataRow("view", "CREATE VIEW dbo.vOob AS SELECT Id FROM dbo.Customers", SchemaSyncOutcome.NoChangesNeeded)]
    [DataRow("extra column", "ALTER TABLE dbo.Customers ADD Oob int NULL", SchemaSyncOutcome.Applied)]
    [DataRow("dropped default", "DECLARE @n sysname = (SELECT name FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID('dbo.Customers')); EXEC('ALTER TABLE dbo.Customers DROP CONSTRAINT ' + @n)", SchemaSyncOutcome.Applied)]
    [DataRow("renamed column", "EXEC sp_rename 'dbo.Customers.Email', 'Mail', 'COLUMN'", SchemaSyncOutcome.Applied)]
    [DataRow("extended property", "EXEC sp_addextendedproperty @name = N'Owner', @value = N'dba', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'Customers'", SchemaSyncOutcome.NoChangesNeeded)]
    public async Task Out_of_band_ddl_brings_the_full_comparison_back(string what, string ddl, SchemaSyncOutcome expected)
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);
        Assert.IsTrue((await db.ApplyShapeAsync(SchemaShape.V1)).ComparisonSkipped, "precondition: the fast path is active");

        await db.ExecuteAsync(ddl);
        var detected = await db.ApplyShapeAsync(SchemaShape.V1);
        var settled = await db.ApplyShapeAsync(SchemaShape.V1);

        Assert.IsFalse(detected.ComparisonSkipped, $"{what}: the change must be noticed");
        Assert.AreEqual(expected, detected.Outcome, $"{what}: {detected.Describe()}");
        Assert.IsTrue(settled.ComparisonSkipped, $"{what}: the next start is fast again");
        Assert.IsTrue(await db.ColumnExistsAsync("dbo.Customers", "Email"), "the model's schema is restored where the change touched it");
    }

    [TestMethod]
    public async Task Retained_objects_and_warnings_are_remembered_on_the_fast_path()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyAsync<SeedDataDbContext>();
        await db.ExecuteAsync("CREATE TABLE dbo.DbaOnly (Id int NOT NULL PRIMARY KEY)");

        var full = await db.ApplyAsync<SeedDataDbContext>();
        var fast = await db.ApplyAsync<SeedDataDbContext>();

        Assert.IsFalse(full.ComparisonSkipped);
        Assert.IsTrue(fast.ComparisonSkipped);
        CollectionAssert.AreEquivalent(full.RetainedObjects.ToList(), fast.RetainedObjects.ToList());
        Assert.IsTrue(fast.RetainedObjects.Any(r => r.Name == "[dbo].[DbaOnly]"));
        Assert.IsTrue(fast.Warnings.Any(w => w.Contains("HasData", StringComparison.Ordinal)), "conversion warnings survive the fast path");
    }

    [TestMethod]
    public async Task Dry_runs_always_compare()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);
        await db.ApplyShapeAsync(SchemaShape.V1);

        var preview = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { DryRun = true });

        Assert.IsFalse(preview.ComparisonSkipped);
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, preview.Outcome, preview.Describe());
    }

    [TestMethod]
    public async Task Disabled_fast_path_never_skips_and_never_creates_the_state_table()
    {
        await using var db = await TestDatabase.CreateAsync();
        var off = new SchemaSyncOptions { SkipComparisonWhenUnchanged = false };

        await db.ApplyShapeAsync(SchemaShape.V1, off);
        var again = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { SkipComparisonWhenUnchanged = false });

        Assert.IsFalse(again.ComparisonSkipped);
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, again.Outcome);
        Assert.IsFalse(await db.ObjectExistsAsync("dbo.__SchemaSyncState"));
    }

    [TestMethod]
    public async Task Turning_the_fast_path_off_later_keeps_the_existing_state_table_safe()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);

        var result = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { SkipComparisonWhenUnchanged = false, AllowObjectRemoval = true });

        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, result.Outcome, result.Describe());
        Assert.IsTrue(await db.ObjectExistsAsync("dbo.__SchemaSyncState"), "the populated state table is neither dropped nor blocking");
        Assert.IsFalse(result.RetainedObjects.Any(r => r.Name.Contains("__SchemaSyncState", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ConfigureDeployOptions_disables_the_fast_path()
    {
        await using var db = await TestDatabase.CreateAsync();
        SchemaSyncOptions Options() => new() { ConfigureDeployOptions = o => o.IgnoreColumnOrder = true };
        await db.ApplyShapeAsync(SchemaShape.V1, Options());

        Assert.IsFalse((await db.ApplyShapeAsync(SchemaShape.V1, Options())).ComparisonSkipped, "the callback's effect cannot be hashed");
    }

    [TestMethod]
    public async Task Hooks_still_run_when_the_comparison_is_skipped()
    {
        await using var db = await TestDatabase.CreateAsync();
        var runs = 0;
        SchemaSyncOptions Options() => new SchemaSyncOptions().AfterDeploy((_, _) => { runs++; return Task.CompletedTask; });

        await db.ApplyShapeAsync(SchemaShape.V1, Options());
        var fast = await db.ApplyShapeAsync(SchemaShape.V1, Options());

        Assert.IsTrue(fast.ComparisonSkipped);
        Assert.AreEqual(2, runs);
        Assert.AreEqual(1, fast.ExecutedHooks.Count);
    }

    [TestMethod]
    public async Task Ddl_done_by_a_hook_is_detected_on_the_next_start()
    {
        await using var db = await TestDatabase.CreateAsync();
        SchemaSyncOptions Options() => new SchemaSyncOptions().AfterDeploy((_, ct) => db.ExecuteAsync("IF OBJECT_ID('dbo.vHook') IS NULL EXEC('CREATE VIEW dbo.vHook AS SELECT 1 AS One')"));

        var first = await db.ApplyShapeAsync(SchemaShape.V1, Options());
        var second = await db.ApplyShapeAsync(SchemaShape.V1, Options());
        var third = await db.ApplyShapeAsync(SchemaShape.V1, Options());

        Assert.IsFalse(first.ComparisonSkipped);
        Assert.IsFalse(second.ComparisonSkipped, "the view the hook created after the state was recorded is a schema change");
        Assert.IsTrue(second.RetainedObjects.Any(r => r.Name == "[dbo].[vHook]"));
        Assert.IsTrue(third.ComparisonSkipped);
    }

    [TestMethod]
    public async Task A_database_created_by_EnsureCreated_gets_the_state_table_without_reporting_a_change()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using (var context = db.OpenMatrix())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var first = await db.ApplyAsync<MatrixDbContext>();
        var second = await db.ApplyAsync<MatrixDbContext>();

        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, first.Outcome, first.Describe());
        Assert.AreEqual(0, first.Changes.Count);
        Assert.IsTrue(await db.ObjectExistsAsync("dbo.__SchemaSyncState"));
        Assert.IsTrue(second.ComparisonSkipped);
    }

    [TestMethod]
    public async Task A_principal_that_cannot_write_the_state_degrades_to_full_comparisons()
    {
        await using var db = await TestDatabase.CreateAsync();
        var login = $"ddl_{Guid.NewGuid():N}"[..20];
        const string password = "Ddl_Passw0rd!2024";
        await SqlFixture.ExecuteAsync(SqlFixture.ConnectionStringFor("master"), $"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF");
        try
        {
            await db.ExecuteAsync(
                $"CREATE USER [{login}] FOR LOGIN [{login}]",
                $"ALTER ROLE db_ddladmin ADD MEMBER [{login}]",
                $"ALTER ROLE db_datareader ADD MEMBER [{login}]",
                $"GRANT VIEW DEFINITION TO [{login}]");
            var connection = new SqlConnectionStringBuilder(db.ConnectionString) { UserID = login, Password = password }.ConnectionString;
            var options = () => new SchemaSyncOptions { DeploymentConnectionString = connection };

            var first = await db.ApplyShapeAsync(SchemaShape.V1, options());
            var second = await db.ApplyShapeAsync(SchemaShape.V1, options());

            Assert.AreEqual(SchemaSyncOutcome.Applied, first.Outcome, first.Describe());
            Assert.IsFalse(second.ComparisonSkipped, "without INSERT on the state table every start compares");
            Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, second.Outcome);
            Assert.IsTrue(db.Logs.Library.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("could not record the schema state", StringComparison.Ordinal)));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await SqlFixture.ExecuteAsync(SqlFixture.ConnectionStringFor("master"), $"""
                DECLARE @kill nvarchar(max) = N'';
                SELECT @kill += N'KILL ' + CAST(session_id AS nvarchar(10)) + N';' FROM sys.dm_exec_sessions WHERE login_name = N'{login}';
                EXEC (@kill);
                IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{login}') DROP LOGIN [{login}];
                """);
        }
    }
}
