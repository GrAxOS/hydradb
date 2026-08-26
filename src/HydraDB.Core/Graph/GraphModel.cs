using System.Text.Json;
using System.Text.Json.Serialization;
using HydraDB.Core.Txn;

namespace HydraDB.Core.Graph;

public sealed class GraphNode
{
    public string Id { get; init; } = string.Empty;
    public int Rows { get; init; }
    public int Versions { get; init; }
    public long Writes { get; init; }
    public double Heat { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public int Degree { get; init; }
}

public sealed class GraphEdge
{
    public string Source { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

public sealed class GraphSnapshot
{
    public List<GraphNode> Nodes { get; init; } = new();
    public List<GraphEdge> Edges { get; init; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    });
}

/// <summary>
/// Closed-form graph layout. Node i is placed at radius sqrt(i+1) and angle
/// i * g where g = pi * (3 - sqrt(5)) is the golden angle. This is the
/// Vogel spiral: it fills the plane with uniform density, never collides, and is
/// fully deterministic, so no force simulation or animation loop is needed.
/// </summary>
public static class GraphBuilder
{
    public static readonly double GoldenAngle = Math.PI * (3.0 - Math.Sqrt(5.0));

    public static GraphSnapshot Build(MvccStore store, Transaction txn)
    {
        var tables = new List<TableData>(store.Tables.Values);

        var degree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (TableData table in tables) degree[table.Def.Name] = 0;

        var edges = new List<GraphEdge>();
        foreach (TableData table in tables)
        {
            foreach (var fk in table.Def.ForeignKeys)
            {
                edges.Add(new GraphEdge
                {
                    Source = table.Def.Name,
                    Target = fk.ReferencedTable,
                    Label = $"{fk.Column} -> {fk.ReferencedColumn}"
                });

                degree[table.Def.Name] = degree.GetValueOrDefault(table.Def.Name) + 1;
                degree[fk.ReferencedTable] = degree.GetValueOrDefault(fk.ReferencedTable) + 1;
            }
        }

        long maxWrites = 1;
        foreach (TableData table in tables) maxWrites = Math.Max(maxWrites, table.Writes);

        var nodes = new List<GraphNode>(tables.Count);
        for (int i = 0; i < tables.Count; i++)
        {
            TableData table = tables[i];

            double angle = i * GoldenAngle;
            double radius = Math.Sqrt(i + 1);

            int visibleRows = 0;
            foreach (RowVersion version in table.Versions)
                if (store.IsVisible(version, txn))
                    visibleRows++;

            nodes.Add(new GraphNode
            {
                Id = table.Def.Name,
                Rows = visibleRows,
                Versions = table.Versions.Count,
                Writes = table.Writes,
                Heat = (double)table.Writes / maxWrites,
                X = Math.Round(radius * Math.Cos(angle), 6),
                Y = Math.Round(radius * Math.Sin(angle), 6),
                Degree = degree.GetValueOrDefault(table.Def.Name)
            });
        }

        return new GraphSnapshot { Nodes = nodes, Edges = edges };
    }
}
