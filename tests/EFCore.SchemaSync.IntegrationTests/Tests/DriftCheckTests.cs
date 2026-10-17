using EFCore.SchemaSync.HealthChecks;
using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>Verification and health checks: report drift, never change anything.</summary>
[TestClass]
public sealed class DriftCheckTests
{
    private static async Task<SchemaSyncResult> VerifyAsync(TestDatabase db, SchemaShape shape, Action<SchemaSyncOptions>? configure = null, string? connectionString = null)
    {
        await using var provider = db.BuildServices<EvolvingDbContext>(services => services.AddSingleton(shape), connectionString);
        return configure is null
            ? await provider.VerifyDatabaseSchemaAsync<EvolvingDbContext>()
            : await provider.VerifyDatabaseSchemaAsync<EvolvingDbContext>(configure);
    }

    [TestMethod]
    public async Task A_current_database_verifies_from_the_fingerprint_or_in_full()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);

        var fast = await VerifyAsync(db, SchemaShape.V1);
        var full = await VerifyAsync(db, SchemaShape.V1, o => o.SkipComparisonWhenUnchanged = false);

        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, fast.Outcome, fast.Describe());
        Assert.IsTrue(fast.ComparisonSkipped, "the recorded fingerprint answers without DacFx");
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, full.Outcome);
        Assert.IsFalse(full.ComparisonSkipped);
    }

    [TestMethod]
    public async Task Pending_changes_throw_with_the_plan_and_nothing_is_changed()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);
        var recordedAt = await db.ScalarAsync<DateTime>("SELECT AppliedAt FROM dbo.__SchemaSyncState");

        var drift = await Assert.ThrowsExactlyAsync<SchemaDriftException>(() => VerifyAsync(db, SchemaShape.V1 with { Phone = true }));

        Assert.AreEqual(SchemaSyncStage.Compare, drift.Stage);
        Assert.IsTrue(drift.PendingChanges.Any(c => c.Kind == SchemaChangeKind.Alter && c.ObjectName == "[dbo].[Customers]"), drift.Message);
        StringAssert.Contains(drift.Result.DeploymentScript!, "[Phone]");
        Assert.IsFalse(await db.ColumnExistsAsync("dbo.Customers", "Phone"), "verification never deploys");
        Assert.AreEqual(recordedAt, await db.ScalarAsync<DateTime>("SELECT AppliedAt FROM dbo.__SchemaSyncState"), "verification never writes state");
    }

    [TestMethod]
    public async Task Out_of_band_changes_are_drift()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);
        await db.ExecuteAsync("ALTER TABLE dbo.Customers DROP COLUMN Email");

        var drift = await Assert.ThrowsExactlyAsync<SchemaDriftException>(() => VerifyAsync(db, SchemaShape.V1));

        Assert.IsFalse(drift.Result.ComparisonSkipped, "the fingerprint changed, so DacFx compared in full");
        Assert.IsTrue(drift.PendingChanges.Any(c => c.ObjectName == "[dbo].[Customers]"), drift.Message);
    }

    [TestMethod]
    public async Task An_empty_database_is_drift_and_target_only_objects_are_not()
    {
        await using var db = await TestDatabase.CreateAsync();

        var empty = await Assert.ThrowsExactlyAsync<SchemaDriftException>(() => VerifyAsync(db, SchemaShape.V1));
        Assert.IsTrue(empty.PendingChanges.Any(c => c.Kind == SchemaChangeKind.Create && c.ObjectName == "[dbo].[Customers]"));
        Assert.AreEqual(0, await db.TableCountAsync());

        await db.ApplyShapeAsync(SchemaShape.V1);
        await db.ExecuteAsync("CREATE TABLE dbo.DbaOnly (Id int NOT NULL PRIMARY KEY)");
        var tolerated = await VerifyAsync(db, SchemaShape.V1);
        Assert.IsTrue(tolerated.RetainedObjects.Any(r => r.Name == "[dbo].[DbaOnly]"), "reported, but not drift");
    }

    [TestMethod]
    public async Task Verification_never_runs_hooks()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);
        var ran = false;

        var result = await VerifyAsync(db, SchemaShape.V1, o => o.AfterDeploy((_, _) => { ran = true; return Task.CompletedTask; }));

        Assert.IsFalse(ran);
        Assert.AreEqual(0, result.ExecutedHooks.Count);
        Assert.IsFalse(result.Warnings.Any(w => w.Contains("hook", StringComparison.OrdinalIgnoreCase)), "no dry-run hook warning for verification");
    }

    [TestMethod]
    public async Task A_failing_check_is_an_error_not_drift()
    {
        await using var db = await TestDatabase.CreateAsync();

        var ex = await Assert.ThrowsExactlyAsync<SchemaSyncException>(() => VerifyAsync(db, SchemaShape.V1, connectionString: SqlFixture.ConnectionStringFor(db.Name + "_missing")));

        Assert.AreEqual(SchemaSyncStage.Connect, ex.Stage);
    }

    [TestMethod]
    public async Task The_health_check_reports_healthy_drift_and_failure()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);

        async Task<HealthReportEntry> CheckAsync(SchemaShape shape, Action<SchemaSyncHealthCheckOptions>? configure = null, string? connectionString = null)
        {
            await using var provider = db.BuildServices<EvolvingDbContext>(
                services =>
                {
                    services.AddSingleton(shape);
                    services.AddHealthChecks().AddSchemaSyncHealthCheck<EvolvingDbContext>(configure: o =>
                    {
                        o.CacheDuration = TimeSpan.Zero;
                        configure?.Invoke(o);
                    });
                },
                connectionString);
            var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            return report.Entries["efcore-schemasync"];
        }

        var healthy = await CheckAsync(SchemaShape.V1);
        Assert.AreEqual(HealthStatus.Healthy, healthy.Status, healthy.Description);
        Assert.AreEqual(true, healthy.Data["comparisonSkipped"], "a probe normally costs a fingerprint, not a comparison");
        Assert.AreEqual(0, healthy.Data["pendingChanges"]);

        var drifted = await CheckAsync(SchemaShape.V1 with { Phone = true });
        Assert.AreEqual(HealthStatus.Degraded, drifted.Status);
        StringAssert.Contains(drifted.Description, "1 pending schema change(s)");
        Assert.AreEqual(1, drifted.Data["pendingChanges"]);

        var strict = await CheckAsync(SchemaShape.V1 with { Phone = true }, o => o.DriftStatus = HealthStatus.Unhealthy);
        Assert.AreEqual(HealthStatus.Unhealthy, strict.Status);

        var failing = await CheckAsync(SchemaShape.V1, connectionString: SqlFixture.ConnectionStringFor(db.Name + "_missing"));
        Assert.AreEqual(HealthStatus.Unhealthy, failing.Status);
        Assert.IsNotNull(failing.Exception);
        Assert.IsFalse(await db.ColumnExistsAsync("dbo.Customers", "Phone"), "health checks never change the schema");
    }

    [TestMethod]
    public async Task Health_results_are_cached_for_the_configured_duration()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);
        await using var provider = db.BuildServices<EvolvingDbContext>(services =>
        {
            services.AddSingleton(SchemaShape.V1);
            services.AddHealthChecks()
                .AddSchemaSyncHealthCheck<EvolvingDbContext>("cached", o => o.CacheDuration = TimeSpan.FromMinutes(5))
                .AddSchemaSyncHealthCheck<EvolvingDbContext>("uncached", o => o.CacheDuration = TimeSpan.Zero);
        });
        var health = provider.GetRequiredService<HealthCheckService>();

        var before = await health.CheckHealthAsync();
        await db.ExecuteAsync("ALTER TABLE dbo.Customers DROP COLUMN Email");
        var after = await health.CheckHealthAsync();

        Assert.AreEqual(HealthStatus.Healthy, before.Entries["cached"].Status);
        Assert.AreEqual(HealthStatus.Healthy, after.Entries["cached"].Status, "served from the cache");
        Assert.AreEqual(HealthStatus.Degraded, after.Entries["uncached"].Status, "the drift is real");
    }
}
