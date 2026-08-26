namespace HydraDB.Core.Catalog;

public enum ColumnType : byte
{
    Int = 1,
    Text = 2,
    Bool = 3
}

public sealed class Column
{
    public Column(string name, ColumnType type, bool primaryKey = false)
    {
        Name = name;
        Type = type;
        PrimaryKey = primaryKey;
    }

    public string Name { get; }
    public ColumnType Type { get; }
    public bool PrimaryKey { get; }
}

public sealed class ForeignKey
{
    public ForeignKey(string column, string referencedTable, string referencedColumn)
    {
        Column = column;
        ReferencedTable = referencedTable;
        ReferencedColumn = referencedColumn;
    }

    public string Column { get; }
    public string ReferencedTable { get; }
    public string ReferencedColumn { get; }
}

public sealed class TableDef
{
    public TableDef(string name, List<Column> columns, List<ForeignKey> foreignKeys)
    {
        Name = name;
        Columns = columns;
        ForeignKeys = foreignKeys;
    }

    public string Name { get; }
    public List<Column> Columns { get; }
    public List<ForeignKey> ForeignKeys { get; }

    public int IndexOf(string column)
    {
        for (int i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i].Name, column, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
