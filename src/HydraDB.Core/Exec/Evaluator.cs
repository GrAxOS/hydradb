using HydraDB.Core.Catalog;
using HydraDB.Core.Sql;

namespace HydraDB.Core.Exec;

/// <summary>
/// Three-valued-ish predicate evaluation: NULL never satisfies a comparison except
/// through the inequality operator, mirroring the practical subset of SQL semantics
/// this engine claims to support.
/// </summary>
public static class Evaluator
{
    public static object? Eval(Expr expr, TableDef def, object?[] row)
    {
        switch (expr)
        {
            case Literal literal:
                return literal.Value;

            case ColumnRef column:
            {
                int index = def.IndexOf(column.Name);
                if (index < 0)
                    throw new SqlException($"unknown column '{column.Name}' in table '{def.Name}'");
                return row[index];
            }

            case Unary unary when unary.Op == "not":
                return !Truthy(Eval(unary.Operand, def, row));

            case Binary binary:
                return EvalBinary(binary, def, row);

            default:
                throw new SqlException("unsupported expression");
        }
    }

    public static bool Truthy(object? value) => value is bool flag && flag;

    private static object? EvalBinary(Binary binary, TableDef def, object?[] row)
    {
        if (binary.Op == "and")
            return Truthy(Eval(binary.Left, def, row)) && Truthy(Eval(binary.Right, def, row));

        if (binary.Op == "or")
            return Truthy(Eval(binary.Left, def, row)) || Truthy(Eval(binary.Right, def, row));

        object? left = Eval(binary.Left, def, row);
        object? right = Eval(binary.Right, def, row);

        if (left is null || right is null)
            return binary.Op == "<>" && (left is null) != (right is null);

        int comparison = Compare(left, right);

        return binary.Op switch
        {
            "=" => comparison == 0,
            "<>" => comparison != 0,
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            ">=" => comparison >= 0,
            _ => throw new SqlException($"unsupported operator '{binary.Op}'")
        };
    }

    public static int Compare(object left, object right)
    {
        if (left is long a && right is long b) return a.CompareTo(b);
        if (left is string s && right is string t) return string.CompareOrdinal(s, t);
        if (left is bool x && right is bool y) return x.CompareTo(y);
        throw new SqlException($"cannot compare {left.GetType().Name} with {right.GetType().Name}");
    }
}
