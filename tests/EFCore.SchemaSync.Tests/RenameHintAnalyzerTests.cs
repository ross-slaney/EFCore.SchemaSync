using EFCore.SchemaSync.Deployment;
using EFCore.SchemaSync.Tests.Models;
using static EFCore.SchemaSync.Deployment.RenameHintAnalyzer;

namespace EFCore.SchemaSync.Tests;

[TestClass]
public sealed class RenameHintAnalyzerTests
{
    private const string CustomerPrefix = "modelBuilder.Entity<Customer>()";

    private static ModelTable Model(string table, params (string Name, string Type)[] columns)
        => new("dbo", table, CustomerPrefix, columns.Select(c => new ModelColumn(c.Name, c.Type, $"{CustomerPrefix}.Property(e => e.{c.Name})")).ToList());

    private static TargetTable Target(string table, params (string Name, string Type)[] columns)
        => Target("dbo", table, columns);

    private static TargetTable Target(string schema, string table, params (string Name, string Type)[] columns)
        => new(schema, table, columns.Select(c => new TargetColumn(c.Name, c.Type)).ToList());

    private static HashSet<(string, string)> Known(params ModelTable[] tables) => tables.Select(t => (t.Schema, t.Name)).ToHashSet();

    [TestMethod]
    public void A_dropped_column_next_to_a_new_column_of_the_same_type_is_a_hint()
    {
        var model = Model("Customers", ("Id", "int"), ("Name", "nvarchar(100)"), ("PhoneNumber", "nvarchar(30)"));
        var target = Target("Customers", ("Id", "int"), ("Name", "nvarchar"), ("Phone", "nvarchar"));

        var hint = Analyze([model], Known(model), [target]).Single();

        Assert.AreEqual(RenameHintKind.Column, hint.Kind);
        Assert.AreEqual("[dbo].[Customers].[Phone]", hint.OldName);
        Assert.AreEqual("[dbo].[Customers].[PhoneNumber]", hint.NewName);
        Assert.AreEqual("modelBuilder.Entity<Customer>().Property(e => e.PhoneNumber).WasRenamedFrom(\"Phone\");", hint.Suggestion);
        Assert.AreEqual($"{hint.OldName} -> {hint.NewName}: {hint.Suggestion}", hint.ToString());
    }

    [TestMethod]
    public void A_rename_with_a_length_change_is_still_a_hint()
    {
        var model = Model("Customers", ("Id", "int"), ("PhoneNumber", "nvarchar(50)"));

        var hints = Analyze([model], Known(model), [Target("Customers", ("Id", "int"), ("Phone", "nvarchar"))]);

        Assert.AreEqual(1, hints.Count);
    }

    [TestMethod]
    public void Different_base_types_are_not_a_rename()
    {
        var model = Model("Customers", ("Id", "int"), ("Tier", "int"));

        Assert.AreEqual(0, Analyze([model], Known(model), [Target("Customers", ("Id", "int"), ("Phone", "nvarchar"))]).Count);
    }

    [TestMethod]
    public void Ambiguous_pairs_produce_no_hint()
    {
        var model = Model("Customers", ("Id", "int"), ("PhoneNumber", "nvarchar(30)"), ("EmailAddress", "nvarchar(320)"));
        var target = Target("Customers", ("Id", "int"), ("Phone", "nvarchar"), ("Email", "nvarchar"));

        Assert.AreEqual(0, Analyze([model], Known(model), [target]).Count, "two dropped and two added columns of one type cannot be paired safely");
    }

    [TestMethod]
    public void One_dropped_column_with_two_candidates_produces_no_hint()
    {
        var model = Model("Customers", ("Id", "int"), ("Mobile", "nvarchar(30)"), ("Landline", "nvarchar(30)"));

        Assert.AreEqual(0, Analyze([model], Known(model), [Target("Customers", ("Id", "int"), ("Phone", "nvarchar"))]).Count);
    }

    [TestMethod]
    public void Pure_drops_and_pure_adds_produce_no_hint()
    {
        var model = Model("Customers", ("Id", "int"), ("Name", "nvarchar(100)"));

        Assert.AreEqual(0, Analyze([model], Known(model), [Target("Customers", ("Id", "int"), ("Name", "nvarchar"), ("Phone", "nvarchar"))]).Count);
        Assert.AreEqual(0, Analyze([model], Known(model), [Target("Customers", ("Id", "int"))]).Count);
    }

    [TestMethod]
    public void Column_name_comparison_ignores_case()
    {
        var model = Model("Customers", ("Id", "int"), ("phone", "nvarchar(30)"));

        Assert.AreEqual(0, Analyze([model], Known(model), [Target("Customers", ("ID", "int"), ("Phone", "nvarchar"))]).Count, "a case change is not a dropped column");
    }

    [TestMethod]
    public void A_new_table_matching_an_unmapped_table_is_a_hint()
    {
        var clients = Model("Clients", ("Id", "int"), ("Name", "nvarchar(100)"));
        var target = Target("Customers", ("Id", "int"), ("Name", "nvarchar"));

        var hint = Analyze([clients], Known(clients), [target]).Single();

        Assert.AreEqual(RenameHintKind.Table, hint.Kind);
        Assert.AreEqual("[dbo].[Customers]", hint.OldName);
        Assert.AreEqual("[dbo].[Clients]", hint.NewName);
        Assert.AreEqual("modelBuilder.Entity<Customer>().WasRenamedFrom(\"Customers\");", hint.Suggestion);
    }

