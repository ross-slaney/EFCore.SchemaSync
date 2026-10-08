using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EFCore.SchemaSync;

/// <summary>
/// Fluent API additions that record previous names in the EF model so EFCore.SchemaSync deploys a rename
/// (<c>sp_rename</c>, <c>ALTER SCHEMA ... TRANSFER</c>) instead of a drop-and-create that would lose data.
/// The annotations are harmless to keep: DacFx records each applied operation in <c>dbo.__RefactorLog</c>
/// and skips operations whose source object no longer exists (for example on a fresh database).
/// </summary>
public static class SchemaSyncModelBuilderExtensions
{
    /// <summary>Annotation holding the previous column name of a property.</summary>
    public const string PreviousColumnNameAnnotation = "SchemaSync:PreviousColumnName";

    /// <summary>Annotation holding the previous table name of an entity type.</summary>
    public const string PreviousTableNameAnnotation = "SchemaSync:PreviousTableName";

    /// <summary>Annotation holding the previous schema of an entity type's table.</summary>
    public const string PreviousSchemaAnnotation = "SchemaSync:PreviousSchema";

    /// <summary>Declares that the column of this property used to be called <paramref name="previousColumnName"/>.</summary>
    public static PropertyBuilder<TProperty> WasRenamedFrom<TProperty>(this PropertyBuilder<TProperty> builder, string previousColumnName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousColumnName);
        builder.HasAnnotation(PreviousColumnNameAnnotation, previousColumnName);
        return builder;
    }

    /// <summary>Declares that the column of this property used to be called <paramref name="previousColumnName"/>.</summary>
    public static PropertyBuilder WasRenamedFrom(this PropertyBuilder builder, string previousColumnName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousColumnName);
        builder.HasAnnotation(PreviousColumnNameAnnotation, previousColumnName);
        return builder;
    }

    /// <summary>
    /// Declares that the table of this entity type used to be called <paramref name="previousTableName"/>
    /// (optionally in <paramref name="previousSchema"/>). A different schema yields a schema move as well.
    /// </summary>
    public static EntityTypeBuilder<TEntity> WasRenamedFrom<TEntity>(this EntityTypeBuilder<TEntity> builder, string previousTableName, string? previousSchema = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousTableName);
        builder.HasAnnotation(PreviousTableNameAnnotation, previousTableName);
        if (previousSchema is not null)
        {
            builder.HasAnnotation(PreviousSchemaAnnotation, previousSchema);
        }

        return builder;
    }

    /// <summary>
    /// Declares that the table of this entity type used to be called <paramref name="previousTableName"/>
    /// (optionally in <paramref name="previousSchema"/>). A different schema yields a schema move as well.
    /// </summary>
    public static EntityTypeBuilder WasRenamedFrom(this EntityTypeBuilder builder, string previousTableName, string? previousSchema = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousTableName);
        builder.HasAnnotation(PreviousTableNameAnnotation, previousTableName);
        if (previousSchema is not null)
        {
            builder.HasAnnotation(PreviousSchemaAnnotation, previousSchema);
        }

        return builder;
    }

    /// <summary>Declares that the table of this entity type used to live in <paramref name="previousSchema"/> under the same name.</summary>
    public static EntityTypeBuilder<TEntity> WasMovedFromSchema<TEntity>(this EntityTypeBuilder<TEntity> builder, string previousSchema)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousSchema);
        builder.HasAnnotation(PreviousSchemaAnnotation, previousSchema);
        return builder;
    }

    /// <summary>Declares that the table of this entity type used to live in <paramref name="previousSchema"/> under the same name.</summary>
    public static EntityTypeBuilder WasMovedFromSchema(this EntityTypeBuilder builder, string previousSchema)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousSchema);
        builder.HasAnnotation(PreviousSchemaAnnotation, previousSchema);
        return builder;
    }
}
