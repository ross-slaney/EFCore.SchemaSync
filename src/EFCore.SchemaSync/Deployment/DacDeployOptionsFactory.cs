using Microsoft.SqlServer.Dac;

namespace EFCore.SchemaSync.Deployment;

/// <summary>
/// Builds the DacFx deployment options from <see cref="SchemaSyncOptions"/>. Everything that is not part of the
/// EF model (security, database settings, files, placement, index storage options) is treated as unmanaged.
/// </summary>
internal static class DacDeployOptionsFactory
{
    /// <summary>Object types the library never manages: security and database/server configuration.</summary>
    internal static readonly ObjectType[] UnmanagedObjectTypes =
    [
        ObjectType.Users,
        ObjectType.Logins,
        ObjectType.RoleMembership,
        ObjectType.Permissions,
        ObjectType.DatabaseRoles,
        ObjectType.ApplicationRoles,
        ObjectType.ServerRoles,
        ObjectType.ServerRoleMembership,
        ObjectType.Credentials,
        ObjectType.DatabaseScopedCredentials,
        ObjectType.Certificates,
        ObjectType.SymmetricKeys,
        ObjectType.AsymmetricKeys,
        ObjectType.MasterKeys,
        ObjectType.DatabaseEncryptionKeys,
        ObjectType.Signatures,
        ObjectType.CryptographicProviders,
        ObjectType.Audits,
        ObjectType.ServerAuditSpecifications,
        ObjectType.DatabaseAuditSpecifications,
        ObjectType.DatabaseOptions,
        ObjectType.Filegroups,
        ObjectType.Files,
        ObjectType.Endpoints,
        ObjectType.LinkedServers,
        ObjectType.LinkedServerLogins,
        ObjectType.ServerTriggers,
        ObjectType.ErrorMessages,
        ObjectType.EventNotifications,
        ObjectType.EventSessions,
        ObjectType.DatabaseWorkloadGroups,
        ObjectType.WorkloadClassifiers,
        ObjectType.Routes,
        ObjectType.ExternalStreams,
        ObjectType.ExternalStreamingJobs,
    ];

    /// <summary>
    /// Creates the options used for the real comparison and deployment.
    /// </summary>
    public static DacDeployOptions Create(SchemaSyncOptions options)
    {
        var commandTimeout = ToSeconds(options.CommandTimeout);
        var deploy = new DacDeployOptions
        {
            // Security and database-level configuration are unmanaged. These collections are assigned first:
            // DacFx resets some Drop*NotInSource switches when DoNotDropObjectTypes is assigned.
            ExcludeObjectTypes = UnmanagedObjectTypes,
            DoNotDropObjectTypes = UnmanagedObjectTypes,
            IgnorePermissions = true,
            IgnoreRoleMembership = true,
            IgnoreUserSettingsObjects = true,
            IgnoreLoginSids = true,
            IgnoreAuthorizer = true,
            ScriptDatabaseOptions = false,
            ScriptDatabaseCollation = false,
            ScriptDatabaseCompatibility = false,
            IgnoreFilegroupPlacement = true,
            IgnoreFileAndLogFilePath = true,
            IgnoreFileSize = true,
            IgnoreFullTextCatalogFilePath = true,
            IgnoreSensitivityClassifications = true,
            IgnoreDatabaseWorkloadGroups = true,
            IgnoreWorkloadClassifiers = true,

            // Safety.
            BlockOnPossibleDataLoss = !options.AllowDataLoss,
            VerifyDeployment = true,
            TreatVerificationErrorsAsWarnings = false,
            IncludeTransactionalScripts = options.UseTransaction,
            BackupDatabaseBeforeChanges = false,
            CreateNewDatabase = false,
            DeployDatabaseInSingleUserMode = false,
            GenerateSmartDefaults = false,
            AllowIncompatiblePlatform = options.AllowIncompatiblePlatform,

            // Target-only objects are preserved unless explicitly opted in.
            DropObjectsNotInSource = options.AllowObjectRemoval,
            DropIndexesNotInSource = options.AllowObjectRemoval,
            DropConstraintsNotInSource = options.AllowObjectRemoval,
            DropDmlTriggersNotInSource = options.AllowObjectRemoval,
            DropExtendedPropertiesNotInSource = options.AllowObjectRemoval,
            DropStatisticsNotInSource = false,
            DropPermissionsNotInSource = false,
            DropRoleMembersNotInSource = false,

            // Physical/storage details EF does not model are left to the DBA.
            IgnoreColumnOrder = true,
            IgnoreFillFactor = true,
            IgnoreIndexPadding = true,
            IgnoreIndexOptions = true,
            IgnoreLockHintsOnIndexes = true,
            IgnoreTableOptions = true,
            IgnoreTablePartitionOptions = true,
            IgnorePartitionSchemes = true,
            IgnoreObjectPlacementOnPartitionScheme = true,
            IgnoreNotForReplication = true,
            IgnoreWithNocheckOnCheckConstraints = true,
            IgnoreWithNocheckOnForeignKeys = true,
            IgnoreDmlTriggerState = true,
            IgnoreDmlTriggerOrder = true,
            IgnoreDdlTriggerState = true,
            IgnoreDdlTriggerOrder = true,
            IgnoreWhitespace = true,
            IgnoreKeywordCasing = true,
            IgnoreSemicolonBetweenStatements = true,
            IgnoreComments = true,

            // Timeouts.
            CommandTimeout = commandTimeout,
            LongRunningCommandTimeout = options.LongRunningCommandTimeout is { } longRunning ? ToSeconds(longRunning) : 0,
            DatabaseLockTimeout = 60,
        };

        options.ConfigureDeployOptions?.Invoke(deploy);
        return deploy;
    }

    /// <summary>
    /// Creates options that drop every target-only object DacFx manages, used only to *report* which objects
    /// the real deployment preserved. These options are never used to deploy.
    /// </summary>
    public static DacDeployOptions CreateForRetainedObjectsReport(SchemaSyncOptions options)
    {
        var report = Create(options);
        report.DoNotDropObjectTypes = UnmanagedObjectTypes;
        report.BlockOnPossibleDataLoss = false;
        report.TreatVerificationErrorsAsWarnings = true;
        report.DropObjectsNotInSource = true;
        report.DropIndexesNotInSource = true;
        report.DropConstraintsNotInSource = true;
        report.DropDmlTriggersNotInSource = true;
        report.DropExtendedPropertiesNotInSource = true;
        return report;
    }

    private static int ToSeconds(TimeSpan value)
    {
        var seconds = Math.Ceiling(value.TotalSeconds);
        return seconds >= int.MaxValue ? int.MaxValue : (int)Math.Max(1, seconds);
    }
}
