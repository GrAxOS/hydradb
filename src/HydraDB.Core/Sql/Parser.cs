using HydraDB.Core.Catalog;
using HydraDB.Core.Exec;

namespace HydraDB.Core.Sql;

/// <summary>
/// Recursive-descent parser. Expression precedence, weakest first:
/// OR &lt; AND &lt; NOT &lt; comparison &lt; primary.
/// </summary>
public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _position;

    public Parser(string sql) => _tokens = new Lexer(sql).Tokenize();

    private Token Peek => _tokens[_position];

    private Token PeekAt(int offset) => _tokens[Math.Min(_position + offset, _tokens.Count - 1)];

    private Token Next() => _tokens[_position++];

    private bool Accept(string text)
    {
        if (!Peek.Is(text)) return false;
        _position++;
        return true;
    }

    private void Expect(string text)
    {
        if (!Accept(text))
            throw new SqlException($"expected '{text}' but found '{Peek.Text}'");
    }

    private string Identifier()
    {
        Token token = Next();
        if (token.Kind != TokenKind.Ident && token.Kind != TokenKind.Keyword)
            throw new SqlException($"expected identifier but found '{token.Text}'");
        return token.Text;
    }

    private long Number()
    {
        Token token = Next();
        if (token.Kind != TokenKind.Number)
            throw new SqlException($"expected number but found '{token.Text}'");
        return long.Parse(token.Text);
    }

    public Stmt ParseStatement()
    {
        Stmt statement;

        if (Peek.Is("create")) statement = ParseCreateTable();
        else if (Peek.Is("insert")) statement = ParseInsert();
        else if (Peek.Is("select")) statement = ParseSelect();
        else if (Peek.Is("update")) statement = ParseUpdate();
        else if (Peek.Is("delete")) statement = ParseDelete();
        else if (Accept("begin")) statement = new TransactionStmt(TransactionKind.Begin);
        else if (Accept("commit")) statement = new TransactionStmt(TransactionKind.Commit);
        else if (Accept("rollback")) statement = new TransactionStmt(TransactionKind.Rollback);
        else throw new SqlException($"unsupported statement starting at '{Peek.Text}'");

        Accept(";");
        if (Peek.Kind != TokenKind.Eof)
            throw new SqlException($"unexpected trailing input '{Peek.Text}'");

        return statement;
    }

    private Stmt ParseCreateTable()
    {
        Expect("create");
        Expect("table");
        string name = Identifier();
        Expect("(");

        var columns = new List<Column>();
        var foreignKeys = new List<ForeignKey>();

        do
        {
            if (Peek.Is("foreign"))
            {
                Expect("foreign");
                Expect("key");
                Expect("(");
                string column = Identifier();
                Expect(")");
                Expect("references");
                string referencedTable = Identifier();
                Expect("(");
                string referencedColumn = Identifier();
                Expect(")");
                foreignKeys.Add(new ForeignKey(column, referencedTable, referencedColumn));
                continue;
            }

            string columnName = Identifier();
            ColumnType type = ParseColumnType();
            bool primaryKey = false;
            if (Accept("primary"))
            {
                Expect("key");
                primaryKey = true;
            }
            columns.Add(new Column(columnName, type, primaryKey));
        } while (Accept(","));

        Expect(")");

        if (columns.Count == 0)
            throw new SqlException("table must declare at least one column");

        return new CreateTableStmt(new TableDef(name, columns, foreignKeys));
    }

    private ColumnType ParseColumnType()
    {
        Token token = Next();
        if (token.Is("int") || token.Is("integer") || token.Is("bigint")) return ColumnType.Int;
        if (token.Is("text") || token.Is("varchar") || token.Is("string")) return ColumnType.Text;
        if (token.Is("bool") || token.Is("boolean")) return ColumnType.Bool;
        throw new SqlException($"unknown column type '{token.Text}'");
    }

    private Stmt ParseInsert()
    {
        Expect("insert");
        Expect("into");

        var statement = new InsertStmt { Table = Identifier() };

        if (Accept("("))
        {
            do
            {
                statement.Columns.Add(Identifier());
            } while (Accept(","));
            Expect(")");
        }

        Expect("values");

        do
        {
            Expect("(");
            var row = new List<Expr>();
            do
            {
                row.Add(ParseExpr());
            } while (Accept(","));
            Expect(")");
            statement.Rows.Add(row);
        } while (Accept(","));

        return statement;
    }

    private Stmt ParseSelect()
    {
        Expect("select");
        var statement = new SelectStmt();

        if (Accept("*"))
        {
            statement.Columns.Add("*");
        }
        else
        {
            do
            {
                if (TryParseAggregate(statement)) continue;
                statement.Columns.Add(Identifier());
            } while (Accept(","));
        }

        Expect("from");
        statement.Table = Identifier();

        if (Accept("where")) statement.Where = ParseExpr();

        if (Accept("group"))
        {
            Expect("by");
            statement.GroupBy = Identifier();
        }

        if (Accept("order"))
        {
            Expect("by");
            statement.OrderBy = Identifier();
            if (Accept("desc")) statement.OrderDescending = true;
            else Accept("asc");
        }

        if (Accept("limit")) statement.Limit = (int)Number();

        return statement;
    }

    /// <summary>COUNT(*), COUNT(col), SUM(col), AVG(col). Anything else is a plain column.</summary>
    private bool TryParseAggregate(SelectStmt statement)
    {
        AggregateKind kind;
        if (Peek.Is("count")) kind = AggregateKind.Count;
        else if (Peek.Is("sum")) kind = AggregateKind.Sum;
        else if (Peek.Is("avg")) kind = AggregateKind.Avg;
        else return false;

        if (!PeekAt(1).Is("(")) return false;

        _position += 2;

        if (kind == AggregateKind.Count && Accept("*"))
        {
            Expect(")");
            statement.Aggregates.Add(new AggregateSpec(AggregateKind.CountStar, null));
            return true;
        }

        string column = Identifier();
        Expect(")");
        statement.Aggregates.Add(new AggregateSpec(kind, column));
        return true;
    }

    private Stmt ParseUpdate()
    {
        Expect("update");
        var statement = new UpdateStmt { Table = Identifier() };
        Expect("set");

        do
        {
            string column = Identifier();
            Expect("=");
            statement.Assignments.Add((column, ParseExpr()));
        } while (Accept(","));

        if (Accept("where")) statement.Where = ParseExpr();

        if (statement.Assignments.Count == 0)
            throw new SqlException("UPDATE requires at least one assignment");

        return statement;
    }

    private Stmt ParseDelete()
    {
        Expect("delete");
        Expect("from");
        var statement = new DeleteStmt { Table = Identifier() };
        if (Accept("where")) statement.Where = ParseExpr();
        return statement;
    }

    private Expr ParseExpr() => ParseOr();

    private Expr ParseOr()
    {
        Expr left = ParseAnd();
        while (Accept("or")) left = new Binary("or", left, ParseAnd());
        return left;
    }

    private Expr ParseAnd()
    {
        Expr left = ParseNot();
        while (Accept("and")) left = new Binary("and", left, ParseNot());
        return left;
    }

    private Expr ParseNot()
    {
        if (Accept("not")) return new Unary("not", ParseNot());
        return ParseComparison();
    }

    private Expr ParseComparison()
    {
        Expr left = ParsePrimary();

        // BETWEEN lowers to and(>=, <=) so the range planner sees two ordinary bounds.
        if (Peek.Kind != TokenKind.Punct && Peek.Is("between"))
        {
            _position++;
            Expr lower = ParsePrimary();
            Expect("and");
            Expr upper = ParsePrimary();
            return new Binary("and", new Binary(">=", left, lower), new Binary("<=", left, upper));
        }

        while (Peek.Kind == TokenKind.Punct &&
               Peek.Text is "=" or "<" or ">" or "<=" or ">=" or "<>")
        {
            string op = Next().Text;
            left = new Binary(op, left, ParsePrimary());
        }

        return left;
    }

    private Expr ParsePrimary()
    {
        Token token = Peek;

        if (token.Kind == TokenKind.Punct && token.Text == "(")
        {
            _position++;
            Expr inner = ParseExpr();
            Expect(")");
            return inner;
        }

        if (token.Kind == TokenKind.Number)
        {
            _position++;
            return new Literal(long.Parse(token.Text));
        }

        if (token.Kind == TokenKind.String)
        {
            _position++;
            return new Literal(token.Text);
        }

        if (token.Is("true"))
        {
            _position++;
            return new Literal(true);
        }

        if (token.Is("false"))
        {
            _position++;
            return new Literal(false);
        }

        if (token.Is("null"))
        {
            _position++;
            return new Literal(null);
        }

        // Identifier() accepts keyword tokens in schema/projection contexts. The
        // same stored column must remain addressable in predicates and assignment
        // expressions; otherwise CREATE TABLE ... (order INT) succeeds but
        // WHERE order = 1 fails to parse. Literal keywords are handled above.
        if (token.Kind == TokenKind.Ident || token.Kind == TokenKind.Keyword)
        {
            _position++;
            return new ColumnRef(token.Text);
        }

        throw new SqlException($"unexpected token '{token.Text}' in expression");
    }
}
