using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace EFCore.SchemaSync.Conversion;

/// <summary>
/// Rewrites the expressions inside table, index and constraint definitions (computed columns, defaults, check
/// constraints, index filters) into the form SQL Server itself stores in the catalog, so that DacFx sees no
/// difference when the deployed schema is compared with the model again. SQL Server normalizes expression text
/// when it stores them (CAST becomes CONVERT with a bracketed type, numeric literals are parenthesized, float
/// literals are written in 17-digit scientific notation, IN lists become OR chains, ...). DacFx tolerates
/// whitespace, casing and most parentheses, but not those rewrites. Everything here is derived from the
/// ScriptDom syntax tree; the original text of every fragment that needs no rewrite is preserved verbatim.
/// </summary>
internal static class ExpressionCanonicalizer
{
    /// <summary>Returns the statement text with every expression inside it canonicalized.</summary>
    public static string RewriteStatement(TSqlFragment statement, string source)
    {
        var roots = new ExpressionRootCollector();
        statement.Accept(roots);

        var text = Slice(statement, source);
        foreach (var root in roots.Roots.OrderByDescending(r => r.Node.StartOffset))
        {
            var replacement = Rewrite(root.Node, source, root.AllowPredicateRewrites);
            if (root.WrapInParentheses && root.Node is not ParenthesisExpression)
            {
                // SQL Server stores computed column and default expressions wrapped in parentheses, and DacFx
                // treats that outer pair as significant for simple expressions such as CONVERT([int], [Col]).
                replacement = "(" + replacement + ")";
            }

            text = Splice(text, statement.StartOffset, root.Node, replacement);
        }

        return text;
    }

    /// <summary>Canonicalizes one expression fragment (recursively).</summary>
    internal static string Rewrite(TSqlFragment node, string source, bool allowPredicateRewrites)
    {
        var collector = new CandidateCollector(allowPredicateRewrites);
        node.Accept(collector);

        var text = Slice(node, source);
        foreach (var candidate in collector.Candidates.OrderByDescending(c => c.StartOffset))
        {
            var replacement = Replace(candidate, source, allowPredicateRewrites);
            text = Splice(text, node.StartOffset, candidate, replacement);
        }

        return text;
    }

    private static string Replace(TSqlFragment candidate, string source, bool allowPredicateRewrites)
    {
        string R(TSqlFragment child) => Rewrite(child, source, allowPredicateRewrites);

        switch (candidate)
        {
            case CastCall cast:
                return $"CONVERT({TypeText(cast.DataType, source)}, {R(cast.Parameter)})";
            case TryCastCall tryCast:
                // Unlike CAST, SQL Server keeps TRY_CAST as TRY_CAST (with a bracketed type) in the catalog.
                return $"TRY_CAST({R(tryCast.Parameter)} AS {TypeText(tryCast.DataType, source)})";
            case ConvertCall convert:
                return $"CONVERT({TypeText(convert.DataType, source)}, {R(convert.Parameter)}{(convert.Style is null ? string.Empty : ", " + R(convert.Style))})";
            case TryConvertCall tryConvert:
                return $"TRY_CONVERT({TypeText(tryConvert.DataType, source)}, {R(tryConvert.Parameter)}{(tryConvert.Style is null ? string.Empty : ", " + R(tryConvert.Style))})";
            case Literal literal:
                return "(" + CanonicalLiteral(literal, negative: false) + ")";
            case UnaryExpression { Expression: Literal inner } unary:
                return unary.UnaryExpressionType switch
                {
                    UnaryExpressionType.Negative => "(" + CanonicalLiteral(inner, negative: true) + ")",
                    UnaryExpressionType.Positive => "(" + CanonicalLiteral(inner, negative: false) + ")",
                    _ => Slice(unary, source),
                };
            case ParenthesisExpression { Expression: Literal inner }:
                return "(" + CanonicalLiteral(inner, negative: false) + ")";
            case ParenthesisExpression { Expression: UnaryExpression { Expression: Literal inner } unary }:
                return Replace(unary, source, allowPredicateRewrites);
            case InPredicate inPredicate:
                return RewriteIn(inPredicate, R);
            case BooleanTernaryExpression ternary:
                return RewriteBetween(ternary, R);
            case BooleanNotExpression not:
                return "NOT " + (not.Expression is BooleanParenthesisExpression { Expression: var operand } && IsSimplePredicate(operand) ? R(operand) : R(not.Expression));
            case FunctionCall function:
                return $"DATEPART({function.FunctionName.Value.ToLowerInvariant()}, {R(function.Parameters[0])})";
            case IIfCall iif:
                return $"CASE WHEN {R(iif.Predicate)} THEN {R(iif.ThenExpression)} ELSE {R(iif.ElseExpression)} END";
            default:
                return Slice(candidate, source);
        }
    }

