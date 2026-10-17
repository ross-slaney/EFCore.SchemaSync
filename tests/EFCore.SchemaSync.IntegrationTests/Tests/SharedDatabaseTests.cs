using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>Several owners in one database: each context only reports and removes what it owns.</summary>
[TestClass]
public sealed class SharedDatabaseTests
{
    private static SchemaSyncOptions Scoped(string schema, bool removal = false, bool dataLoss = false)
    {
        var options = new SchemaSyncOptions { AllowObjectRemoval = removal, AllowDataLoss = dataLoss };
        options.ManagedSchemas.Add(schema);
        return options;
    }

    private static async Task<TestDatabase> TwoOwnersAsync()
    {
        var db = await TestDatabase.CreateAsync();
        await db.ApplyAsync<BillingContext>(Scoped("billing"));
        await db.ApplyAsync<ShippingContext>(Scoped("shipping"));
        await db.ExecuteAsync(
            "INSERT INTO billing.Invoices (Number) VALUES (N'INV-1')",
            "INSERT INTO shipping.Shipments (Carrier) VALUES (N'Post')");
        return db;
    }

    [TestMethod]
    public async Task Two_contexts_with_their_own_schemas_never_see_or_drop_each_others_objects()
    {
        await using var db = await TwoOwnersAsync();

        var billing = await db.ApplyAsync<BillingContext>(Scoped("billing"));
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, billing.Outcome, billing.Describe());
        Assert.AreEqual(0, billing.RetainedObjects.Count, "shipping's objects are not billing's business: " + billing.Describe());

