using EFCore.SchemaSync.Deployment;
using Microsoft.SqlServer.Dac;

namespace EFCore.SchemaSync.Tests;

[TestClass]
public sealed class DacDeployOptionsFactoryTests
{
    [TestMethod]
    public void Defaults_are_safe()
    {
        var options = DacDeployOptionsFactory.Create(new SchemaSyncOptions());

        Assert.IsTrue(options.BlockOnPossibleDataLoss);
        Assert.IsTrue(options.VerifyDeployment);
        Assert.IsFalse(options.TreatVerificationErrorsAsWarnings);
        Assert.IsTrue(options.IncludeTransactionalScripts);
        Assert.IsFalse(options.DropObjectsNotInSource);
        Assert.IsFalse(options.DropIndexesNotInSource);
        Assert.IsFalse(options.DropConstraintsNotInSource);
        Assert.IsFalse(options.DropDmlTriggersNotInSource);
        Assert.IsFalse(options.DropExtendedPropertiesNotInSource);
        Assert.IsFalse(options.DropStatisticsNotInSource);
        Assert.IsFalse(options.DropPermissionsNotInSource);
        Assert.IsFalse(options.DropRoleMembersNotInSource);
        Assert.IsFalse(options.CreateNewDatabase);
        Assert.IsFalse(options.BackupDatabaseBeforeChanges);
        Assert.IsFalse(options.DeployDatabaseInSingleUserMode);
        Assert.IsFalse(options.GenerateSmartDefaults);
        Assert.IsFalse(options.AllowIncompatiblePlatform);
    }

    [TestMethod]
    public void Security_and_database_configuration_are_unmanaged()
    {
        var options = DacDeployOptionsFactory.Create(new SchemaSyncOptions());

        Assert.IsTrue(options.IgnorePermissions);
        Assert.IsTrue(options.IgnoreRoleMembership);
        Assert.IsTrue(options.IgnoreUserSettingsObjects);
        Assert.IsTrue(options.IgnoreLoginSids);
        Assert.IsTrue(options.IgnoreAuthorizer);
        Assert.IsFalse(options.ScriptDatabaseOptions);
        Assert.IsFalse(options.ScriptDatabaseCollation);
        Assert.IsFalse(options.ScriptDatabaseCompatibility);
        Assert.IsTrue(options.IgnoreFilegroupPlacement);
        Assert.IsTrue(options.IgnoreFileAndLogFilePath);
        Assert.IsTrue(options.IgnoreColumnOrder);
        Assert.IsTrue(options.IgnoreIndexOptions);
        Assert.IsTrue(options.IgnoreTableOptions);

        var excluded = options.ExcludeObjectTypes;
        CollectionAssert.Contains(excluded, ObjectType.Users);
        CollectionAssert.Contains(excluded, ObjectType.Logins);
        CollectionAssert.Contains(excluded, ObjectType.Permissions);
        CollectionAssert.Contains(excluded, ObjectType.RoleMembership);
        CollectionAssert.Contains(excluded, ObjectType.DatabaseRoles);
        CollectionAssert.Contains(excluded, ObjectType.DatabaseOptions);
        CollectionAssert.Contains(excluded, ObjectType.Filegroups);
        CollectionAssert.Contains(excluded, ObjectType.Files);
        CollectionAssert.DoesNotContain(excluded, ObjectType.Tables);
        CollectionAssert.DoesNotContain(excluded, ObjectType.Views);
        CollectionAssert.IsSubsetOf(excluded, options.DoNotDropObjectTypes, "everything unmanaged is also protected from drops (DacFx may add entries of its own)");
    }

    [TestMethod]
    public void Opt_ins_map_to_dacfx_switches()
    {
        var removal = DacDeployOptionsFactory.Create(new SchemaSyncOptions { AllowObjectRemoval = true });
        Assert.IsTrue(removal.DropObjectsNotInSource);
        Assert.IsTrue(removal.DropIndexesNotInSource);
        Assert.IsTrue(removal.DropConstraintsNotInSource);
        Assert.IsTrue(removal.DropDmlTriggersNotInSource);
        Assert.IsTrue(removal.DropExtendedPropertiesNotInSource);
        Assert.IsTrue(removal.BlockOnPossibleDataLoss, "object removal does not imply data loss permission");
        CollectionAssert.Contains(removal.DoNotDropObjectTypes, ObjectType.Users);

        var dataLoss = DacDeployOptionsFactory.Create(new SchemaSyncOptions { AllowDataLoss = true });
        Assert.IsFalse(dataLoss.BlockOnPossibleDataLoss);
        Assert.IsFalse(dataLoss.DropObjectsNotInSource, "data loss permission does not imply object removal");

        var noTransaction = DacDeployOptionsFactory.Create(new SchemaSyncOptions { UseTransaction = false });
        Assert.IsFalse(noTransaction.IncludeTransactionalScripts);

        var platform = DacDeployOptionsFactory.Create(new SchemaSyncOptions { AllowIncompatiblePlatform = true });
        Assert.IsTrue(platform.AllowIncompatiblePlatform);
    }

    [TestMethod]
    public void Timeouts_are_mapped_in_seconds()
    {
        var options = DacDeployOptionsFactory.Create(new SchemaSyncOptions
        {
            CommandTimeout = TimeSpan.FromSeconds(90),
            LongRunningCommandTimeout = TimeSpan.FromMinutes(10),
        });

        Assert.AreEqual(90, options.CommandTimeout);
        Assert.AreEqual(600, options.LongRunningCommandTimeout);

        var defaults = DacDeployOptionsFactory.Create(new SchemaSyncOptions());
        Assert.AreEqual(300, defaults.CommandTimeout);
        Assert.AreEqual(0, defaults.LongRunningCommandTimeout, "0 lets DacFx fall back to CommandTimeout");
    }

    [TestMethod]
    public void Configure_callback_runs_last()
    {
        var options = DacDeployOptionsFactory.Create(new SchemaSyncOptions
        {
            ConfigureDeployOptions = o => o.IgnoreColumnOrder = false,
        });

        Assert.IsFalse(options.IgnoreColumnOrder);
    }

    [TestMethod]
    public void Retained_objects_report_options_drop_everything_but_never_deploy_settings()
    {
        var options = DacDeployOptionsFactory.CreateForRetainedObjectsReport(new SchemaSyncOptions());

        Assert.IsTrue(options.DropObjectsNotInSource);
        Assert.IsTrue(options.DropIndexesNotInSource);
        Assert.IsTrue(options.DropConstraintsNotInSource);
        Assert.IsTrue(options.DropDmlTriggersNotInSource);
        Assert.IsFalse(options.BlockOnPossibleDataLoss);
        Assert.IsTrue(options.TreatVerificationErrorsAsWarnings);
        CollectionAssert.Contains(options.DoNotDropObjectTypes, ObjectType.Users);
    }

    [TestMethod]
    public void Option_validation_rejects_nonsense()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SchemaSyncOptions { LockTimeout = TimeSpan.Zero }.Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SchemaSyncOptions { CommandTimeout = TimeSpan.FromSeconds(-1) }.Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SchemaSyncOptions { LongRunningCommandTimeout = TimeSpan.Zero }.Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new SchemaSyncOptions { LockResourceName = " " }.Validate());
        new SchemaSyncOptions().Validate();
    }
}
