using System.Xml.Linq;
using EFCore.SchemaSync.Conversion;
using EFCore.SchemaSync.Deployment;
using EFCore.SchemaSync.Tests.Models;

namespace EFCore.SchemaSync.Tests;

[TestClass]
public sealed class RefactorLogTests
{
    [TestMethod]
    public void Serializes_in_the_dacfx_refactorlog_format()
    {
        var log = new RefactorLog()
            .RenameColumn("dbo", "Customer", "Phone", "PhoneNumber")
            .RenameTable("dbo", "Customer", "Customers")
            .MoveToSchema("dbo", "Customers", "crm")
            .RenameIndex("crm", "Customers", "IX_Customer_Phone", "IX_Customers_PhoneNumber")
            .RenameConstraint("crm", "Customers", RefactorConstraintKind.PrimaryKey, "PK_Customer", "PK_Customers");

        var xml = log.ToXml();

        StringAssert.StartsWith(xml, "<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        var document = XDocument.Parse(xml);
        XNamespace ns = RefactorLog.Namespace;
        Assert.AreEqual(ns + "Operations", document.Root!.Name);
        Assert.AreEqual("1.0", document.Root.Attribute("Version")?.Value);
        var operations = document.Root.Elements(ns + "Operation").ToList();
        Assert.AreEqual(5, operations.Count);

        var column = operations[0];
        Assert.AreEqual("Rename Refactor", column.Attribute("Name")?.Value);
        Assert.IsTrue(Guid.TryParse(column.Attribute("Key")?.Value, out _));
        Assert.AreEqual("01/01/2000 00:00:00", column.Attribute("ChangeDateTime")?.Value);
        var properties = column.Elements(ns + "Property").ToDictionary(p => p.Attribute("Name")!.Value, p => p.Attribute("Value")!.Value);
        Assert.AreEqual("[dbo].[Customer].[Phone]", properties["ElementName"]);
        Assert.AreEqual("SqlSimpleColumn", properties["ElementType"]);
        Assert.AreEqual("[dbo].[Customer]", properties["ParentElementName"]);
        Assert.AreEqual("SqlTable", properties["ParentElementType"]);
        Assert.AreEqual("[PhoneNumber]", properties["NewName"]);

        var table = operations[1].Elements(ns + "Property").ToDictionary(p => p.Attribute("Name")!.Value, p => p.Attribute("Value")!.Value);
        Assert.AreEqual("[dbo].[Customer]", table["ElementName"]);
        Assert.AreEqual("SqlTable", table["ElementType"]);
        Assert.AreEqual("[dbo]", table["ParentElementName"]);
        Assert.AreEqual("SqlSchema", table["ParentElementType"]);
        Assert.AreEqual("[Customers]", table["NewName"]);

        var move = operations[2];
        Assert.AreEqual("Move Schema", move.Attribute("Name")?.Value);
        var moveProperties = move.Elements(ns + "Property").ToDictionary(p => p.Attribute("Name")!.Value, p => p.Attribute("Value")!.Value);
        Assert.AreEqual("[dbo].[Customers]", moveProperties["ElementName"]);
        Assert.AreEqual("crm", moveProperties["NewSchema"]);

        Assert.AreEqual("SqlIndex", operations[3].Elements(ns + "Property").Single(p => p.Attribute("Name")!.Value == "ElementType").Attribute("Value")!.Value);
        Assert.AreEqual("SqlPrimaryKeyConstraint", operations[4].Elements(ns + "Property").Single(p => p.Attribute("Name")!.Value == "ElementType").Attribute("Value")!.Value);
        Assert.AreEqual("[crm].[PK_Customer]", operations[4].Elements(ns + "Property").Single(p => p.Attribute("Name")!.Value == "ElementName").Attribute("Value")!.Value);
    }

    [TestMethod]
    public void Keys_are_deterministic_and_content_based()
    {
        var first = RefactorOperation.RenameColumn("dbo", "Customers", "Phone", "PhoneNumber");
        var again = RefactorOperation.RenameColumn("dbo", "Customers", "Phone", "PhoneNumber");
        var different = RefactorOperation.RenameColumn("dbo", "Customers", "Phone", "Mobile");

        Assert.AreEqual(first.Key, again.Key, "the same rename must map to the same key on every startup");
        Assert.AreNotEqual(first.Key, different.Key);
        Assert.AreNotEqual(first.Key, RefactorOperation.RenameTable("dbo", "Phone", "PhoneNumber").Key);
        Assert.AreEqual("Rename [dbo].[Customers].[Phone] to [PhoneNumber]", first.ToString());
        Assert.AreEqual("Move [dbo].[Customers] to schema crm", RefactorOperation.MoveToSchema("dbo", "Customers", "crm").ToString());
    }

    [TestMethod]
    public void Duplicate_operations_are_added_once()
    {
        var log = new RefactorLog()
            .RenameColumn("dbo", "Customers", "Phone", "PhoneNumber")
            .RenameColumn("dbo", "Customers", "Phone", "PhoneNumber");

        Assert.AreEqual(1, log.Operations.Count);
        Assert.IsFalse(log.IsEmpty);
    }

    [TestMethod]
    public void Identifiers_with_brackets_are_escaped()
    {
        var operation = RefactorOperation.RenameColumn("dbo", "Odd]Table", "Old]Name", "New]Name");

        Assert.AreEqual("[dbo].[Odd]]Table].[Old]]Name]", operation.GetProperty("ElementName"));
        Assert.AreEqual("[New]]Name]", operation.GetProperty("NewName"));
    }

    [TestMethod]
    public void Parses_and_round_trips_an_ssdt_refactorlog_file()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Operations Version="1.0" xmlns="http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02">
              <Operation Name="Rename Refactor" Key="7e10d1d2-8f7a-4c6a-bb5b-0e7d6d5d0a0e" ChangeDateTime="03/16/2020 13:33:33">
                <Property Name="ElementName" Value="[dbo].[Table1].[Column1]" />
                <Property Name="ElementType" Value="SqlSimpleColumn" />
                <Property Name="ParentElementName" Value="[dbo].[Table1]" />
                <Property Name="ParentElementType" Value="SqlTable" />
                <Property Name="NewName" Value="[Column2]" />
              </Operation>
              <Operation Name="Move Schema" Key="1b1a4e1c-6d6e-4f47-9f8a-3f4a2a1b0c0d" ChangeDateTime="03/17/2020 09:00:00">
                <Property Name="ElementName" Value="[dbo].[Table1]" />
                <Property Name="ElementType" Value="SqlTable" />
                <Property Name="NewSchema" Value="archive" />
              </Operation>
            </Operations>
            """;

        var log = RefactorLog.Parse(xml);

        Assert.AreEqual(2, log.Operations.Count);
        Assert.AreEqual(Guid.Parse("7e10d1d2-8f7a-4c6a-bb5b-0e7d6d5d0a0e"), log.Operations[0].Key, "keys from the file are preserved so already-applied operations stay applied");
        Assert.AreEqual(new DateTime(2020, 3, 16, 13, 33, 33, DateTimeKind.Utc), log.Operations[0].ChangedAt);
        Assert.IsTrue(log.Operations[0].IsRename);
        Assert.IsTrue(log.Operations[1].IsMoveSchema);
        Assert.AreEqual("archive", log.Operations[1].GetProperty("NewSchema"));

        var path = Path.Combine(Path.GetTempPath(), $"schemasync-{Guid.NewGuid():N}.refactorlog");
        try
        {
            log.Save(path);
            var reloaded = RefactorLog.Load(path);
            Assert.AreEqual(log.ToXml(), reloaded.ToXml());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Rejects_a_file_that_is_not_a_refactor_log()
    {
        Assert.ThrowsExactly<FormatException>(() => RefactorLog.Parse("<Nope />"));
    }

    [TestMethod]
    public void Model_annotations_become_ordered_refactor_operations()
    {
        using var context = new RenamedDbContext(TestContextFactory.SqlServerOptions<RenamedDbContext>());

        var package = SchemaConverter.Convert(context);

        var operations = package.RefactorOperations.Select(o => o.ToString()).ToList();
        CollectionAssert.AreEqual(
            new[]
            {
                "Rename [dbo].[Gadgets].[Caption] to [Title]",
                "Rename [dbo].[Gadgets].[Dimensions_Height] to [Dimensions_H]",
                "Rename [dbo].[Gadgets].[Double] to [Total]",
                "Rename [dbo].[Widget].[Name] to [Label]",
                "Rename [dbo].[Widget] to [Widgets]",
                "Move [dbo].[Widgets] to schema crm",
                "Rename [crm].[Widgets].[IX_Widget_Name] to [IX_Widgets_Label]",
                "Rename [crm].[PK_Widget] to [PK_Widgets]",
            },
            operations,
            string.Join(Environment.NewLine, operations));
        Assert.AreEqual("SqlComputedColumn", package.RefactorOperations[2].GetProperty("ElementType"), "computed columns use the computed element type");
        Assert.AreEqual("SqlSimpleColumn", package.RefactorOperations[1].GetProperty("ElementType"), "owned type columns rename on the owner's table");
    }

    [TestMethod]
    public void Explicit_log_and_file_are_appended_after_model_annotations()
    {
        using var context = new RenamedDbContext(TestContextFactory.SqlServerOptions<RenamedDbContext>());
        var path = Path.Combine(Path.GetTempPath(), $"schemasync-{Guid.NewGuid():N}.refactorlog");
        new RefactorLog().RenameIndex("crm", "Widgets", "IX_Old", "IX_New").Save(path);
        try
        {
            var package = SchemaConverter.Convert(context, new SchemaConversionOptions
            {
                RefactorLog = new RefactorLog().RenameConstraint("crm", "Widgets", RefactorConstraintKind.Check, "CK_Old", "CK_New"),
                RefactorLogPath = path,
            });

            var operations = package.RefactorOperations.Select(o => o.ToString()).ToList();
            Assert.AreEqual(10, operations.Count, string.Join(Environment.NewLine, operations));
            Assert.AreEqual("Rename [crm].[CK_Old] to [CK_New]", operations[8]);
            Assert.AreEqual("Rename [crm].[Widgets].[IX_Old] to [IX_New]", operations[9]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Custom_constraint_names_are_not_renamed()
    {
        var log = new RefactorLog().RenameTable("dbo", "Customers", "Clients");

        Assert.AreEqual(1, log.Operations.Count, "the explicit API never guesses dependents");
    }

    [TestMethod]
    public void Model_annotations_can_be_ignored()
    {
        using var context = new RenamedDbContext(TestContextFactory.SqlServerOptions<RenamedDbContext>());

        var package = SchemaConverter.Convert(context, new SchemaConversionOptions { UseModelAnnotations = false });

        Assert.AreEqual(0, package.RefactorOperations.Count);
    }

    [TestMethod]
    public void Missing_refactor_log_file_fails_at_conversion()
    {
        using var context = new RenamedDbContext(TestContextFactory.SqlServerOptions<RenamedDbContext>());

        var ex = Assert.ThrowsExactly<SchemaSyncException>(() => SchemaConverter.Convert(context, new SchemaConversionOptions { RefactorLogPath = "/nope/missing.refactorlog" }));

        Assert.AreEqual(SchemaSyncStage.ConvertModel, ex.Stage);
    }

    [TestMethod]
    public void Models_without_annotations_embed_no_refactor_log()
    {
        using var context = TestContextFactory.Matrix();

        Assert.AreEqual(0, SchemaConverter.Convert(context).RefactorOperations.Count);
    }

    [TestMethod]
    public void Deploy_report_maps_rename_and_move_operations()
    {
        Assert.AreEqual(SchemaChangeKind.Rename, DeployReport.ToKind("Rename"));
        Assert.AreEqual(SchemaChangeKind.MoveSchema, DeployReport.ToKind("MoveSchema"));
    }
}