    private static string RewriteIn(InPredicate inPredicate, Func<TSqlFragment, string> rewrite)
    {
        // SQL Server stores `x IN (a, b, c)` as `x=c OR x=b OR x=a` (reversed) and NOT IN as `NOT (...)`.
        var subject = rewrite(inPredicate.Expression);
        var chain = string.Join(" OR ", inPredicate.Values.Reverse().Select(v => $"{subject}={rewrite(v)}"));
        return inPredicate.NotDefined ? $"NOT ({chain})" : $"({chain})";
    }

    private static string RewriteBetween(BooleanTernaryExpression ternary, Func<TSqlFragment, string> rewrite)
    {
        var subject = rewrite(ternary.FirstExpression);
        var chain = $"({subject}>={rewrite(ternary.SecondExpression)} AND {subject}<={rewrite(ternary.ThirdExpression)})";
        return ternary.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween ? $"NOT {chain}" : chain;
    }

    private static bool IsSimplePredicate(BooleanExpression expression)
        => expression is BooleanComparisonExpression or BooleanIsNullExpression or LikePredicate or InPredicate or BooleanTernaryExpression or ExistsPredicate;

    private static string CanonicalLiteral(Literal literal, bool negative)
    {
        var sign = negative ? "-" : string.Empty;
        switch (literal)
        {
            case RealLiteral real:
                return sign + FormatReal(real.Value);
            case IntegerLiteral integer:
                return sign + FormatInteger(integer.Value, negative);
            case NumericLiteral numeric:
                // ScriptDom classifies integers beyond the int range as numeric literals.
                return sign + (numeric.Value.All(char.IsAsciiDigit) ? FormatInteger(numeric.Value, negative) : numeric.Value);
            case MoneyLiteral money:
                return sign + money.Value;
            default:
                return sign + literal.Value;
        }
    }

    /// <summary>SQL Server stores float literals as 17 significant digits in scientific notation with a 3-digit exponent.</summary>
    internal static string FormatReal(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return value;
        }

