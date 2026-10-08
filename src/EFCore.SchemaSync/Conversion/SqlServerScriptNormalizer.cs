using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace EFCore.SchemaSync.Conversion;

/// <summary>
/// Turns the script EF Core generates for SQL Server (<c>GenerateCreateScript()</c>) into declarative statements a
/// DacFx model accepts, using the ScriptDom syntax tree rather than text matching:
/// <list type="bullet">
/// <item>CREATE SCHEMA/TABLE/INDEX/SEQUENCE and ALTER TABLE ... ADD CONSTRAINT pass through (with canonicalized expressions).</item>
/// <item><c>IF SCHEMA_ID(...) IS NULL EXEC(N'CREATE SCHEMA ...')</c> becomes <c>CREATE SCHEMA ...</c>.</item>
/// <item>Temporal tables (<c>DECLARE @historyTableSchema ...; EXEC(N'CREATE TABLE ... ' + @historyTableSchema + ...)</c>) are resolved into the CREATE TABLE statement.</item>
/// <item>Comments (<c>EXEC sp_addextendedproperty</c> driven by variables) become declarative extended properties.</item>
/// <item>Seed data (<c>HasData</c>: IDENTITY_INSERT guards and INSERTs) is skipped with a warning; the library deploys schema only.</item>
/// <item>Everything else is rejected explicitly through <see cref="UnsupportedSchemaException"/>; nothing is dropped silently.</item>
/// </list>
/// </summary>
internal static class SqlServerScriptNormalizer
{
    private const string DefaultSchema = "dbo";

