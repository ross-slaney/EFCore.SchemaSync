using System.Collections.Concurrent;
using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>BeforeDeploy/AfterDeploy hooks: C# instead of SSDT pre- and post-deployment scripts.</summary>
[TestClass]
public sealed class HookTests
{
    private static async Task SeedSystemCustomerAsync(SchemaSyncHookContext hook, CancellationToken ct)
    {
        var db = hook.GetDbContext<EvolvingDbContext>();
        if (!await db.Customers.AnyAsync(c => c.Name == "System", ct))
        {
            db.Customers.Add(new EvolvingCustomer { Name = "System" });
            await db.SaveChangesAsync(ct);
        }
    }

    [TestMethod]
    public async Task AfterDeploy_seeds_with_the_DbContext_on_every_start_in_registration_order()
    {
        await using var db = await TestDatabase.CreateAsync();
        var observed = new List<string>();

        SchemaSyncOptions Options() => new SchemaSyncOptions()
            .BeforeDeploy("check", (hook, _) =>
            {
                observed.Add($"{hook.Phase} changes={hook.HasChanges} result={hook.Result is not null}");
                return Task.CompletedTask;
            })
            .AfterDeploy("seed system customer", SeedSystemCustomerAsync)
            .AfterDeploy("inspect", (hook, _) =>
            {
                var sameScope = ReferenceEquals(hook.Services!.GetRequiredService<EvolvingDbContext>(), hook.DbContext);
                observed.Add($"{hook.Phase} changes={hook.HasChanges} outcome={hook.Result!.Outcome} sameScope={sameScope}");
                return Task.CompletedTask;
            });

        var first = await db.ApplyShapeAsync(SchemaShape.V1, Options());

        Assert.AreEqual(SchemaSyncOutcome.Applied, first.Outcome, first.Describe());
        CollectionAssert.AreEqual(new[] { "check", "seed system customer", "inspect" }, first.ExecutedHooks.ToArray(), "before hooks first, then after hooks, each in registration order");
        Assert.AreEqual(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customers WHERE Name = N'System'"));
        StringAssert.Contains(first.Describe(), "Hook: seed system customer");

        var second = await db.ApplyShapeAsync(SchemaShape.V1, Options());

        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, second.Outcome, second.Describe());
        Assert.AreEqual(3, second.ExecutedHooks.Count, "hooks also run when the schema is current");
        Assert.AreEqual(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customers WHERE Name = N'System'"), "the idempotent seed did not duplicate");
        CollectionAssert.AreEqual(
            new[]
            {
                "BeforeDeploy changes=True result=False",
                "AfterDeploy changes=True outcome=Applied sameScope=True",
                "BeforeDeploy changes=False result=False",
                "AfterDeploy changes=False outcome=NoChangesNeeded sameScope=True",
            },
            observed);
        Assert.IsTrue(db.Logs.Library.Any(e => e.Message.Contains("AfterDeploy hook 'seed system customer' completed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task BeforeDeploy_sees_the_old_schema_and_can_fix_data_so_a_change_succeeds()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);
        var longName = new string('x', 80);
        await using (var context = db.OpenShape(SchemaShape.V1))
        {
            context.Customers.AddRange(new EvolvingCustomer { Name = longName }, new EvolvingCustomer { Name = "Ada" });
            await context.SaveChangesAsync();
        }

        var narrower = SchemaShape.V1 with { NameLength = 50 };

        // Without the hook SQL Server refuses to shrink the column below the data: the deployment fails and rolls back.
        var failed = await Assert.ThrowsExactlyAsync<SchemaSyncException>(() => db.ApplyShapeAsync(narrower, new SchemaSyncOptions { AllowDataLoss = true }));
        Assert.AreEqual(SchemaSyncStage.Deploy, failed.Stage, failed.Message);
        Assert.AreEqual(100, (await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Name"))!.Length);

        // With a BeforeDeploy hook the data is fixed in C# first, then the change applies.
        int? lengthSeenByHook = null;
        var options = new SchemaSyncOptions { AllowDataLoss = true }.BeforeDeploy("truncate long names", async (hook, ct) =>
        {
            Assert.IsTrue(hook.Changes.Any(c => c.Kind == SchemaChangeKind.Alter && c.ObjectName == "[dbo].[Customers]"), "the hook sees the planned changes");
            lengthSeenByHook = (await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Name"))!.Length;
            await hook.GetDbContext<EvolvingDbContext>().Customers
                .Where(c => c.Name.Length > 50)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, c => c.Name.Substring(0, 50)), ct);
        });

        var result = await db.ApplyShapeAsync(narrower, options);

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.AreEqual(100, lengthSeenByHook, "BeforeDeploy runs against the old schema");
        Assert.AreEqual(50, (await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Name"))!.Length);
        Assert.AreEqual(new string('x', 50), await db.ScalarAsync<string>("SELECT Name FROM dbo.Customers WHERE Name LIKE N'x%'"));
        Assert.AreEqual(2, await db.CountAsync("dbo.Customers"));
    }

    [TestMethod]
    public async Task Hooks_do_not_run_on_a_dry_run()
    {
        await using var db = await TestDatabase.CreateAsync();
        var options = new SchemaSyncOptions { DryRun = true }
            .BeforeDeploy((_, _) => throw new InvalidOperationException("must not run"))
            .AfterDeploy((_, _) => throw new InvalidOperationException("must not run"));

        var preview = await db.ApplyShapeAsync(SchemaShape.V1, options);

        Assert.AreEqual(SchemaSyncOutcome.Previewed, preview.Outcome, preview.Describe());
        Assert.AreEqual(0, preview.ExecutedHooks.Count);
        Assert.IsTrue(preview.Warnings.Any(w => w.Contains("2 hook(s) were not run", StringComparison.Ordinal)), string.Join(" | ", preview.Warnings));
        Assert.AreEqual(0, await db.TableCountAsync());
    }

    [TestMethod]
    public async Task A_blocked_deployment_runs_no_hooks()
    {
        await using var db = await TestDatabase.CreateAsync();
        var withPhone = SchemaShape.V1 with { Phone = true };
        await db.ApplyShapeAsync(withPhone);
        await db.ExecuteAsync("INSERT INTO dbo.Customers (Name, Phone) VALUES (N'Ada', N'555-0100')");
        var ran = false;
        var options = new SchemaSyncOptions().BeforeDeploy((_, _) => { ran = true; return Task.CompletedTask; });

        await Assert.ThrowsExactlyAsync<SchemaChangesBlockedException>(() => db.ApplyShapeAsync(SchemaShape.V1, options));

        Assert.IsFalse(ran, "nothing runs when the deployment is rejected");
    }

    [TestMethod]
    public async Task A_failing_BeforeDeploy_hook_stops_before_any_schema_change()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(SchemaShape.V1);
        var afterRan = false;
        var options = new SchemaSyncOptions()
            .BeforeDeploy("broken", (_, _) => throw new InvalidOperationException("boom"))
            .AfterDeploy((_, _) => { afterRan = true; return Task.CompletedTask; });

        var ex = await Assert.ThrowsExactlyAsync<SchemaSyncException>(() => db.ApplyShapeAsync(SchemaShape.V1 with { Phone = true }, options));

        Assert.AreEqual(SchemaSyncStage.BeforeDeployHook, ex.Stage);
        Assert.IsInstanceOfType<InvalidOperationException>(ex.InnerException);
        StringAssert.Contains(ex.Message, "'broken'");
        StringAssert.Contains(ex.Message, "No schema change was executed");
        Assert.IsFalse(await db.ColumnExistsAsync("dbo.Customers", "Phone"));
        Assert.IsFalse(afterRan);
    }

    [TestMethod]
    public async Task A_failing_AfterDeploy_hook_reports_that_the_schema_was_already_applied()
    {
        await using var db = await TestDatabase.CreateAsync();
        var laterRan = false;
        var options = new SchemaSyncOptions()
            .AfterDeploy("broken seed", (_, _) => throw new InvalidOperationException("boom"))
            .AfterDeploy("later", (_, _) => { laterRan = true; return Task.CompletedTask; });

        var ex = await Assert.ThrowsExactlyAsync<SchemaSyncException>(() => db.ApplyShapeAsync(SchemaShape.V1, options));

        Assert.AreEqual(SchemaSyncStage.AfterDeployHook, ex.Stage);
        Assert.IsInstanceOfType<InvalidOperationException>(ex.InnerException);
        StringAssert.Contains(ex.Message, "already applied");
        Assert.IsTrue(await db.ObjectExistsAsync("dbo.Customers"), "the schema stays applied");
        Assert.IsFalse(laterRan, "later hooks do not run");
    }

    [TestMethod]
    public async Task Cancellation_inside_a_hook_propagates()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var cts = new CancellationTokenSource();
        var options = new SchemaSyncOptions().AfterDeploy(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => db.ApplyShapeAsync(SchemaShape.V1, options, cts.Token));
    }

    [TestMethod]
    public async Task Hooks_of_concurrent_instances_never_overlap()
    {
        await using var db = await TestDatabase.CreateAsync();
        var intervals = new ConcurrentBag<(DateTime Start, DateTime End)>();

        SchemaSyncOptions Options() => new SchemaSyncOptions().AfterDeploy(async (_, ct) =>
        {
            var start = DateTime.UtcNow;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            intervals.Add((start, DateTime.UtcNow));
        });

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => db.ApplyShapeAsync(SchemaShape.V1, Options())));

        var ordered = intervals.OrderBy(i => i.Start).ToList();
        Assert.AreEqual(3, ordered.Count);
        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.IsTrue(ordered[i].Start >= ordered[i - 1].End, "hooks run under the schema lock, one instance at a time");
        }
    }
}
