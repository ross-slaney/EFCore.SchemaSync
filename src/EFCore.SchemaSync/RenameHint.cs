namespace EFCore.SchemaSync;

/// <summary>What a <see cref="RenameHint"/> suggests was renamed.</summary>
public enum RenameHintKind
{
    /// <summary>A column of a table that exists in both the database and the model.</summary>
    Column,

    /// <summary>A table: the model maps a table the database lacks and the database holds a table the model lacks, with the same columns.</summary>
    Table,
}

/// <summary>
/// A change that looks like an undeclared rename. The library never applies renames on its own; a hint only
/// shows the line to add so the next deployment renames in place and keeps the data.
/// </summary>
/// <param name="Kind">Column or table.</param>
/// <param name="OldName">Qualified name in the database, for example <c>[dbo].[Customers].[Phone]</c>.</param>
/// <param name="NewName">Qualified name in the model, for example <c>[dbo].[Customers].[PhoneNumber]</c>.</param>
/// <param name="Suggestion">The EF Core configuration that declares the rename, for example
/// <c>modelBuilder.Entity&lt;Customer&gt;().Property(e =&gt; e.PhoneNumber).WasRenamedFrom("Phone");</c>.</param>
public sealed record RenameHint(RenameHintKind Kind, string OldName, string NewName, string Suggestion)
{
    /// <inheritdoc />
    public override string ToString() => $"{OldName} -> {NewName}: {Suggestion}";
}
