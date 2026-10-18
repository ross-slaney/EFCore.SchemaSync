using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EFCore.SchemaSync.Conversion;

namespace EFCore.SchemaSync.Deployment;

/// <summary>
/// A SHA-256 over everything that decides what a deployment does: the EF create script, the refactor log, the database
/// collation, the target platform, the deployment-relevant options and the library version. Computed without ScriptDom
/// or DacFx so an unchanged start never loads them.
/// </summary>
internal static class ModelHash
{
    public static string LibraryVersion { get; } =
        typeof(ModelHash).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ModelHash).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static string Compute(PreparedSchema prepared, string? collation, Microsoft.SqlServer.Dac.Model.SqlServerVersion platform, SchemaSyncOptions options)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "␀");
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        Add("EFCore.SchemaSync model hash v1");
        Add(LibraryVersion);
        Add(collation);
        Add(platform.ToString());
        Add(options.AllowIncompatiblePlatform.ToString());
        Add(options.AllowDataLoss.ToString());
        Add(options.AllowObjectRemoval.ToString());
        Add(options.ReportRetainedObjects.ToString());
        Add(options.UseTransaction.ToString());
        Add(string.Join("|", options.ManagedSchemas.Select(schema => schema.ToUpperInvariant()).Order(StringComparer.Ordinal)));
        Add(prepared.CreateScript);
        Add(prepared.RefactorLog.ToXml());
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
