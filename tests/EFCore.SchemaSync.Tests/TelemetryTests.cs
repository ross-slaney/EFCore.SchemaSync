using System.Diagnostics;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.SchemaSync.Tests;

[TestClass]
public sealed class TelemetryTests
{
    [TestMethod]
    public void The_meter_publishes_exactly_the_documented_instruments()
    {
        GC.KeepAlive(SchemaSyncTelemetry.Meter);
        var published = new List<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == SchemaSyncTelemetry.MeterName)
                {
                    published.Add($"{instrument.Name} [{instrument.Unit}]");
                }
            },
        };
        listener.Start();

        CollectionAssert.AreEquivalent(
            new[]
            {
                SchemaSyncTelemetry.ApplyDurationInstrument + " [s]",
                SchemaSyncTelemetry.StageDurationInstrument + " [s]",
                SchemaSyncTelemetry.LockWaitInstrument + " [s]",
                SchemaSyncTelemetry.ApplicationsInstrument + " [{application}]",
                SchemaSyncTelemetry.ChangesInstrument + " [{change}]",
            },
            published);
        Assert.AreEqual(SchemaSyncTelemetry.SourceName, SchemaSyncTelemetry.Source.Name);
    }

    [TestMethod]
    public async Task A_connection_failure_is_traced_as_an_error_and_counted()
    {
        using var capture = new TelemetryCapture();
        var database = $"Unreachable_{Guid.NewGuid():N}"[..30];
        var services = new ServiceCollection();
        services.AddDbContext<DefaultSchemaDbContext>(o => o.UseSqlServer($"Server=127.0.0.1,1;Database={database};User Id=sa;Password=nope;TrustServerCertificate=true;Connect Timeout=2"));
        await using var provider = services.BuildServiceProvider();

        await Assert.ThrowsExactlyAsync<SchemaSyncException>(() => provider.ApplyDatabaseSchemaAsync<DefaultSchemaDbContext>());

        var root = capture.Roots("db.namespace", database).Single();
        Assert.AreEqual(ActivityStatusCode.Error, root.Status);
        Assert.AreEqual(typeof(SchemaSyncException).FullName, root.GetTagItem("error.type"));
        Assert.AreEqual("Connect", root.GetTagItem("efcore.schemasync.stage"));
        Assert.AreEqual("failed", root.GetTagItem("efcore.schemasync.outcome"));
        Assert.IsTrue(root.Events.Any(e => e.Name == "exception"), "the exception is recorded on the activity");
        Assert.IsFalse(root.Tags.Any(t => t.Value?.Contains("nope", StringComparison.Ordinal) == true), "no credentials in tags");

        var connect = capture.Children(root).Single();
        Assert.AreEqual("EFCore.SchemaSync.Connect", connect.OperationName);
        Assert.AreEqual(ActivityStatusCode.Error, connect.Status);

        var applications = capture.For(SchemaSyncTelemetry.ApplicationsInstrument, "db.namespace", database).Single();
        Assert.AreEqual(1, applications.Value);
        Assert.AreEqual("failed", applications.Tags["efcore.schemasync.outcome"]);
        Assert.AreEqual(typeof(SchemaSyncException).FullName, applications.Tags["error.type"]);
        var stage = capture.For(SchemaSyncTelemetry.StageDurationInstrument, "db.namespace", database).Single();
        Assert.AreEqual("Connect", stage.Tags["efcore.schemasync.stage"]);
        Assert.AreEqual("error", stage.Tags["efcore.schemasync.status"]);
    }
}
