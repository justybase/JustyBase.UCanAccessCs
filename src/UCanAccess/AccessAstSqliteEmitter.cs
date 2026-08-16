using JustyBase.NetezzaSqlParser.Ast;
using JustyBase.NetezzaSqlParser.Dialects;
using JustyBase.NetezzaSqlParser.Formatter;
using JustyBase.NetezzaSqlParser.Lexer;
using Superpower.Model;

namespace UCanAccess;

/// <summary>
/// Selective bridge from the shared Access AST to the provider translator.
///
/// The AST is deliberately used as a normalization and contract boundary only
/// for a small, semantics-preserving subset. Provider-specific rewrites (LIKE,
/// dates, parameters, money and crosstabs) remain owned by the legacy translator.
/// Unsupported or uncertain shapes return <c>false</c> and keep the old path.
/// </summary>
internal static class AccessAstSqliteEmitter
{
    public static bool IsCandidate(string accessSql)
        => TryGetSupportedStatement(accessSql, out _);

    public static bool TryTranslate(
        string accessSql,
        out string translated,
        out int parameterCount,
        out IReadOnlyList<string>? namedParameters,
        Func<string, bool>? isMoneyColumn,
        Func<string, bool>? isExactDecimalColumn,
        Func<string, bool>? isDateColumn)
    {
        translated = string.Empty;
        parameterCount = 0;
        namedParameters = null;

        if (!TryGetSupportedStatement(accessSql, out Statement? statement))
        {
            return false;
        }

        // Canonical Access text keeps the provider-specific semantic rewrites in
        // one place and makes the parser/translator lexical contract executable.
        string canonicalAccessSql = NzSqlFormatter.Format(statement!);
        translated = AccessSqlTranslator.TranslateLegacy(
            canonicalAccessSql,
            out parameterCount,
            out namedParameters,
            isMoneyColumn,
            isExactDecimalColumn,
            isDateColumn);
        return true;
    }

