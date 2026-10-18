using EFCore.SchemaSync.Conversion;
using EFCore.SchemaSync.Deployment;
using Microsoft.SqlServer.Dac.Model;

namespace EFCore.SchemaSync.Tests;

[TestClass]
public sealed class FastPathTests
{
    private static PreparedSchema Prepared(string script = "CREATE TABLE [T] ([Id] int NOT NULL);", RefactorLog? log = null)
        => new(script, log ?? new RefactorLog(), "Test", new SchemaConversionOptions());

    [TestMethod]
    public void Model_hash_is_stable_for_identical_inputs()
    {
        Assert.AreEqual(ModelHash.Compute(Prepared(), "Latin1_General_CI_AS", SqlServerVersion.Sql160, new SchemaSyncOptions()), ModelHash.Compute(Prepared(), "Latin1_General_CI_AS", SqlServerVersion.Sql160, new SchemaSyncOptions()));
        Assert.AreEqual(64, ModelHash.Compute(Prepared(), null, SqlServerVersion.Sql160, new SchemaSyncOptions()).Length);
    }

    [TestMethod]
    public void Model_hash_changes_with_everything_that_affects_a_deployment()
    {
        var baseline = ModelHash.Compute(Prepared(), "Latin1_General_CI_AS", SqlServerVersion.Sql160, new SchemaSyncOptions());
        var variants = new Dictionary<string, string>
        {
            ["script"] = ModelHash.Compute(Prepared("CREATE TABLE [T] ([Id] bigint NOT NULL);"), "Latin1_General_CI_AS", SqlServerVersion.Sql160, new SchemaSyncOptions()),
            ["refactor log"] = ModelHash.Compute(Prepared(log: new RefactorLog().RenameTable("dbo", "A", "T")), "Latin1_General_CI_AS", SqlServerVersion.Sql160, new SchemaSyncOptions()),
            ["collation"] = ModelHash.Compute(Prepared(), "Latin1_General_CS_AS", SqlServerVersion.Sql160, new SchemaSyncOptions()),
            ["no collation"] = ModelHash.Compute(Prepared(), null, SqlServerVersion.Sql160, new SchemaSyncOptions()),
            ["AllowDataLoss"] = ModelHash.Compute(Prepared(), "Latin1_General_CI_AS", SqlServerVersion.Sql160, new SchemaSyncOptions { AllowDataLoss = true }),
            ["AllowObjectRemoval"] = ModelHash.Compute(Prepared(), "Latin1_General_CI_AS", SqlServerVersion.Sql160, new SchemaSyncOptions { AllowObjectRemoval = true }),
            ["ReportRetainedObjects"] = ModelHash.Compute(Prepared(), "Latin1_General_CI_AS", SqlServerVersion.Sql160, new SchemaSyncOptions { ReportRetainedObjects = false }),
            ["UseTransaction"] = ModelHash.Compute(Prepared(), "Latin1_General_CI_AS", SqlServerVersion.Sql160, new SchemaSyncOptions { UseTransaction = false }),
            ["platform"] = ModelHash.Compute(Prepared(), "Latin1_General_CI_AS", SqlServerVersion.Sql150, new SchemaSyncOptions()),
            ["ManagedSchemas"] = ModelHash.Compute(Prepared(), "Latin1_General_CI_AS", SqlServerVersion.Sql160, ManagedSchemas("billing")),
            ["AllowIncompatiblePlatform"] = ModelHash.Compute(Prepared(), "Latin1_General_CI_AS", SqlServerVersion.Sql160, new SchemaSyncOptions { AllowIncompatiblePlatform = true }),
        };

        foreach (var (input, hash) in variants)
        {
            Assert.AreNotEqual(baseline, hash, $"changing {input} must change the model hash");
        }

        Assert.AreEqual(variants.Count, variants.Values.Distinct().Count(), "every input contributes independently");
    }

