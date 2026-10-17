using EFCore.SchemaSync.Conversion;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.Dac.Model;

namespace EFCore.SchemaSync.Tests;

[TestClass]
public sealed class Ef10MappingConversionTests
{
    private static DbContextOptions<T> Options<T>(bool compatibility170 = false)
        where T : DbContext
        => new DbContextOptionsBuilder<T>().UseSqlServer(TestContextFactory.OfflineConnectionString, o =>
        {
            if (compatibility170)
            {
                o.UseCompatibilityLevel(170);
            }
        }).Options;

    [TestMethod]
    public void Inheritance_many_to_many_table_splitting_complex_types_and_hilo_convert()
    {
        using var context = new InheritanceAndRelationshipsContext(Options<InheritanceAndRelationshipsContext>());

        var package = SchemaConverter.Convert(context);

        var objects = package.Objects.Select(o => $"{o.ObjectType} {o.Name}").ToList();
        CollectionAssert.Contains(objects, "Table [dbo].[Animals]", "TPT base table");
        CollectionAssert.Contains(objects, "Table [dbo].[Dog]", "TPT derived table");
        CollectionAssert.Contains(objects, "Table [dbo].[Cat]", "TPT derived table");
        CollectionAssert.Contains(objects, "ForeignKeyConstraint [dbo].[FK_Dog_Animals_Id]", "TPT derived rows reference the base row");
        CollectionAssert.Contains(objects, "Table [dbo].[Car]", "TPC concrete table");
        CollectionAssert.Contains(objects, "Table [dbo].[Truck]", "TPC concrete table");
        CollectionAssert.DoesNotContain(objects, "Table [dbo].[Vehicles]", "TPC has no table for the abstract base");
        CollectionAssert.Contains(objects, "Sequence [dbo].[VehicleSequence]", "TPC keys come from a shared sequence");
        CollectionAssert.Contains(objects, "Table [dbo].[PostTag]", "many-to-many join table");
        CollectionAssert.Contains(objects, "Sequence [dbo].[TicketIds]", "HiLo sequence");
        Assert.AreEqual(1, objects.Count(o => o == "Table [dbo].[Posts]"), "table splitting maps two entity types to one table");
        StringAssert.Contains(package.NormalizedScript, "[Views] int NOT NULL", "the split entity's columns live in Posts");
        StringAssert.Contains(package.NormalizedScript, "[Price_Amount] decimal(18,2) NOT NULL", "complex type columns");
        StringAssert.Contains(package.NormalizedScript, "[Price_Currency] nchar(3) NOT NULL");
        StringAssert.Contains(package.NormalizedScript, "[Keywords] nvarchar(max) NOT NULL", "primitive collections are JSON text");
    }

    [TestMethod]
    public void Json_columns_convert_as_nvarchar_max_below_compatibility_level_170()
    {
        using var context = new JsonContext(Options<JsonContext>());

        var script = SchemaConverter.Convert(context).NormalizedScript;

        StringAssert.Contains(script, "[Settings] nvarchar(max)");
        StringAssert.Contains(script, "[Address] nvarchar(max)");
    }

    [TestMethod]
    public void Native_json_and_vector_convert_for_SQL_Server_2025()
    {
        using var json = new JsonContext(Options<JsonContext>(compatibility170: true));
        using var vector = new VectorContext(Options<VectorContext>(compatibility170: true));
        var options = new SchemaConversionOptions { TargetSqlServerVersion = SqlServerVersion.Sql170 };

        var jsonScript = SchemaConverter.Convert(json, options).NormalizedScript;
        var vectorScript = SchemaConverter.Convert(vector, options).NormalizedScript;

        StringAssert.Contains(jsonScript, "[Settings] json");
        StringAssert.Contains(jsonScript, "[Address] json");
        StringAssert.Contains(vectorScript, "[Embedding] vector(3) NOT NULL");
    }
}
