using Microsoft.SqlServer.Dac;

namespace EFCore.SchemaSync.Conversion;

/// <summary>
/// The DACPAC generated from an EF Core model, kept in memory. Consumers normally never see it; it is exposed
/// so the conversion can be inspected and tested independently of deployment.
/// </summary>
public sealed class SchemaPackage
{
    private readonly byte[] _dacpac;

    internal SchemaPackage(string name, byte[] dacpac, IReadOnlyList<SchemaObjectReference> objects, IReadOnlyList<string> warnings, string normalizedScript, int skippedSeedStatements, IReadOnlyList<RefactorOperation> refactorOperations)
    {
        Name = name;
        _dacpac = dacpac;
        Objects = objects;
        Warnings = warnings;
        NormalizedScript = normalizedScript;
        SkippedSeedStatements = skippedSeedStatements;
        RefactorOperations = refactorOperations;
    }

    /// <summary>Package name (the DbContext type name by default).</summary>
    public string Name { get; }

    /// <summary>The DACPAC bytes.</summary>
    public ReadOnlyMemory<byte> Content => _dacpac;

    /// <summary>Schema objects in the DacFx model (tables, indexes, constraints, sequences, schemas, extended properties).</summary>
    public IReadOnlyList<SchemaObjectReference> Objects { get; }

    /// <summary>Conversion warnings (for example skipped seed data, DacFx model warnings).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The declarative T-SQL that was fed to DacFx, one statement per batch. Useful for diagnostics.</summary>
    public string NormalizedScript { get; }

    /// <summary>Number of INSERT statements (EF <c>HasData</c> seed rows) that were skipped because the library deploys schema only.</summary>
    public int SkippedSeedStatements { get; }

    /// <summary>Renames and schema moves embedded in the DACPAC as its refactor log, in application order.</summary>
    public IReadOnlyList<RefactorOperation> RefactorOperations { get; }

    /// <summary>Opens a read-only stream over the DACPAC bytes.</summary>
    public Stream OpenRead() => new MemoryStream(_dacpac, writable: false);

    /// <summary>Writes the DACPAC to disk, for example to inspect it with SqlPackage or a SQL project.</summary>
    public void Save(string path) => File.WriteAllBytes(path, _dacpac);

    internal DacPackage LoadDacPackage() => DacPackage.Load(OpenRead(), DacSchemaModelStorageType.Memory);
}
