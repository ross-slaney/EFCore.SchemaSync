using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EFCore.SchemaSync;

/// <summary>Constraint kinds a <see cref="RefactorOperation"/> can rename.</summary>
public enum RefactorConstraintKind
{
    /// <summary>A PRIMARY KEY constraint.</summary>
    PrimaryKey,

    /// <summary>A FOREIGN KEY constraint.</summary>
    ForeignKey,

    /// <summary>A UNIQUE constraint (EF alternate key).</summary>
    Unique,

    /// <summary>A CHECK constraint.</summary>
    Check,

    /// <summary>A DEFAULT constraint.</summary>
    Default,
}

/// <summary>
/// One entry of a DacFx refactor log: a rename or a schema move that DacFx applies in place
/// (<c>sp_rename</c>, <c>ALTER SCHEMA ... TRANSFER</c>) instead of dropping and recreating the object.
/// DacFx records the <see cref="Key"/> in the target's <c>dbo.__RefactorLog</c> table so every operation runs once.
/// </summary>
public sealed class RefactorOperation
{
    /// <summary>Operation name DacFx uses for renames.</summary>
    public const string RenameOperationName = "Rename Refactor";

    /// <summary>Operation name DacFx uses for schema moves.</summary>
    public const string MoveSchemaOperationName = "Move Schema";

    internal static readonly DateTime DefaultChangedAt = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Creates an operation from its raw DacFx representation.</summary>
    /// <param name="name">Operation name, for example <see cref="RenameOperationName"/>.</param>
    /// <param name="properties">Ordered DacFx properties (ElementName, ElementType, ParentElementName, ParentElementType, NewName, NewSchema).</param>
    /// <param name="key">Operation key recorded in the database; when null a deterministic key is derived from the operation's content.</param>
    /// <param name="changedAt">Informational timestamp written to the log.</param>
    public RefactorOperation(string name, IReadOnlyList<KeyValuePair<string, string>> properties, Guid? key = null, DateTime? changedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(properties);
        Name = name;
        Properties = properties;
        Key = key ?? DeterministicKey(name, properties);
        ChangedAt = changedAt ?? DefaultChangedAt;
    }

    /// <summary>Key DacFx stores in <c>dbo.__RefactorLog</c> once the operation has been applied (or skipped).</summary>
    public Guid Key { get; }

    /// <summary>DacFx operation name.</summary>
    public string Name { get; }

    /// <summary>Informational timestamp written to the log.</summary>
    public DateTime ChangedAt { get; }

    /// <summary>Ordered DacFx properties of the operation.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Properties { get; }

    /// <summary>True for a rename.</summary>
    public bool IsRename => string.Equals(Name, RenameOperationName, StringComparison.Ordinal);

    /// <summary>True for a schema move.</summary>
    public bool IsMoveSchema => string.Equals(Name, MoveSchemaOperationName, StringComparison.Ordinal);

    /// <summary>Returns a property value or null.</summary>
    public string? GetProperty(string propertyName)
        => Properties.FirstOrDefault(p => string.Equals(p.Key, propertyName, StringComparison.Ordinal)).Value;

    /// <summary>Renames a table. Names are unquoted identifiers.</summary>
    public static RefactorOperation RenameTable(string schema, string oldName, string newName)
        => Rename(Qualified(schema, oldName), "SqlTable", Quote(schema), "SqlSchema", newName);

    /// <summary>Renames a column. Names are unquoted identifiers.</summary>
    public static RefactorOperation RenameColumn(string schema, string table, string oldName, string newName, bool isComputed = false)
        => Rename(Qualified(schema, table, oldName), isComputed ? "SqlComputedColumn" : "SqlSimpleColumn", Qualified(schema, table), "SqlTable", newName);

    /// <summary>Renames an index of a table.</summary>
    public static RefactorOperation RenameIndex(string schema, string table, string oldName, string newName)
        => Rename(Qualified(schema, table, oldName), "SqlIndex", Qualified(schema, table), "SqlTable", newName);

    /// <summary>Renames a constraint of a table.</summary>
    public static RefactorOperation RenameConstraint(string schema, string table, RefactorConstraintKind kind, string oldName, string newName)
    {
        var elementType = kind switch
        {
            RefactorConstraintKind.PrimaryKey => "SqlPrimaryKeyConstraint",
            RefactorConstraintKind.ForeignKey => "SqlForeignKeyConstraint",
            RefactorConstraintKind.Unique => "SqlUniqueConstraint",
            RefactorConstraintKind.Check => "SqlCheckConstraint",
            RefactorConstraintKind.Default => "SqlDefaultConstraint",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return Rename(Qualified(schema, oldName), elementType, Qualified(schema, table), "SqlTable", newName);
    }

    /// <summary>Moves a table to another schema.</summary>
    public static RefactorOperation MoveToSchema(string schema, string table, string newSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newSchema);
        return new RefactorOperation(MoveSchemaOperationName,
        [
            new("ElementName", Qualified(schema, table)),
            new("ElementType", "SqlTable"),
            new("NewSchema", newSchema),
        ]);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var element = GetProperty("ElementName") ?? "?";
        if (IsRename)
        {
            return $"Rename {element} to {GetProperty("NewName")}";
        }

        if (IsMoveSchema)
        {
            return $"Move {element} to schema {GetProperty("NewSchema")}";
        }

        return $"{Name} {element}";
    }