    private static bool TryGetSupportedStatement(string accessSql, out Statement? statement)
    {
        statement = null;
        if (string.IsNullOrWhiteSpace(accessSql))
        {
            return false;
        }

        try
        {
            Token<NzToken>[] tokens = DialectRuntime.Tokenize(accessSql, SqlDialect.Access).ToArray();
            var parser = DialectRuntime.CreateParser(tokens, SqlDialect.Access);
            statement = parser.Parse();

            // Do not normalize a partial parse or silently discard a second
            // statement. The existing translator remains the recovery path.
            if (statement is null || parser.Errors.Count != 0 || parser.Position < tokens.Length)
            {
                statement = null;
                return false;
            }

            if (!IsSupportedStatement(statement))
            {
                statement = null;
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            // Parser failures must not change provider behavior. The legacy
            // translator is intentionally the compatibility fallback.
            statement = null;
            return false;
        }
    }

    private static bool IsSupportedStatement(Statement statement)
        => statement switch
        {
            SelectStatement select => IsSupportedSelect(select, allowTop: true),
            AccessCrosstabStatement crosstab => IsSupportedCrosstab(crosstab),
            _ => false,
        };

    private static bool IsSupportedSelect(SelectStatement select, bool allowTop)
    {
        if (select.With is not null || select.HasInto || select.Limit is not null || select.OffsetFetch is not null
            || select.SetOperations is { Count: > 0 } || select.CompoundSelects is { Count: > 0 }
            || select.DistinctOn is { Count: > 0 } || select.SqliteWindowTokens is { Count: > 0 })
        {
            return false;
        }

        AccessQueryOptions? options = select.AccessOptions;
        if (options?.ExternalDatabase is not null)
        {
            return false;
        }

        if (!allowTop && options?.Top is not null)
        {
            return false;
        }

        if (options?.Top is { } top && (top.Percent || top.Count is not Literal { Kind: LiteralKind.Number }))
        {
            // This mirrors the provider's current TOP baseline. In particular,
            // TOP ... PERCENT must keep its existing NotSupportedException.
            return false;
        }

        if (select.SelectList.Count == 0 || select.SelectList.Any(item => !IsSupportedSelectItem(item)))
        {
            return false;
        }

        if (!IsSupportedFrom(select.From))
        {
            return false;
        }

        if (select.Where is not null && !IsSupportedExpression(select.Where))
        {
            return false;
        }

        if (select.GroupBy is not null && select.GroupBy.Any(item => !IsSupportedExpression(item)))
        {
            return false;
        }

        if (select.Having is not null && !IsSupportedExpression(select.Having))
        {
            return false;
        }

        if (select.OrderBy is not null && select.OrderBy.Any(item =>
                item.NullsFirst || !IsSupportedExpression(item.Expression)))
        {
            return false;
        }

        return true;
    }

    private static bool IsSupportedCrosstab(AccessCrosstabStatement crosstab)
    {
        if (crosstab.AccessOptions?.WithOwnerAccessOption == false
            || crosstab.InValues is not { Count: > 0 }
            || crosstab.TransformItems.Count == 0
            || crosstab.TransformItems.Any(item => !IsSupportedSelectItem(item))
            || !IsSupportedExpression(crosstab.PivotExpression)
            || crosstab.InValues.Any(value => !IsSupportedExpression(value)))
        {
            return false;
        }

        // CrosstabTranslator has its own deliberately narrow grammar. Avoid
        // feeding it joins, subqueries, TOP or ORDER BY that it cannot preserve.
        return IsSupportedSelect(crosstab.RowQuery, allowTop: false)
            && crosstab.RowQuery.OrderBy is not { Count: > 0 };
    }

    private static bool IsSupportedSelectItem(SelectItem item)
        => IsSupportedExpression(item.Expression)
            && (item.Alias is null || IsPlainIdentifier(item.Alias));

    private static bool IsSupportedFrom(IReadOnlyList<TableReference>? from)
    {
        if (from is null)
        {
            return true;
        }

        foreach (TableReference reference in from)
        {
            if (reference.Joins is { Count: > 0 } || reference.Applies is { Count: > 0 })
            {
                return false;
            }

            TableSource source = reference.Source;
            if (source.Table is null || source.Subquery is not null || source.FunctionSource
                || source.Lateral || source.TableFunction is not null)
            {
                return false;
            }

            if (source.Alias is not null && !IsPlainIdentifier(source.Alias))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSupportedExpression(Expression expression, int parentPrecedence = 0, bool rightChild = false)
    {
        switch (expression)
        {
            case Literal literal:
                return literal.Kind is LiteralKind.Number or LiteralKind.String or LiteralKind.Date
                    or LiteralKind.Null or LiteralKind.BooleanTrue or LiteralKind.BooleanFalse;

            case ColumnReference column:
                return column.Name.Length > 0;

            case StarExpression star:
                return star.Qualifier is null || IsPlainIdentifier(star.Qualifier);

            case ParameterExpression parameter:
                return parameter.Name is null || IsParameterName(parameter.Name);

            case FunctionCall function:
                return (function.Schema is null || IsPlainIdentifier(function.Schema))
                    && IsPlainIdentifier(function.Name)
                    && function.Filter is null
                    && function.Over is null
                    && function.WithinGroup is null
                    && (function.Arguments is null
                        || function.Arguments.All(argument => IsSupportedExpression(argument)));

            case BinaryExpression binary:
                if (binary.Operator is BinaryOperator.NotLike or BinaryOperator.NotIlike)
                {
                    // Keep NOT LIKE on the compatibility path until the AST
                    // formatter and provider rewrite share its negation shape.
                    return false;
                }

                int precedence = BinaryPrecedence(binary.Operator);
                if (precedence == 0 || (parentPrecedence > 0 &&
                    (precedence < parentPrecedence
                        || (precedence == parentPrecedence && rightChild && !IsAssociative(binary.Operator)))))
                {
                    return false;
                }

                return IsSupportedExpression(binary.Left, precedence)
                    && IsSupportedExpression(binary.Right, precedence, rightChild: true);

            case UnaryExpression unary:
                return unary.Operand is not BinaryExpression
                    && unary.Operand is not BetweenExpression
                    && IsSupportedExpression(unary.Operand);

            case InExpression inExpression:
                return inExpression.Subquery is null
                    && inExpression.Values is { Count: > 0 }
                    && IsSupportedExpression(inExpression.Left)
                    && inExpression.Values.All(value => IsSupportedExpression(value));

            case BetweenExpression between:
                return IsSupportedExpression(between.Value)
                    && IsSupportedExpression(between.Low)
                    && IsSupportedExpression(between.High);

            case CaseExpression caseExpression:
                return (caseExpression.Value is null || IsSupportedExpression(caseExpression.Value))
                    && caseExpression.WhenClauses.All(clause =>
                        IsSupportedExpression(clause.When) && IsSupportedExpression(clause.Then))
                    && (caseExpression.ElseClause is null || IsSupportedExpression(caseExpression.ElseClause));

            case CastExpression cast:
                return IsSupportedExpression(cast.Expression);

            case IsExpression isExpression:
                return IsSupportedExpression(isExpression.Left);

            default:
                return false;
        }
    }

    private static int BinaryPrecedence(BinaryOperator op)
        => op switch
        {
            BinaryOperator.Or => 1,
            BinaryOperator.And => 2,
            BinaryOperator.Equals or BinaryOperator.NotEquals or BinaryOperator.LessThan
                or BinaryOperator.GreaterThan or BinaryOperator.LessThanEquals
                or BinaryOperator.GreaterThanEquals or BinaryOperator.Like or BinaryOperator.Ilike
                or BinaryOperator.NotLike or BinaryOperator.NotIlike or BinaryOperator.In
                or BinaryOperator.NotIn or BinaryOperator.Between or BinaryOperator.NotBetween
                or BinaryOperator.Is or BinaryOperator.IsNot => 3,
            BinaryOperator.Plus or BinaryOperator.Minus or BinaryOperator.Concat => 4,
            BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo => 5,
            _ => 0,
        };

    private static bool IsAssociative(BinaryOperator op)
        => op is BinaryOperator.And or BinaryOperator.Or;

    private static bool IsPlainIdentifier(string value)
    {
        if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        for (int i = 1; i < value.Length; i++)
        {
            if (!(char.IsLetterOrDigit(value[i]) || value[i] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsParameterName(string value)
    {
        if (value.Length > 1 && value[0] is '@' or ':' or '$')
        {
            return IsPlainIdentifier(value[1..]);
        }

        return IsPlainIdentifier(value);
    }
}
