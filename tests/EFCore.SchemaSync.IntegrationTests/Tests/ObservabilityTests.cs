using System.Diagnostics;
using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests;
using EFCore.SchemaSync.Tests.Models;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>Traces and metrics of real applications against SQL Server.</summary>
[TestClass]
public sealed class ObservabilityTests
{
    private static string[] Names(IEnumerable<Activity> activities) => activities.Select(a => a.OperationName.Replace("EFCore.SchemaSync.", string.Empty, StringComparison.Ordinal)).ToArray();

    [TestMethod]
    public async Task An_application_is_one_activity_with_a_child_per_stage_and_matching_metrics()
    {
        using var capture = new TelemetryCapture();
        await using var db = await TestDatabase.CreateAsync();

        var result = await db.ApplyShapeAsync(SchemaShape.V1);

        var root = capture.Roots("db.namespace", db.Name).Single();
        Assert.AreEqual(ActivityStatusCode.Ok, root.Status);
        Assert.AreEqual("Applied", root.GetTagItem("efcore.schemasync.outcome"));
        Assert.AreEqual(nameof(EvolvingDbContext), root.GetTagItem("efcore.schemasync.context"));
        Assert.AreEqual(false, root.GetTagItem("efcore.schemasync.dry_run"));
        Assert.AreEqual("microsoft.sql_server", root.GetTagItem("db.system.name"));
        Assert.AreEqual(result.ServerName, root.GetTagItem("server.address"));
        Assert.AreEqual(result.Changes.Count, root.GetTagItem("efcore.schemasync.changes"));
        CollectionAssert.AreEqual(new[] { "Connect", "Convert", "AcquireLock", "Compare", "Deploy" }, Names(capture.Children(root)));
        Assert.IsTrue(capture.Children(root).All(c => c.Status == ActivityStatusCode.Ok));

        var duration = capture.For(SchemaSyncTelemetry.ApplyDurationInstrument, "db.namespace", db.Name).Single();
        Assert.AreEqual("Applied", duration.Tags["efcore.schemasync.outcome"]);
        Assert.IsTrue(duration.Value > 0);
        CollectionAssert.AreEquivalent(
            new[] { "Connect", "Convert", "AcquireLock", "Compare", "Deploy" },
            capture.For(SchemaSyncTelemetry.StageDurationInstrument, "db.namespace", db.Name).Select(m => (string)m.Tags["efcore.schemasync.stage"]!).ToArray());
        Assert.AreEqual(1, capture.For(SchemaSyncTelemetry.LockWaitInstrument, "db.namespace", db.Name).Count);
        Assert.AreEqual(1, capture.For(SchemaSyncTelemetry.ApplicationsInstrument, "db.namespace", db.Name).Single().Value);
        var changes = capture.For(SchemaSyncTelemetry.ChangesInstrument, "db.namespace", db.Name);
        Assert.AreEqual(result.Changes.Count, (int)changes.Sum(m => m.Value));
        Assert.IsTrue(changes.All(m => m.Tags["efcore.schemasync.change.kind"] is "Create" or "Alter" or "Drop" or "TableRebuild" or "Other"));
    }

    [TestMethod]
    public async Task An_unchanged_application_has_no_deploy_stage()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);
        using var capture = new TelemetryCapture();

        await db.ApplyShapeAsync(SchemaShape.V1);

        var root = capture.Roots("db.namespace", db.Name).Single();
        Assert.AreEqual("NoChangesNeeded", root.GetTagItem("efcore.schemasync.outcome"));
        Assert.AreEqual(true, root.GetTagItem("efcore.schemasync.comparison_skipped"));
        CollectionAssert.AreEqual(new[] { "Connect", "Convert", "AcquireLock", "Compare" }, Names(capture.Children(root)));
        Assert.AreEqual(0, capture.For(SchemaSyncTelemetry.ChangesInstrument, "db.namespace", db.Name).Count);
    }

    [TestMethod]
    public async Task Hooks_get_their_own_activities()
    {
        using var capture = new TelemetryCapture();
        await using var db = await TestDatabase.CreateAsync();

        await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions()
            .BeforeDeploy("check", (_, _) => Task.CompletedTask)
            .AfterDeploy("seed", (_, _) => Task.CompletedTask));

        var hooks = capture.Children(capture.Roots("db.namespace", db.Name).Single()).Where(a => a.OperationName == "EFCore.SchemaSync.Hook").ToList();
        CollectionAssert.AreEqual(new[] { "check", "seed" }, hooks.Select(h => (string?)h.GetTagItem("efcore.schemasync.hook")).ToArray());
        CollectionAssert.AreEqual(new[] { "BeforeDeploy", "AfterDeploy" }, hooks.Select(h => (string?)h.GetTagItem("efcore.schemasync.hook.phase")).ToArray());
    }

    [TestMethod]
    public async Task A_blocked_application_records_the_error_on_the_failing_stage()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1 with { Phone = true });
        await db.ExecuteAsync("INSERT INTO dbo.Customers (Name, Phone) VALUES (N'Ada', N'555-0100')");
        using var capture = new TelemetryCapture();

        await Assert.ThrowsExactlyAsync<SchemaChangesBlockedException>(() => db.ApplyShapeAsync(SchemaShape.V1));

        var root = capture.Roots("db.namespace", db.Name).Single();
        Assert.AreEqual(ActivityStatusCode.Error, root.Status);
        Assert.AreEqual(typeof(SchemaChangesBlockedException).FullName, root.GetTagItem("error.type"));
        Assert.AreEqual("Compare", root.GetTagItem("efcore.schemasync.stage"));
        var compare = capture.Children(root).Single(c => c.OperationName == "EFCore.SchemaSync.Compare");
        Assert.AreEqual(ActivityStatusCode.Error, compare.Status);
        Assert.IsFalse(Names(capture.Children(root)).Contains("Deploy"));
        var counted = capture.For(SchemaSyncTelemetry.ApplicationsInstrument, "db.namespace", db.Name).Single();
        Assert.AreEqual("failed", counted.Tags["efcore.schemasync.outcome"]);
    }

    [TestMethod]
    public async Task Dry_runs_are_tagged_and_count_planned_changes()
    {
        using var capture = new TelemetryCapture();
        await using var db = await TestDatabase.CreateAsync();

        var preview = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { DryRun = true });

        var root = capture.Roots("db.namespace", db.Name).Single();
        Assert.AreEqual(true, root.GetTagItem("efcore.schemasync.dry_run"));
        Assert.AreEqual("Previewed", root.GetTagItem("efcore.schemasync.outcome"));
        Assert.IsFalse(Names(capture.Children(root)).Contains("Deploy"));
        var changes = capture.For(SchemaSyncTelemetry.ChangesInstrument, "db.namespace", db.Name);
        Assert.AreEqual(preview.Changes.Count, (int)changes.Sum(m => m.Value));
        Assert.IsTrue(changes.All(m => Equals(m.Tags["efcore.schemasync.dry_run"], true)));
    }
}
