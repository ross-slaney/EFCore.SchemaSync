using EFCore.SchemaSync.Deployment;
using Microsoft.SqlServer.Dac.Model;
using static EFCore.SchemaSync.Deployment.TargetPlatform;

namespace EFCore.SchemaSync.Tests;

[TestClass]
public sealed class TargetPlatformTests
{
    [TestMethod]
    [DataRow(3, 11, SqlServerVersion.Sql110)]
    [DataRow(3, 12, SqlServerVersion.Sql120)]
    [DataRow(2, 13, SqlServerVersion.Sql130)]
    [DataRow(4, 14, SqlServerVersion.Sql140)]
    [DataRow(3, 15, SqlServerVersion.Sql150)]
    [DataRow(3, 16, SqlServerVersion.Sql160)]
    [DataRow(1, 16, SqlServerVersion.Sql160)]
    [DataRow(3, 17, SqlServerVersion.Sql170)]
    [DataRow(3, 18, SqlServerVersion.Sql170)]
    [DataRow(5, 12, SqlServerVersion.SqlAzure)]
    [DataRow(8, 12, SqlServerVersion.Sql160)]
    [DataRow(9, 15, SqlServerVersion.Sql150)]
    public void Supported_servers_map_to_their_DacFx_platform(int edition, int major, SqlServerVersion expected)
        => Assert.AreEqual(expected, Resolve(new ServerInfo(edition, major, $"{major}.0.1000.1")));

    [TestMethod]
    [DataRow(6, 10, "Synapse")]
    [DataRow(11, 12, "Fabric warehouse")]
    [DataRow(12, 12, "Fabric")]
    [DataRow(42, 16, "engine edition 42")]
    [DataRow(3, 10, "major version 10")]
    public void Unsupported_platforms_are_rejected_with_the_override(int edition, int major, string mention)
    {
        var ex = Assert.ThrowsExactly<SchemaSyncException>(() => Resolve(new ServerInfo(edition, major, "x")));

        Assert.AreEqual(SchemaSyncStage.Connect, ex.Stage);
        StringAssert.Contains(ex.Message, mention);
        StringAssert.Contains(ex.Message, "TargetSqlServerVersion", "the message names the escape hatch");
    }

    [TestMethod]
    public void Servers_are_described_for_logs()
    {
        Assert.AreEqual("Azure SQL Database 12.0.2000.8", Describe(new ServerInfo(5, 12, "12.0.2000.8")));
        Assert.AreEqual("SQL Server 16.0.4295.3", Describe(new ServerInfo(3, 16, "16.0.4295.3")));
        Assert.AreEqual("Azure SQL Managed Instance 12.0.2000.8", Describe(new ServerInfo(8, 12, "12.0.2000.8")));
    }

    [TestMethod]
    public void The_target_platform_is_detected_unless_pinned()
    {
        Assert.IsNull(new SchemaSyncOptions().TargetSqlServerVersion);
    }

    [TestMethod]
    public void The_normalizer_parses_SQL_Server_2025_types()
    {
        var normalized = Conversion.SqlServerScriptNormalizer.Normalize("""
            CREATE TABLE [Docs] (
                [Id] int NOT NULL,
                [Body] json NULL,
                [Embedding] vector(3) NULL,
                CONSTRAINT [PK_Docs] PRIMARY KEY ([Id])
            );
            GO
            """);

        StringAssert.Contains(normalized.Statements.Single().Text, "[Embedding] vector(3) NULL");
    }
}
