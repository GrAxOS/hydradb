namespace HydraDB.Core.Exec;

public sealed class QueryResult
{
    public List<string> Columns { get; init; } = new();
    public List<object?[]> Rows { get; init; } = new();
    public int Affected { get; init; }
    public string? Text { get; init; }

    public static QueryResult Message(string text) => new QueryResult { Text = text };

    public static QueryResult Changed(int rows) =>
        new QueryResult { Affected = rows, Text = $"{rows} row(s)" };
}