    [TestMethod]
    public void Options_that_do_not_change_the_schema_do_not_change_the_hash()
    {
        var baseline = ModelHash.Compute(Prepared(), null, SqlServerVersion.Sql160, new SchemaSyncOptions());

        Assert.AreEqual(baseline, ModelHash.Compute(Prepared(), null, SqlServerVersion.Sql160, new SchemaSyncOptions { LockTimeout = TimeSpan.FromMinutes(9), CommandTimeout = TimeSpan.FromMinutes(9), IncludeDeploymentScript = true }));
        Assert.AreEqual(baseline, ModelHash.Compute(Prepared(), null, SqlServerVersion.Sql160, new SchemaSyncOptions().AfterDeploy((_, _) => Task.CompletedTask)), "hooks run on every start anyway");
    }

    [TestMethod]
    public void Library_version_is_part_of_the_hash()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(ModelHash.LibraryVersion));
    }

    [TestMethod]
    public void Fingerprint_values_are_formatted_culture_invariantly()
    {
        Assert.AreEqual("␀", SchemaFingerprint.Format(DBNull.Value));
        Assert.AreEqual("2026-01-02T03:04:05.0000000", SchemaFingerprint.Format(new DateTime(2026, 1, 2, 3, 4, 5)));
        Assert.AreEqual("0A0B", SchemaFingerprint.Format(new byte[] { 10, 11 }));
        Assert.AreEqual("1.5", SchemaFingerprint.Format(1.5m));
        Assert.AreEqual("abc", SchemaFingerprint.Format("abc"));
    }

    [TestMethod]
    public void The_fingerprint_covers_every_catalog_area_DacFx_compares()
    {
        foreach (var view in new[] { "sys.objects", "sys.columns", "sys.computed_columns", "sys.default_constraints", "sys.check_constraints", "sys.indexes", "sys.index_columns", "sys.foreign_keys", "sys.identity_columns", "sys.sequences", "sys.sql_modules", "sys.extended_properties", "sys.tables", "sys.types", "sys.synonyms", "sys.schemas" })
        {
            StringAssert.Contains(SchemaFingerprint.Sql, view);
        }

        StringAssert.Contains(SchemaFingerprint.Sql, "modify_date", "ALTERs are caught through the modify dates");
    }

    [TestMethod]
    public void State_objects_are_recognized_as_internal()
    {
        Assert.IsTrue(SchemaState.IsInternal("[dbo].[__SchemaSyncState]"));
        Assert.IsTrue(SchemaState.IsInternal("[dbo].[__SchemaSyncState].[ContextKey]"));
        Assert.IsTrue(SchemaState.IsInternal("[dbo].[PK___SchemaSyncState]"));
        Assert.IsFalse(SchemaState.IsInternal("[dbo].[Customers]"));
        Assert.IsFalse(SchemaState.IsInternal("[dbo].[__RefactorLog]"));
    }

    [TestMethod]
    public void The_state_table_is_valid_in_a_dacfx_model()
    {
        var package = Prepared().Build([SchemaState.Statement]);

        Assert.IsTrue(package.Objects.Any(o => o.ObjectType == "Table" && o.Name == SchemaState.QualifiedTableName));
        Assert.IsTrue(package.Objects.Any(o => o.Name == SchemaState.PrimaryKeyName));
    }

    [TestMethod]
    public void The_fast_path_is_on_by_default_and_described_in_results()
    {
        Assert.IsTrue(new SchemaSyncOptions().SkipComparisonWhenUnchanged);
        var skipped = new SchemaSyncResult(SchemaSyncOutcome.NoChangesNeeded, "Ctx", "srv", "db", [], [], [], [], null, TimeSpan.FromMilliseconds(80), comparisonSkipped: true);

        Assert.IsTrue(skipped.ComparisonSkipped);
        StringAssert.StartsWith(skipped.Describe(), "Schema is current (unchanged since the last deployment; comparison skipped)");
    }

    private static SchemaSyncOptions ManagedSchemas(params string[] schemas)
    {
        var options = new SchemaSyncOptions();
        foreach (var schema in schemas)
        {
            options.ManagedSchemas.Add(schema);
        }

        return options;
    }
}
