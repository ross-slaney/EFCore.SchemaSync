using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.SqlServer.Dac.Model;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>The DACPAC targets the engine the suite runs against (2022 locally by default, 2022 and 2025 in CI).</summary>
[TestClass]
public sealed class PlatformTests
{
    private static async Task<int> MajorVersionAsync(TestDatabase db) => await db.ScalarAsync<int>("SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'))");

    [TestMethod]
    public async Task The_target_platform_is_detected_from_the_server()
    {
        await using var db = await TestDatabase.CreateAsync();
        var major = await MajorVersionAsync(db);

        var result = await db.ApplyShapeAsync(SchemaShape.V1);

        var expected = major switch
        {
            16 => SqlServerVersion.Sql160,
            >= 17 => SqlServerVersion.Sql170,
            _ => throw new AssertInconclusiveException($"Unexpected engine version {major} for this suite."),
        };
        Assert.AreEqual(expected, result.TargetPlatform);
        Assert.IsTrue(db.Logs.Library.Any(e => e.Message.Contains($"targeting {expected}", StringComparison.Ordinal)));
        var tag = Environment.GetEnvironmentVariable("SCHEMASYNC_SQL_IMAGE_TAG");
        if (tag is { Length: > 0 })
        {
            Assert.AreEqual(tag.StartsWith("2025", StringComparison.Ordinal) ? 17 : 16, major, $"the suite runs against the requested image {tag}");
        }
    }

    [TestMethod]
    public async Task A_pinned_older_platform_deploys_to_a_newer_engine()
    {
        await using var db = await TestDatabase.CreateAsync();

        var result = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { TargetSqlServerVersion = SqlServerVersion.Sql150 });
        var again = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { TargetSqlServerVersion = SqlServerVersion.Sql150 });

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.AreEqual(SqlServerVersion.Sql150, result.TargetPlatform);
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, again.Outcome, again.Describe());
        Assert.IsFalse(db.Logs.Library.Any(e => e.Message.Contains("targeting", StringComparison.Ordinal)), "no detection when pinned");
    }

    [TestMethod]
    public async Task A_pinned_platform_newer_than_the_engine_needs_AllowIncompatiblePlatform()
    {
        await using var db = await TestDatabase.CreateAsync();
        if (await MajorVersionAsync(db) >= 17)
        {
            Assert.Inconclusive("Needs an engine older than the newest DacFx platform.");
        }

        var refused = await Assert.ThrowsAsync<SchemaSyncException>(() => db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { TargetSqlServerVersion = SqlServerVersion.Sql170 }));
        Assert.IsTrue(refused.Stage is SchemaSyncStage.Compare or SchemaSyncStage.Deploy, refused.Message);
        Assert.AreEqual(0, await db.TableCountAsync());

        var allowed = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { TargetSqlServerVersion = SqlServerVersion.Sql170, AllowIncompatiblePlatform = true });
        Assert.AreEqual(SchemaSyncOutcome.Applied, allowed.Outcome, allowed.Describe());
    }
}