    private static RefactorOperation Rename(string elementName, string elementType, string parentName, string parentType, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        return new RefactorOperation(RenameOperationName,
        [
            new("ElementName", elementName),
            new("ElementType", elementType),
            new("ParentElementName", parentName),
            new("ParentElementType", parentType),
            new("NewName", Quote(newName)),
        ]);
    }

    internal static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    private static string Qualified(params string[] parts) => string.Join(".", parts.Select(Quote));

    private static Guid DeterministicKey(string name, IReadOnlyList<KeyValuePair<string, string>> properties)
    {
        var builder = new StringBuilder("EFCore.SchemaSync.RefactorOperation\n").Append(name);
        foreach (var (propertyName, value) in properties)
        {
            builder.Append('\n').Append(propertyName).Append('=').Append(value);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// An ordered list of renames and schema moves DacFx applies in place before comparing the rest of the schema,
/// the same mechanism SQL Server Data Tools projects use through their <c>.refactorlog</c> file.
/// Operations run in order and must name objects as they are called at that point in the sequence.
/// </summary>
public sealed class RefactorLog
{
    /// <summary>XML namespace of the DacFx refactor log format.</summary>
    public const string Namespace = "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";

    private const string ChangeDateTimeFormat = "MM/dd/yyyy HH:mm:ss";
    private readonly List<RefactorOperation> _operations = [];

    /// <summary>The operations in application order.</summary>
    public IReadOnlyList<RefactorOperation> Operations => _operations;

    /// <summary>True when the log holds no operations.</summary>
    public bool IsEmpty => _operations.Count == 0;

    /// <summary>Appends an operation; an operation whose key is already present is ignored.</summary>
    public RefactorLog Add(RefactorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!_operations.Any(o => o.Key == operation.Key))
        {
            _operations.Add(operation);
        }

        return this;
    }

    /// <summary>Appends operations in order.</summary>
    public RefactorLog AddRange(IEnumerable<RefactorOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        foreach (var operation in operations)
        {
            Add(operation);
        }

        return this;
    }

    /// <summary>Renames a table. Names are unquoted identifiers.</summary>
    public RefactorLog RenameTable(string schema, string oldName, string newName) => Add(RefactorOperation.RenameTable(schema, oldName, newName));

    /// <summary>Renames a column of a table.</summary>
    public RefactorLog RenameColumn(string schema, string table, string oldName, string newName, bool isComputed = false) => Add(RefactorOperation.RenameColumn(schema, table, oldName, newName, isComputed));

    /// <summary>Renames an index of a table.</summary>
    public RefactorLog RenameIndex(string schema, string table, string oldName, string newName) => Add(RefactorOperation.RenameIndex(schema, table, oldName, newName));

    /// <summary>Renames a constraint of a table.</summary>
    public RefactorLog RenameConstraint(string schema, string table, RefactorConstraintKind kind, string oldName, string newName) => Add(RefactorOperation.RenameConstraint(schema, table, kind, oldName, newName));

    /// <summary>Moves a table to another schema.</summary>
    public RefactorLog MoveToSchema(string schema, string table, string newSchema) => Add(RefactorOperation.MoveToSchema(schema, table, newSchema));

    /// <summary>Loads an SSDT-style <c>.refactorlog</c> file.</summary>
    public static RefactorLog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses the XML of an SSDT-style <c>.refactorlog</c> file.</summary>
    public static RefactorLog Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new FormatException("The refactor log is empty.");
        if (!string.Equals(root.Name.LocalName, "Operations", StringComparison.Ordinal))
        {
            throw new FormatException($"Expected an <Operations> root element but found <{root.Name.LocalName}>.");
        }

        var ns = root.Name.Namespace;
        var log = new RefactorLog();
        foreach (var element in root.Elements(ns + "Operation"))
        {
            var name = element.Attribute("Name")?.Value ?? throw new FormatException("An <Operation> element has no Name attribute.");
            Guid? key = Guid.TryParse(element.Attribute("Key")?.Value, out var parsedKey) ? parsedKey : null;
            DateTime? changedAt = DateTime.TryParseExact(element.Attribute("ChangeDateTime")?.Value, ChangeDateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact)
                ? exact
                : DateTime.TryParse(element.Attribute("ChangeDateTime")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var loose) ? loose : null;
            var properties = element.Elements(ns + "Property")
                .Select(p => new KeyValuePair<string, string>(
                    p.Attribute("Name")?.Value ?? throw new FormatException("A <Property> element has no Name attribute."),
                    p.Attribute("Value")?.Value ?? string.Empty))
                .ToList();
            log.Add(new RefactorOperation(name, properties, key, changedAt));
        }

        return log;
    }

    /// <summary>Serializes the log in the DacFx <c>.refactorlog</c> format.</summary>
    public string ToXml()
    {
        XNamespace ns = Namespace;
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "Operations",
                new XAttribute("Version", "1.0"),
                _operations.Select(operation => new XElement(ns + "Operation",
                    new XAttribute("Name", operation.Name),
                    new XAttribute("Key", operation.Key.ToString("D")),
                    new XAttribute("ChangeDateTime", operation.ChangedAt.ToString(ChangeDateTimeFormat, CultureInfo.InvariantCulture)),
                    operation.Properties.Select(p => new XElement(ns + "Property",
                        new XAttribute("Name", p.Key),
                        new XAttribute("Value", p.Value)))))));

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
        {
            document.Save(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes the log as a <c>.refactorlog</c> file.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllText(path, ToXml(), new UTF8Encoding(false));
    }
}
