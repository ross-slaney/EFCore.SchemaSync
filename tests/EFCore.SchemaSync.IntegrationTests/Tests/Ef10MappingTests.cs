using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>EF Core 10 mappings beyond the core matrix: deployed, inspected in the catalog, round-tripped, converged.</summary>
[TestClass]
public sealed class Ef10MappingTests
{
    private static DbContextOptions<T> Options<T>(TestDatabase db, bool compatibility170 = false)
        where T : DbContext
        => new DbContextOptionsBuilder<T>().UseSqlServer(db.ConnectionString, o =>
        {
            if (compatibility170)
            {
                o.UseCompatibilityLevel(170);
            }
        }).Options;

    private static async Task<int> MajorVersionAsync(TestDatabase db) => await db.ScalarAsync<int>("SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'))");

    [TestMethod]
    public async Task Tpt_tpc_many_to_many_table_splitting_complex_types_hilo_and_primitive_collections()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var context = new InheritanceAndRelationshipsContext(Options<InheritanceAndRelationshipsContext>(db));

        var first = await SchemaSync.ApplyAsync(context);
        var second = await SchemaSync.ApplyAsync(context);

        Assert.AreEqual(SchemaSyncOutcome.Applied, first.Outcome, first.Describe());
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, second.Outcome, "every mapping converges: " + second.Describe());

        var tables = await SchemaInspector.TablesAsync(db);
        CollectionAssert.IsSubsetOf(new[] { "dbo.Animals", "dbo.Dog", "dbo.Cat", "dbo.Car", "dbo.Truck", "dbo.Posts", "dbo.Tags", "dbo.PostTag", "dbo.HiLoTickets" }, tables);
        CollectionAssert.DoesNotContain(tables, "dbo.Vehicles", "TPC creates no table for the abstract base");
        Assert.IsNotNull(await SchemaInspector.ForeignKeyAsync(db, "FK_Dog_Animals_Id"), "TPT derived rows reference the base row");
        Assert.AreEqual(2, await db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.sequences WHERE name IN ('VehicleSequence', 'TicketIds')"));
        Assert.AreEqual("decimal", (await SchemaInspector.ColumnAsync(db, "dbo.Posts", "Price_Amount"))!.DataType, "complex type column");
        Assert.IsNotNull(await SchemaInspector.ColumnAsync(db, "dbo.Posts", "Views"), "table-split entity column");
        Assert.AreEqual("nvarchar", (await SchemaInspector.ColumnAsync(db, "dbo.Posts", "Keywords"))!.DataType, "primitive collection as JSON text");

        context.Animals.AddRange(new Dog { Name = "Rex", Breed = "Collie" }, new Cat { Name = "Tom", Indoor = true });
        context.Vehicles.AddRange(new Car { Plate = "AB-12", Seats = 5 }, new Truck { Plate = "TR-99", Payload = 12.5m });
        var tag = new Tag { Label = "ef" };
        context.Posts.Add(new Post { Title = "Hello", Tags = [tag], Keywords = ["schema", "sync"], Stats = new PostStats { Views = 3 }, Price = new Money { Amount = 9.99m, Currency = "EUR" } });
        context.Tickets.Add(new Ticket2 { Subject = "HiLo" });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.AreEqual(1, await context.Animals.OfType<Dog>().CountAsync(d => d.Breed == "Collie"));
        Assert.AreEqual(2, await context.Vehicles.CountAsync());
        var post = await context.Posts.Include(p => p.Tags).Include(p => p.Stats).SingleAsync();
        Assert.AreEqual("ef", post.Tags.Single().Label);
        CollectionAssert.AreEqual(new[] { "schema", "sync" }, post.Keywords);
        Assert.AreEqual(3, post.Stats.Views);
        Assert.AreEqual(9.99m, post.Price.Amount);
        Assert.IsTrue((await context.Tickets.SingleAsync()).Id > 0, "HiLo assigned a key");
    }

    [TestMethod]
    public async Task Json_columns_deploy_as_text_and_converge()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var context = new JsonContext(Options<JsonContext>(db));

        var first = await SchemaSync.ApplyAsync(context);
        var second = await SchemaSync.ApplyAsync(context);

        Assert.AreEqual(SchemaSyncOutcome.Applied, first.Outcome, first.Describe());
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, second.Outcome, second.Describe());
        Assert.AreEqual("nvarchar", (await SchemaInspector.ColumnAsync(db, "dbo.Documents", "Settings"))!.DataType);

        context.Documents.Add(new Document { Title = "Doc", Settings = new Profile { Theme = "dark", Interests = ["sql"] }, Address = new ShippingAddress { Street = "Main", City = "Oslo" } });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var document = await context.Documents.SingleAsync(d => d.Settings.Theme == "dark");
        Assert.AreEqual("Oslo", document.Address.City);
    }

    [TestMethod]
    public async Task Native_json_and_vector_columns_deploy_on_SQL_Server_2025()
    {
        await using var db = await TestDatabase.CreateAsync();
        if (await MajorVersionAsync(db) < 17)
        {
            Assert.Inconclusive("The json and vector types need SQL Server 2025 (CI runs this on the 2025 leg: SCHEMASYNC_SQL_IMAGE_TAG=2025-latest).");
        }

        await using var json = new JsonContext(Options<JsonContext>(db, compatibility170: true));
        await using var vector = new VectorContext(Options<VectorContext>(db, compatibility170: true));

        var jsonFirst = await SchemaSync.ApplyAsync(json);
        var vectorFirst = await SchemaSync.ApplyAsync(vector, new SchemaSyncOptions { ReportRetainedObjects = false });
        var jsonSecond = await SchemaSync.ApplyAsync(json, new SchemaSyncOptions { ReportRetainedObjects = false });
        var vectorSecond = await SchemaSync.ApplyAsync(vector, new SchemaSyncOptions { ReportRetainedObjects = false });

        Assert.AreEqual(SchemaSyncOutcome.Applied, jsonFirst.Outcome, jsonFirst.Describe());
        Assert.AreEqual(SchemaSyncOutcome.Applied, vectorFirst.Outcome, vectorFirst.Describe());
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, jsonSecond.Outcome, jsonSecond.Describe());
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, vectorSecond.Outcome, vectorSecond.Describe());
        Assert.AreEqual("json", (await SchemaInspector.ColumnAsync(db, "dbo.Documents", "Settings"))!.DataType);
        Assert.AreEqual("vector", (await SchemaInspector.ColumnAsync(db, "dbo.Items", "Embedding"))!.DataType);

        vector.Items.Add(new Embedded { Embedding = new SqlVector<float>(new float[] { 1f, 2f, 3f }) });
        await vector.SaveChangesAsync();
        vector.ChangeTracker.Clear();
        CollectionAssert.AreEqual(new[] { 1f, 2f, 3f }, (await vector.Items.SingleAsync()).Embedding.Memory.ToArray());
    }
}