    [TestMethod]
    public void A_table_moved_to_another_schema_includes_the_previous_schema()
    {
        var clients = new ModelTable("crm", "Clients", CustomerPrefix, [new ModelColumn("Id", "int", "x")]);

        var hint = Analyze([clients], Known(clients), [Target("dbo", "Customers", ("Id", "int"))]).Single();

        Assert.AreEqual("[crm].[Clients]", hint.NewName);
        Assert.AreEqual("modelBuilder.Entity<Customer>().WasRenamedFrom(\"Customers\", \"dbo\");", hint.Suggestion);
    }

    [TestMethod]
    public void Tables_with_different_columns_two_candidates_or_infrastructure_produce_no_hint()
    {
        var clients = Model("Clients", ("Id", "int"), ("Name", "nvarchar(100)"));

        Assert.AreEqual(0, Analyze([clients], Known(clients), [Target("Customers", ("Id", "int"), ("Title", "nvarchar"))]).Count);
        Assert.AreEqual(0, Analyze([clients], Known(clients), [Target("Customers", ("Id", "int"), ("Name", "nvarchar")), Target("People", ("Id", "int"), ("Name", "nvarchar"))]).Count);
        Assert.AreEqual(0, Analyze([clients], Known(clients), [Target("__RefactorLog", ("Id", "int"), ("Name", "nvarchar"))]).Count);
    }

    [TestMethod]
    public void Tables_the_model_knows_but_does_not_deploy_are_never_rename_sources()
    {
        var clients = Model("Clients", ("Id", "int"), ("Name", "nvarchar(100)"));
        var known = Known(clients);
        known.Add(("dbo", "Customers"));

        Assert.AreEqual(0, Analyze([clients], known, [Target("Customers", ("Id", "int"), ("Name", "nvarchar"))]).Count);
    }

    [TestMethod]
    public void Owned_collection_tables_get_no_table_hint()
    {
        var owned = new ModelTable("dbo", "Lines", null, [new ModelColumn("Id", "int", "x")]);

        Assert.AreEqual(0, Analyze([owned], Known(owned), [Target("OrderLines", ("Id", "int"))]).Count);
    }

    [TestMethod]
    [DataRow("nvarchar(30)", "nvarchar", true)]
    [DataRow("NVARCHAR(MAX)", "nvarchar", true)]
    [DataRow("decimal(18,2)", "decimal", true)]
    [DataRow("dec(18,2)", "decimal", true)]
    [DataRow("rowversion", "timestamp", true)]
    [DataRow("[int]", "int", true)]
    [DataRow("nvarchar(30)", "varchar", false)]
    [DataRow("datetime2", "datetime", false)]
    public void Base_type_comparison(string left, string right, bool same)
        => Assert.AreEqual(same, SameBaseType(left, right));

    [TestMethod]
    public void Model_description_generates_compilable_suggestions_for_every_mapping_shape()
    {
        using var context = TestContextFactory.Matrix();

        var (deployed, known) = DescribeModel(context);

        string Suggestion(string table, string column) => deployed.Single(t => t.Name == table).Columns.Single(c => c.Name == column).Suggestion;
        Assert.AreEqual("modelBuilder.Entity<Customer>().Property(e => e.Email)", Suggestion("Customers", "Email"));
        Assert.AreEqual("modelBuilder.Entity<Customer>().OwnsOne(e => e.Address).Property(e => e.Street)", Suggestion("Customers", "Address_Street"), "owned type columns go through OwnsOne");
        Assert.AreEqual("modelBuilder.Entity<CardPayment>().Property(e => e.CardLast4)", Suggestion("Payments", "CardLast4"), "TPH columns use their declaring type");
        Assert.AreEqual("modelBuilder.Entity<Payment>().Property(\"PaymentType\")", Suggestion("Payments", "PaymentType"), "shadow properties use the string overload");
        Assert.AreEqual("modelBuilder.Entity<Payment>()", deployed.Single(t => t.Name == "Payments").TableSuggestionPrefix, "a TPH table is renamed on its root type");
        Assert.AreEqual("nvarchar(320)", deployed.Single(t => t.Name == "Customers").Columns.Single(c => c.Name == "Email").StoreType);
        Assert.IsTrue(known.Contains(("sales", "Orders")));
        Assert.AreEqual(1, deployed.Count(t => t.Name == "Customers"), "owned columns are folded into the owner's table");
    }

    [TestMethod]
    public void Tables_excluded_from_migrations_are_known_but_not_deployed()
    {
        using var context = new ExcludedTableDbContext(TestContextFactory.SqlServerOptions<ExcludedTableDbContext>());

        var (deployed, known) = DescribeModel(context);

        Assert.IsTrue(known.Contains(("dbo", "Widgets")));
        Assert.IsFalse(deployed.Any(t => t.Name == "Widgets"));
        Assert.IsTrue(deployed.Any(t => t.Name == "Gadgets"));
    }

    [TestMethod]
    public void Blocked_exception_lists_hints_in_its_message()
    {
        var hint = new RenameHint(RenameHintKind.Column, "[dbo].[Customers].[Phone]", "[dbo].[Customers].[PhoneNumber]", "modelBuilder.Entity<Customer>().Property(e => e.PhoneNumber).WasRenamedFrom(\"Phone\");");

        var ex = new SchemaChangesBlockedException(["The column [dbo].[Customers].[Phone] is being dropped, data loss could occur."], [], [hint]);

        Assert.AreEqual(hint, ex.RenameHints.Single());
        StringAssert.Contains(ex.Message, "If this is a rename, declare it");
        StringAssert.Contains(ex.Message, hint.Suggestion);
        Assert.AreEqual(0, new SchemaChangesBlockedException(["x"], []).RenameHints.Count);
    }
}
