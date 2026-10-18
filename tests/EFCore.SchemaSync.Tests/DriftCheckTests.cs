using EFCore.SchemaSync.HealthChecks;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EFCore.SchemaSync.Tests;

[TestClass]
public sealed class DriftCheckTests
{
    [TestMethod]
    public void Drift_exception_lists_the_pending_changes()
    {
        var result = new SchemaSyncResult(SchemaSyncOutcome.Previewed, "AppDbContext", "srv", "app",
            [new SchemaChange(SchemaChangeKind.Alter, "Table", "[dbo].[Customers]", "Alter")], [], [], [], "script", TimeSpan.FromSeconds(1));

        var ex = new SchemaDriftException(result);

        Assert.AreEqual(SchemaSyncStage.Compare, ex.Stage);
        Assert.AreSame(result, ex.Result);
        Assert.AreEqual(1, ex.PendingChanges.Count);
        StringAssert.Contains(ex.Message, "srv/app does not match the model of AppDbContext: 1 pending change(s)");
        StringAssert.Contains(ex.Message, "Alter Table [dbo].[Customers]");
        Assert.IsInstanceOfType<SchemaSyncException>(ex, "callers catching SchemaSyncException still see drift");
    }

    [TestMethod]
    public void Verification_is_a_read_only_dry_run_that_keeps_the_callers_options()
    {
        var options = SchemaSync.VerificationOptions(o =>
        {
            o.AllowDataLoss = true;
            o.DryRun = false;
            o.AfterDeploy((_, _) => Task.CompletedTask);
        });

        Assert.IsTrue(options.DryRun, "verification can never deploy, whatever the configuration says");
        Assert.IsTrue(options.VerifyOnly);
        Assert.IsTrue(options.AllowDataLoss, "the caller's options are kept so the model hash matches the application's");
        Assert.AreEqual(1, options.Hooks.Count, "hooks are registered but dry runs never run them");
    }

    [TestMethod]
    public void The_health_check_is_registered_with_its_name_status_and_tags()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddSchemaSyncHealthCheck<MatrixDbContext>("schema", o => o.DriftStatus = HealthStatus.Unhealthy, HealthStatus.Degraded, ["ready"]);
        using var provider = services.BuildServiceProvider();

        var registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.Single();

        Assert.AreEqual("schema", registration.Name);
        Assert.AreEqual(HealthStatus.Degraded, registration.FailureStatus);
        CollectionAssert.AreEqual(new[] { "ready" }, registration.Tags.ToArray());
        Assert.IsInstanceOfType<SchemaSyncHealthCheck<MatrixDbContext>>(registration.Factory(provider));
    }

    [TestMethod]
    public async Task An_unreachable_database_makes_the_health_check_report_the_failure_status()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DefaultSchemaDbContext>(o => o.UseSqlServer("Server=127.0.0.1,1;Database=Nope;User Id=sa;Password=nope;TrustServerCertificate=true;Connect Timeout=2"));
        services.AddHealthChecks().AddSchemaSyncHealthCheck<DefaultSchemaDbContext>();
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        var entry = report.Entries["efcore-schemasync"];
        Assert.AreEqual(HealthStatus.Unhealthy, entry.Status);
        StringAssert.Contains(entry.Description, "stage Connect");
        Assert.IsInstanceOfType<SchemaSyncException>(entry.Exception);
        Assert.IsFalse(entry.Description!.Contains("nope", StringComparison.Ordinal), "credentials never leak into health output");
    }

    [TestMethod]
    public void Health_check_defaults_are_conservative()
    {
        var options = new SchemaSyncHealthCheckOptions();

        Assert.AreEqual(HealthStatus.Degraded, options.DriftStatus, "drift should not make orchestrators kill healthy instances by default");
        Assert.AreEqual(TimeSpan.FromMinutes(1), options.CacheDuration);
        Assert.IsNull(options.Configure);
    }
}