        // Removal without AllowDataLoss: shipping's populated table must neither be dropped nor block the deployment.
        var removal = await db.ApplyAsync<BillingContext>(Scoped("billing", removal: true));
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, removal.Outcome, removal.Describe());
        Assert.AreEqual(0, removal.DataLossRisks.Count, "DacFx's alerts about kept tables are filtered out");

        var both = await db.ApplyAsync<BillingContext>(Scoped("billing", removal: true, dataLoss: true));
        Assert.IsFalse(both.Changes.Any(c => c.Kind == SchemaChangeKind.Drop), both.Describe());
        Assert.AreEqual(1, await db.CountAsync("shipping.Shipments"));

        var shipping = await db.ApplyAsync<ShippingContext>(Scoped("shipping", removal: true, dataLoss: true));
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, shipping.Outcome, shipping.Describe());
        Assert.AreEqual(1, await db.CountAsync("billing.Invoices"));
        Assert.IsNotNull(await SchemaInspector.IndexAsync(db, "billing.Invoices", "IX_Invoices_Number"), "billing's index survives shipping's removal run");
    }

    [TestMethod]
    public async Task Without_managed_schemas_the_other_owners_objects_count_as_retained_and_removable()
    {
        await using var db = await TwoOwnersAsync();

        var unscoped = await db.ApplyAsync<BillingContext>();
        var preview = await db.ApplyAsync<BillingContext>(new SchemaSyncOptions { AllowObjectRemoval = true, AllowDataLoss = true, DryRun = true });

        Assert.IsTrue(unscoped.RetainedObjects.Any(r => r.Name == "[shipping].[Shipments]"), unscoped.Describe());
        Assert.IsTrue(preview.Changes.Any(c => c.Kind == SchemaChangeKind.Drop && c.ObjectName == "[shipping].[Shipments]"), "this is what ManagedSchemas prevents: " + preview.Describe());
        Assert.AreEqual(1, await db.CountAsync("shipping.Shipments"), "the dry run changed nothing");
    }

    [TestMethod]
    public async Task Target_only_objects_inside_a_managed_schema_are_still_managed()
    {
        await using var db = await TwoOwnersAsync();
        await db.ExecuteAsync("CREATE TABLE billing.DbaOnly (Id int NOT NULL PRIMARY KEY)", "CREATE INDEX IX_Dba ON billing.Invoices (Number DESC)");

        var kept = await db.ApplyAsync<BillingContext>(Scoped("billing"));
        var removed = await db.ApplyAsync<BillingContext>(Scoped("billing", removal: true));

        var retained = kept.RetainedObjects.Select(r => r.Name).ToList();
        CollectionAssert.Contains(retained, "[billing].[DbaOnly]");
        CollectionAssert.Contains(retained, "[billing].[Invoices].[IX_Dba]");
        Assert.IsFalse(retained.Any(r => r.StartsWith("[shipping]", StringComparison.Ordinal)));
        Assert.IsTrue(removed.Changes.Any(c => c.Kind == SchemaChangeKind.Drop && c.ObjectName == "[billing].[DbaOnly]"), removed.Describe());
        Assert.IsFalse(await db.ObjectExistsAsync("billing.DbaOnly"));
        Assert.IsTrue(await db.ObjectExistsAsync("shipping.Shipments"));
    }

    [TestMethod]
    public async Task The_schemas_the_model_deploys_into_are_managed_even_when_not_listed()
    {
        await using var db = await TwoOwnersAsync();
        await db.ExecuteAsync("CREATE TABLE billing.DbaOnly (Id int NOT NULL PRIMARY KEY)");

        var result = await db.ApplyAsync<BillingContext>(Scoped("reporting"));

        Assert.IsTrue(result.RetainedObjects.Any(r => r.Name == "[billing].[DbaOnly]"), result.Describe());
        Assert.IsFalse(result.RetainedObjects.Any(r => r.Name.StartsWith("[shipping]", StringComparison.Ordinal)), result.Describe());
    }

    [TestMethod]
    public async Task Tables_excluded_from_migrations_are_never_reported_or_dropped()
    {
        await using var db = await TestDatabase.CreateAsync();
        // Another library owns dbo.Memberships (with an index, a default and rows); this context only maps it.
        await db.ExecuteAsync(
            "CREATE TABLE dbo.Memberships (Id int NOT NULL IDENTITY CONSTRAINT PK_Memberships PRIMARY KEY, Role nvarchar(20) NOT NULL CONSTRAINT DF_Memberships_Role DEFAULT N'member')",
            "CREATE INDEX IX_Memberships_Role ON dbo.Memberships (Role)",
            "INSERT INTO dbo.Memberships (Role) VALUES (N'admin'), (DEFAULT)",
            "CREATE TABLE dbo.DbaOnly (Id int NOT NULL PRIMARY KEY)");

        var first = await db.ApplyAsync<ForeignTableContext>();
        var removal = await db.ApplyAsync<ForeignTableContext>(new SchemaSyncOptions { AllowObjectRemoval = true });

        Assert.AreEqual(SchemaSyncOutcome.Applied, first.Outcome, first.Describe());
        Assert.IsFalse(first.Changes.Any(c => c.ObjectName.Contains("Memberships", StringComparison.Ordinal)), "the excluded table is not deployed");
        Assert.IsFalse(first.RetainedObjects.Any(r => r.Name.Contains("Memberships", StringComparison.Ordinal)), first.Describe());
        Assert.IsTrue(first.RetainedObjects.Any(r => r.Name == "[dbo].[DbaOnly]"), "other target-only objects are still reported");

        Assert.IsTrue(removal.Changes.Any(c => c.Kind == SchemaChangeKind.Drop && c.ObjectName == "[dbo].[DbaOnly]"), removal.Describe());
        Assert.IsFalse(removal.Changes.Any(c => c.ObjectName.Contains("Memberships", StringComparison.Ordinal)), removal.Describe());
        Assert.AreEqual(2, await db.CountAsync("dbo.Memberships"), "the populated foreign table survives removal and does not block it");
        Assert.IsNotNull(await SchemaInspector.IndexAsync(db, "dbo.Memberships", "IX_Memberships_Role"), "objects hanging off the foreign table survive");
        Assert.AreEqual("(N'member')", (await SchemaInspector.ColumnAsync(db, "dbo.Memberships", "Role"))!.DefaultDefinition);
        Assert.IsFalse(await db.ObjectExistsAsync("dbo.DbaOnly"));
    }
}