    public static NormalizedScript Normalize(string createScript)
    {
        ArgumentNullException.ThrowIfNull(createScript);

        var script = Parse(createScript, "the EF Core create script");
        var state = new State();
        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                state.Process(statement, createScript, nested: false);
            }
        }

        if (state.Unsupported.Count > 0)
        {
            throw new UnsupportedSchemaException(state.Unsupported);
        }

        var warnings = new List<string>();
        if (state.SkippedSeedStatements > 0)
        {
            warnings.Add($"Model seed data (HasData) is not deployed by EFCore.SchemaSync: {state.SkippedSeedStatements} INSERT statement(s) in the EF create script were skipped. Seed data separately.");
        }

        return new NormalizedScript(state.Statements, warnings, state.SkippedSeedStatements);
    }

    private static TSqlScript Parse(string text, string what)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(text))
        {
            fragment = parser.Parse(reader, out errors);
        }

        if (errors.Count > 0)
        {
            var details = string.Join("; ", errors.Select(e => $"line {e.Line}, column {e.Column}: {e.Message}"));
            throw new SchemaSyncException(SchemaSyncStage.ConvertModel, $"ScriptDom could not parse {what}: {details}");
        }

        return (TSqlScript)fragment;
    }

    private sealed class State
    {
        private readonly Dictionary<string, string?> _variables = new(StringComparer.OrdinalIgnoreCase);
        private int _counter;

        public List<NormalizedStatement> Statements { get; } = [];

        public List<string> Unsupported { get; } = [];

        public int SkippedSeedStatements { get; private set; }

        public void Process(TSqlStatement statement, string source, bool nested)
        {
            switch (statement)
            {
                case CreateSchemaStatement schema:
                    Add("Schema", schema.Name.Value, ExpressionCanonicalizer.Slice(schema, source));
                    break;
                case CreateTableStatement table:
                    ProcessCreateTable(table, source);
                    break;
                case CreateIndexStatement index:
                    ProcessCreateIndex(index, source);
                    break;
                case CreateSequenceStatement sequence:
                    Add("Sequence", Name(sequence.Name), ExpressionCanonicalizer.Slice(sequence, source));
                    break;
                case AlterTableAddTableElementStatement alter:
                    ProcessAlterTableAdd(alter, source);
                    break;
                case IfStatement conditional when !nested:
                    ProcessIf(conditional, source);
                    break;
                case ExecuteStatement execute when !nested:
                    ProcessExecute(execute, source);
                    break;
                case DeclareVariableStatement declare when !nested:
                    ProcessDeclare(declare, source);
                    break;
                case SetVariableStatement set when !nested:
                    ProcessSet(set, source);
                    break;
                case InsertStatement when !nested:
                    SkippedSeedStatements++;
                    break;
                default:
                    Unsupported.Add($"{Describe(statement)}: {Snippet(statement, source)}");
                    break;
            }
        }

        private void ProcessCreateTable(CreateTableStatement table, string source)
        {
            var name = Name(table.SchemaObjectName);
            var problems = new List<string>();

            if (table.AsFileTable)
            {
                problems.Add("FileTable");
            }

            if (table.AsEdge || table.AsNode)
            {
                problems.Add("graph (AS NODE/AS EDGE) table");
            }

            if (table.SelectStatement is not null)
            {
                problems.Add("CREATE TABLE ... AS SELECT");
            }

            if (table.OnFileGroupOrPartitionScheme is not null || table.TextImageOn is not null || table.FileStreamOn is not null)
            {
                problems.Add("explicit filegroup, partition scheme, TEXTIMAGE_ON or FILESTREAM_ON placement");
            }

            foreach (var option in table.Options)
            {
                switch (option)
                {
                    case SystemVersioningTableOption:
                        break; // temporal tables are supported declaratively by DacFx
                    case MemoryOptimizedTableOption:
                        problems.Add("memory-optimized table (IsMemoryOptimized). It requires a MEMORY_OPTIMIZED_DATA filegroup, which is database-level configuration EFCore.SchemaSync does not manage");
                        break;
                    default:
                        problems.Add($"table option {option.OptionKind}");
                        break;
                }
            }

            foreach (var column in table.Definition.ColumnDefinitions)
            {
                var columnName = column.ColumnIdentifier.Value;
                if (column.Encryption is not null)
                {
                    problems.Add($"Always Encrypted column [{columnName}]");
                }

                if (column.IsMasked)
                {
                    problems.Add($"dynamic data masking on column [{columnName}]");
                }
            }

            if (problems.Count > 0)
            {
                Unsupported.Add($"Table {name}: {string.Join("; ", problems)}");
                return;
            }

            Add("Table", name, ExpressionCanonicalizer.RewriteStatement(table, source));
        }

        private void ProcessCreateIndex(CreateIndexStatement index, string source)
        {
            var name = $"{Name(index.OnName)}.{index.Name.Value}";
            if (index.OnFileGroupOrPartitionScheme is not null || index.FileStreamOn is not null)
            {
                Unsupported.Add($"Index {name}: explicit filegroup, partition scheme or FILESTREAM_ON placement");
                return;
            }

            Add("Index", name, ExpressionCanonicalizer.RewriteStatement(index, source));
        }

        private void ProcessAlterTableAdd(AlterTableAddTableElementStatement alter, string source)
        {
            var name = Name(alter.SchemaObjectName);
            if (alter.Definition.ColumnDefinitions.Count > 0 || alter.Definition.Indexes.Count > 0 || alter.Definition.TableConstraints.Count == 0)
            {
                Unsupported.Add($"ALTER TABLE {name} ADD with columns or indexes (only constraints are expected here): {Snippet(alter, source)}");
                return;
            }

            var constraints = string.Join("+", alter.Definition.TableConstraints.Select(c => c.ConstraintIdentifier?.Value ?? c.GetType().Name));
            Add("Constraint", $"{name}.{constraints}", ExpressionCanonicalizer.RewriteStatement(alter, source));
        }

        private void ProcessIf(IfStatement conditional, string source)
        {
            // EF HasData: IF EXISTS (identity column) SET IDENTITY_INSERT ... ON/OFF. Seed data is not deployed.
            if (conditional.ThenStatement is SetIdentityInsertStatement && conditional.ElseStatement is null)
            {
                return;
            }

            // EF schema creation: IF SCHEMA_ID(N'x') IS NULL EXEC(N'CREATE SCHEMA [x];');
            if (conditional.ElseStatement is null
                && conditional.ThenStatement is ExecuteStatement { ExecuteSpecification.ExecutableEntity: ExecutableStringList strings }
                && TryResolveDynamicSql(strings, out var sql))
            {
                var inner = TryParseInner(sql, conditional, source);
                if (inner is not null && inner.All(s => s is CreateSchemaStatement))
                {
                    foreach (var statement in inner)
                    {
                        Process(statement, sql, nested: true);
                    }

                    return;
                }
            }

            Unsupported.Add($"Conditional (IF) statement that cannot be expressed declaratively: {Snippet(conditional, source)}");
        }

        private void ProcessExecute(ExecuteStatement execute, string source)
        {
            switch (execute.ExecuteSpecification.ExecutableEntity)
            {
                case ExecutableStringList strings:
                {
                    // Temporal tables: EXEC(N'CREATE TABLE ... HISTORY_TABLE = ' + @historyTableSchema + N'.[History]))');
                    if (!TryResolveDynamicSql(strings, out var sql))
                    {
                        Unsupported.Add($"Dynamic SQL whose text depends on run-time values: {Snippet(execute, source)}");
                        return;
                    }

                    var inner = TryParseInner(sql, execute, source);
                    if (inner is null)
                    {
                        return;
                    }

                    foreach (var statement in inner)
                    {
                        Process(statement, sql, nested: true);
                    }

                    return;
                }

                case ExecutableProcedureReference procedure when IsProcedure(procedure, "sp_addextendedproperty"):
                    ProcessExtendedProperty(procedure, execute, source);
                    return;
                case ExecutableProcedureReference procedure:
                    Unsupported.Add($"Stored procedure call {Name(procedure.ProcedureReference.ProcedureReference.Name)}: {Snippet(execute, source)}");
                    return;
                default:
                    Unsupported.Add($"EXECUTE statement: {Snippet(execute, source)}");
                    return;
            }
        }

        private void ProcessDeclare(DeclareVariableStatement declare, string source)
        {
            foreach (var declaration in declare.Declarations)
            {
                _variables[declaration.VariableName.Value] = declaration.Value is null ? null : EvaluateConstant(declaration.Value, source);
            }
        }

        private void ProcessSet(SetVariableStatement set, string source)
        {
            if (set.AssignmentKind != AssignmentKind.Equals || set.Expression is null)
            {
                Unsupported.Add($"Variable assignment: {Snippet(set, source)}");
                return;
            }

            _variables[set.Variable.Name] = EvaluateConstant(set.Expression, source);
        }

        private void ProcessExtendedProperty(ExecutableProcedureReference procedure, ExecuteStatement execute, string source)
        {
            string[] parameterNames = ["@name", "@value", "@level0type", "@level0name", "@level1type", "@level1name", "@level2type", "@level2name"];
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < procedure.Parameters.Count; i++)
            {
                var parameter = procedure.Parameters[i];
                var parameterName = parameter.Variable?.Name ?? (i < parameterNames.Length ? parameterNames[i] : null);
                if (parameterName is null || !parameterNames.Contains(parameterName, StringComparer.OrdinalIgnoreCase))
                {
                    Unsupported.Add($"sp_addextendedproperty with unexpected parameter #{i + 1}: {Snippet(execute, source)}");
                    return;
                }

                if (parameter.ParameterValue is NullLiteral)
                {
                    values[parameterName] = null;
                    continue;
                }

                var value = EvaluateConstant(parameter.ParameterValue, source);
                if (value is null)
                {
                    Unsupported.Add($"sp_addextendedproperty with a non-constant {parameterName}: {Snippet(execute, source)}");
                    return;
                }

                values[parameterName] = value;
            }

            if (!values.TryGetValue("@name", out var propertyName) || propertyName is null)
            {
                Unsupported.Add($"sp_addextendedproperty without a property name: {Snippet(execute, source)}");
                return;
            }

            var builder = new StringBuilder("EXECUTE sp_addextendedproperty");
            var first = true;
            foreach (var name in parameterNames)
            {
                if (!values.TryGetValue(name, out var value) || value is null)
                {
                    continue;
                }

                builder.Append(first ? " " : ", ").Append(name).Append(" = N'").Append(value.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
                first = false;
            }

            builder.Append(';');
            var target = string.Join(".", parameterNames.Skip(3).Where((n, i) => i % 2 == 0).Select(n => values.GetValueOrDefault(n)).Where(v => v is not null));
            Add("ExtendedProperty", $"{target}.{propertyName}", builder.ToString());
        }

        private bool TryResolveDynamicSql(ExecutableStringList strings, out string sql)
        {
            var builder = new StringBuilder();
            foreach (var part in strings.Strings)
            {
                switch (part)
                {
                    case StringLiteral literal:
                        builder.Append(literal.Value);
                        break;
                    case VariableReference variable when _variables.TryGetValue(variable.Name, out var value) && value is not null:
                        builder.Append(value);
                        break;
                    default:
                        sql = string.Empty;
                        return false;
                }
            }

            sql = builder.ToString();
            return true;
        }

        private IList<TSqlStatement>? TryParseInner(string sql, TSqlStatement outer, string source)
        {
            try
            {
                return Parse(sql, "dynamic SQL in the EF Core create script").Batches.SelectMany(b => b.Statements).ToList();
            }
            catch (SchemaSyncException ex)
            {
                Unsupported.Add($"{ex.Message} in: {Snippet(outer, source)}");
                return null;
            }
        }

        private string? EvaluateConstant(ScalarExpression expression, string source)
        {
            switch (expression)
            {
                case StringLiteral literal:
                    return literal.Value;
                case ParenthesisExpression parenthesis:
                    return EvaluateConstant(parenthesis.Expression, source);
                case VariableReference variable:
                    return _variables.GetValueOrDefault(variable.Name);
                case FunctionCall { CallTarget: null, Parameters.Count: 0 } function when IsFunction(function, "SCHEMA_NAME"):
                    return DefaultSchema;
                case FunctionCall { CallTarget: null, Parameters.Count: 1 } function when IsFunction(function, "QUOTENAME")
                                                                                             && function.Parameters[0] is FunctionCall { CallTarget: null, Parameters.Count: 0 } inner
                                                                                             && IsFunction(inner, "SCHEMA_NAME"):
                    return "[" + DefaultSchema + "]";
                case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } concat:
                {
                    var left = EvaluateConstant(concat.FirstExpression, source);
                    var right = EvaluateConstant(concat.SecondExpression, source);
                    return left is null || right is null ? null : left + right;
                }

                default:
                    Unsupported.Add($"Expression that cannot be evaluated at conversion time: {Snippet(expression, source)}");
                    return null;
            }
        }

        private void Add(string kind, string objectName, string text)
        {
            var safeName = new string(objectName.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '.' ? c : '_').ToArray()).Trim('.');
            Statements.Add(new NormalizedStatement($"{++_counter:000}_{kind}_{safeName}.sql", kind, text.Trim()));
        }

        private static bool IsProcedure(ExecutableProcedureReference procedure, string name)
            => string.Equals(procedure.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value, name, StringComparison.OrdinalIgnoreCase);

        private static bool IsFunction(FunctionCall function, string name)
            => string.Equals(function.FunctionName?.Value, name, StringComparison.OrdinalIgnoreCase);

        private static string Name(SchemaObjectName name)
            => string.Join(".", name.Identifiers.Select(i => "[" + i.Value + "]"));

        private static string Describe(TSqlStatement statement) => statement switch
        {
            CreateViewStatement => "View definition (EF maps views but does not create them; create the view outside EFCore.SchemaSync)",
            CreateProcedureStatement or CreateOrAlterProcedureStatement => "Stored procedure definition",
            CreateFunctionStatement or CreateOrAlterFunctionStatement => "Function definition",
            CreateTriggerStatement or CreateOrAlterTriggerStatement => "Trigger definition",
            AlterDatabaseStatement => "ALTER DATABASE (database-level configuration is not managed)",
            InsertStatement => "INSERT statement",
            IfStatement => "Conditional (IF) statement",
            ExecuteStatement => "EXECUTE statement",
            DeclareVariableStatement => "Variable declaration",
            SetVariableStatement => "Variable assignment",
            _ => statement.GetType().Name.Replace("Statement", " statement", StringComparison.Ordinal),
        };

        private static string Snippet(TSqlFragment fragment, string source)
        {
            var text = ExpressionCanonicalizer.Slice(fragment, source).Replace("\r", string.Empty).Replace('\n', ' ');
            while (text.Contains("  ", StringComparison.Ordinal))
            {
                text = text.Replace("  ", " ", StringComparison.Ordinal);
            }

            return text.Length <= 160 ? text : text[..160] + "...";
        }
    }
}
