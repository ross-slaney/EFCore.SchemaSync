using EFCore.SchemaSync.Deployment;
using EFCore.SchemaSync.Tests.Models;

namespace EFCore.SchemaSync.Tests;

[TestClass]
public sealed class DeploymentScopeTests
{
    [TestMethod]
    public void By_default_the_context_owns_the_whole_database()
    {
        using var context = new BillingContext(TestContextFactory.SqlServerOptions<BillingContext>());

        var scope = DeploymentScope.Create(context, new SchemaSyncOptions());

        Assert.IsNull(scope.ManagedSchemas);
        Assert.AreEqual(0, scope.UnmanagedTables.Count);
        Assert.IsFalse(scope.IsRestricted, "no contributor is attached in the default case");
        Assert.IsFalse(scope.IsOutOfScope("anything", "[anything].[Table]"));
    }

    [TestMethod]
    public void Managed_schemas_include_the_schemas_the_model_deploys_into()
    {
        using var context = new BillingContext(TestContextFactory.SqlServerOptions<BillingContext>());
        var options = new SchemaSyncOptions();
        options.ManagedSchemas.Add("reporting");

        var scope = DeploymentScope.Create(context, options);

        CollectionAssert.AreEquivalent(new[] { "reporting", "billing" }, scope.ManagedSchemas!.ToArray());
        Assert.IsTrue(scope.IsRestricted);
        Assert.IsFalse(scope.IsOutOfScope("billing", null));
        Assert.IsFalse(scope.IsOutOfScope("BILLING", null), "schema names compare case-insensitively");
        Assert.IsFalse(scope.IsOutOfScope("reporting", null));
        Assert.IsTrue(scope.IsOutOfScope("shipping", null));
        Assert.IsTrue(scope.IsOutOfScope("dbo", "[dbo].[Anything]"));
        Assert.IsFalse(scope.IsOutOfScope(null, null), "objects without a schema (database options) are never touched by the scope");
    }

    [TestMethod]
    public void Tables_excluded_from_migrations_are_always_out_of_scope()
    {
        using var context = new ForeignTableContext(TestContextFactory.SqlServerOptions<ForeignTableContext>());

        var scope = DeploymentScope.Create(context, new SchemaSyncOptions());

        Assert.IsNull(scope.ManagedSchemas);
        CollectionAssert.AreEquivalent(new[] { "[dbo].[Memberships]" }, scope.UnmanagedTables.ToArray());
        Assert.IsTrue(scope.IsRestricted);
        Assert.IsTrue(scope.IsOutOfScope("dbo", "[dbo].[Memberships]"), "the table and everything owned by it is kept");
        Assert.IsFalse(scope.IsOutOfScope("dbo", "[dbo].[Invoices]"));
        Assert.IsFalse(scope.IsOutOfScope("dbo", null));
    }

    [TestMethod]
    public void Registration_makes_the_scope_reachable_until_disposed()
    {
        var scope = new DeploymentScope(null, new HashSet<string> { "[dbo].[X]" });

        string token;
        using (var registration = scope.Register())
        {
            token = registration.Token;
            Assert.AreSame(scope, DeploymentScope.Find(token));
        }

        Assert.IsNull(DeploymentScope.Find(token));
    }

    [TestMethod]
    public void The_contributor_is_attached_without_replacing_existing_ones()
    {
        var options = new SchemaSyncOptions
        {
            ConfigureDeployOptions = o => o.AdditionalDeploymentContributors += ";Other.Contributor",
        };

        var deploy = DacDeployOptionsFactory.Create(options, "abc123");

        Assert.AreEqual($"{ScopeContributor.ContributorId};Other.Contributor", deploy.AdditionalDeploymentContributors);
        Assert.AreEqual(typeof(ScopeContributor).Assembly.Location, deploy.AdditionalDeploymentContributorPaths);
        Assert.AreEqual($"{ScopeContributor.TokenArgument}=abc123", deploy.AdditionalDeploymentContributorArguments);
        Assert.IsNull(DacDeployOptionsFactory.Create(new SchemaSyncOptions()).AdditionalDeploymentContributors, "nothing is attached without a scope");

        var report = DacDeployOptionsFactory.CreateForRetainedObjectsReport(new SchemaSyncOptions(), "abc123");
        Assert.AreEqual(ScopeContributor.ContributorId, report.AdditionalDeploymentContributors, "the retained-objects report is scoped too");
    }
}
