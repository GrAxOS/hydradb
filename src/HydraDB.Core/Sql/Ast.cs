using HydraDB.Core.Catalog;

namespace HydraDB.Core.Sql;

public sealed class SqlException : Exception
{
    public SqlException(string message) : base(message) { }
}

public abstract class Stmt
{
}

public sealed class CreateTableStmt : Stmt
{
    public CreateTableStmt(TableDef def) => Def = def;

    public TableDef Def { get; }
}

public sealed class InsertStmt : Stmt
{
    public string Table = string.Empty;
    public List<string> Columns = new();
    public List<List<Expr>> Rows = new();
}

public sealed class SelectStmt : Stmt
{
    /// <summary>A single "*" entry means every column.</summary>
    public List<string> Columns = new();

    public string Table = string.Empty;
    public Expr? Where;
    public string? OrderBy;
    public bool OrderDescending;
    public int? Limit;
}

public sealed class UpdateStmt : Stmt
{
    public string Table = string.Empty;
    public List<(string Column, Expr Value)> Assignments = new();
    public Expr? Where;
}

public sealed class DeleteStmt : Stmt
{
    public string Table = string.Empty;
    public Expr? Where;
}

public enum TransactionKind
{
    Begin,
    Commit,
    Rollback
}

public sealed class TransactionStmt : Stmt
{
    public TransactionStmt(TransactionKind kind) => Kind = kind;

    public TransactionKind Kind { get; }
}

public abstract class Expr
{
}

public sealed class Literal : Expr
{
    public Literal(object? value) => Value = value;

    public object? Value { get; }
}

public sealed class ColumnRef : Expr
{
    public ColumnRef(string name) => Name = name;

    public string Name { get; }
}

public sealed class Unary : Expr
{
    public Unary(string op, Expr operand)
    {
        Op = op;
        Operand = operand;
    }

    public string Op { get; }
    public Expr Operand { get; }
}

public sealed class Binary : Expr
{
    public Binary(string op, Expr left, Expr right)
    {
        Op = op;
        Left = left;
        Right = right;
    }

    public string Op { get; }
    public Expr Left { get; }
    public Expr Right { get; }
}
