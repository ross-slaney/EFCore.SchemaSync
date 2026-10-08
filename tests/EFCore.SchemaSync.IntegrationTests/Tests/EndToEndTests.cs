using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.EntityFrameworkCore;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>The complete path first: create, populate, evolve through the public API, keep the rows, and converge.</summary>
[TestClass]
public sealed class EndToEndTests
{
    [TestMethod]
    public async Task Adding_a_nullable_column_to_a_populated_table_preserves_rows_and_a_second_apply_is_a_noop()
    {
        await using var db = await TestDatabase.CreateAsync();

        var created = await db.ApplyShapeAsync(SchemaShape.V1);
        Assert.AreEqual(SchemaSyncOutcome.Applied, created.Outcome, created.Describe());
        Assert.IsTrue(created.Changes.Any(c => c.Kind == SchemaChangeKind.Create && c.ObjectType == "Table" && c.ObjectName == "[dbo].[Customers]"), created.Describe());

        await using (var context = db.OpenShape(SchemaShape.V1))
        {
            context.Customers.AddRange(
                new EvolvingCustomer { Name = "Ada", Email = "ada@example.test" },
                new EvolvingCustomer { Name = "Grace" },
                new EvolvingCustomer { Name = "Linus" });
            await context.SaveChangesAsync();
        }

        var v2 = SchemaShape.V1 with { Phone = true };
        var upgraded = await db.ApplyShapeAsync(v2);
        Assert.AreEqual(SchemaSyncOutcome.Applied, upgraded.Outcome, upgraded.Describe());
        Assert.AreEqual(1, upgraded.Changes.Count, upgraded.Describe());
        Assert.AreEqual(new SchemaChange(SchemaChangeKind.Alter, "Table", "[dbo].[Customers]", "Alter"), upgraded.Changes[0]);
        Assert.AreEqual(0, upgraded.DataLossRisks.Count);
        Assert.AreEqual(0, upgraded.RetainedObjects.Count);
        Assert.IsNull(upgraded.DeploymentScript);

        var phone = await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Phone");
        Assert.IsNotNull(phone);
        Assert.AreEqual("nvarchar", phone.DataType);
        Assert.AreEqual(30, phone.Length);
        Assert.IsTrue(phone.IsNullable);

        await using (var context = db.OpenShape(v2))
        {
            var customers = await context.Customers.OrderBy(c => c.Id).ToListAsync();
            CollectionAssert.AreEqual(new[] { "Ada", "Grace", "Linus" }, customers.Select(c => c.Name).ToArray());
            Assert.IsTrue(customers.All(c => c.Phone is null));
            Assert.AreEqual("ada@example.test", customers[0].Email);
            customers[0].Phone = "+1 555 0100";
            await context.SaveChangesAsync();
        }

        var again = await db.ApplyShapeAsync(v2);
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, again.Outcome, again.Describe());
        Assert.AreEqual(0, again.Changes.Count);
        Assert.IsFalse(again.HasChanges);
        Assert.AreEqual(3, await db.CountAsync("dbo.Customers"));
        Assert.AreEqual("+1 555 0100", await db.ScalarAsync<string>("SELECT Phone FROM dbo.Customers WHERE Name = N'Ada'"));
    }

    [TestMethod]
    public async Task The_result_identifies_context_server_database_and_duration()
    {
        await using var db = await TestDatabase.CreateAsync();

        var result = await db.ApplyShapeAsync(SchemaShape.V1);

        Assert.AreEqual(nameof(EvolvingDbContext), result.ContextName);
        Assert.AreEqual(db.Name, result.DatabaseName);
        Assert.AreEqual(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(db.ConnectionString).DataSource, result.ServerName);
        Assert.IsTrue(result.Duration > TimeSpan.Zero);
        StringAssert.Contains(result.Describe(), "Applied");
        StringAssert.Contains(result.Describe(), "Create Table [dbo].[Customers]");
    }
}