        // "E16" yields 17 significant digits and a signed three-digit exponent, exactly like SQL Server's printf("%.16e").
        return parsed.ToString("E16", CultureInfo.InvariantCulture).Replace('E', 'e');
    }

    /// <summary>Integer literals outside the int range are numeric(p,0) literals; SQL Server stores them with a trailing period.</summary>
    internal static string FormatInteger(string value, bool negative)
    {
        if (!BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude))
        {
            return value;
        }

        var limit = negative ? new BigInteger(int.MaxValue) + 1 : new BigInteger(int.MaxValue);
        return magnitude <= limit ? value : value + ".";
    }

    private static string TypeText(DataTypeReference dataType, string source)
    {
        switch (dataType)
        {
            case SqlDataTypeReference sql:
            {
                var name = sql.Name?.BaseIdentifier?.Value ?? sql.SqlDataTypeOption.ToString().ToLowerInvariant();
                return "[" + name + "]" + Parameters(sql.Parameters);
            }

            case UserDataTypeReference user:
                return string.Join(".", user.Name.Identifiers.Select(i => "[" + i.Value + "]")) + Parameters(user.Parameters);
            default:
                return Slice(dataType, source);
        }
    }

    private static string Parameters(IList<Literal> parameters)
        => parameters.Count == 0 ? string.Empty : "(" + string.Join(",", parameters.Select(p => p is MaxLiteral ? "max" : p.Value)) + ")";

    internal static string Slice(TSqlFragment fragment, string source) => source.Substring(fragment.StartOffset, fragment.FragmentLength);

    private static string Splice(string text, int textStartOffset, TSqlFragment fragment, string replacement)
    {
        var relative = fragment.StartOffset - textStartOffset;
        return string.Concat(text.AsSpan(0, relative), replacement, text.AsSpan(relative + fragment.FragmentLength));
    }

    /// <summary>Finds the expression roots of a statement: computed columns, defaults, check conditions, index filters.</summary>
    private sealed class ExpressionRootCollector : TSqlFragmentVisitor
    {
        public List<(TSqlFragment Node, bool AllowPredicateRewrites, bool WrapInParentheses)> Roots { get; } = [];

        public override void ExplicitVisit(ColumnDefinition node)
        {
            if (node.ComputedColumnExpression is not null)
            {
                Roots.Add((node.ComputedColumnExpression, true, true));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DefaultConstraintDefinition node)
        {
            if (node.Expression is not null)
            {
                Roots.Add((node.Expression, true, true));
            }
        }

        public override void ExplicitVisit(CheckConstraintDefinition node)
        {
            if (node.CheckCondition is not null)
            {
                Roots.Add((node.CheckCondition, true, false));
            }
        }

        public override void ExplicitVisit(CreateIndexStatement node)
        {
            if (node.FilterPredicate is not null)
            {
                // Filtered index predicates keep IN lists; only literals are parenthesized.
                Roots.Add((node.FilterPredicate, false, false));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(IndexDefinition node)
        {
            if (node.FilterPredicate is not null)
            {
                Roots.Add((node.FilterPredicate, false, false));
            }

            base.ExplicitVisit(node);
        }
    }

    /// <summary>Collects the outermost fragments inside an expression that need rewriting; nested ones are handled recursively.</summary>
    private sealed class CandidateCollector(bool allowPredicateRewrites) : TSqlFragmentVisitor
    {
        private static readonly HashSet<string> DatePartFunctions = new(StringComparer.OrdinalIgnoreCase) { "YEAR", "MONTH", "DAY" };

        public List<TSqlFragment> Candidates { get; } = [];

        public override void ExplicitVisit(CastCall node) => Candidates.Add(node);

        public override void ExplicitVisit(TryCastCall node) => Candidates.Add(node);

        public override void ExplicitVisit(ConvertCall node) => Candidates.Add(node);

        public override void ExplicitVisit(TryConvertCall node) => Candidates.Add(node);

        public override void ExplicitVisit(IntegerLiteral node) => Candidates.Add(node);

        public override void ExplicitVisit(NumericLiteral node) => Candidates.Add(node);

        public override void ExplicitVisit(RealLiteral node) => Candidates.Add(node);

        public override void ExplicitVisit(MoneyLiteral node) => Candidates.Add(node);

        public override void ExplicitVisit(IIfCall node) => Candidates.Add(node);

        public override void ExplicitVisit(BooleanNotExpression node) => Candidates.Add(node);

        public override void ExplicitVisit(UnaryExpression node)
        {
            if (IsNumeric(node.Expression))
            {
                Candidates.Add(node);
                return;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ParenthesisExpression node)
        {
            if (IsNumeric(node.Expression) || (node.Expression is UnaryExpression unary && IsNumeric(unary.Expression)))
            {
                Candidates.Add(node);
                return;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is null && node.Parameters.Count == 1 && DatePartFunctions.Contains(node.FunctionName.Value))
            {
                Candidates.Add(node);
                return;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InPredicate node)
        {
            if (allowPredicateRewrites && node.Subquery is null && node.Values.Count > 0)
            {
                Candidates.Add(node);
                return;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BooleanTernaryExpression node)
        {
            if (allowPredicateRewrites)
            {
                Candidates.Add(node);
                return;
            }

            base.ExplicitVisit(node);
        }

        private static bool IsNumeric(ScalarExpression? expression)
            => expression is IntegerLiteral or NumericLiteral or RealLiteral or MoneyLiteral;
    }
}
