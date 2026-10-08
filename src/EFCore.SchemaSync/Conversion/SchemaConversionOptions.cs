using Microsoft.SqlServer.Dac.Model;

namespace EFCore.SchemaSync.Conversion;

/// <summary>Options for <see cref="SchemaConverter"/>.</summary>
public sealed class SchemaConversionOptions
{
    /// <summary>SQL Server platform the DACPAC targets. Default: SQL Server 2022.</summary>
    public SqlServerVersion TargetSqlServerVersion { get; set; } = SqlServerVersion.Sql160;

    /// <summary>
    /// Collation of the target database, used as the model collation so identifier comparison matches the
    /// database. When null the DacFx default (SQL_Latin1_General_CP1_CI_AS) is used.
    /// </summary>
    public string? Collation { get; set; }

    /// <summary>Package name recorded in the DACPAC. Default: the DbContext type name.</summary>
    public string? PackageName { get; set; }
}
