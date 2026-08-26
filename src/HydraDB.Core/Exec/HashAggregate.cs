namespace HydraDB.Core.Exec;

public enum AggregateKind
{
    CountStar,
    Count,
    Sum,
    Avg
}

/// <summary>One aggregate in the select list. <see cref="ColumnIndex"/> is bound by the executor.</summary>
public sealed class AggregateSpec
{
    public AggregateSpec(AggregateKind kind, string? column)
    {
        Kind = kind;
        Column = column;
    }

    public AggregateKind Kind { get; }

    /// <summary>Null for COUNT(*).</summary>
    public string? Column { get; }

    public int ColumnIndex { get; set; } = -1;

    public string Name => Kind switch
    {
        AggregateKind.CountStar => "count(*)",
        AggregateKind.Count => $"count({Column})",
        AggregateKind.Sum => $"sum({Column})",
        _ => $"avg({Column})"
    };
}

/// <summary>
/// Grouped aggregation over a chunk pipeline. Each group keeps two counters per aggregate,
/// count and sum, so COUNT, SUM, and AVG all fall out of the same fixed-width state.
/// Up to <see cref="SmallGroupLimit"/> groups live in one stack allocation; beyond that the
/// whole state spills into a dictionary. Output is sorted by group key, so results are
/// deterministic regardless of the path taken.
/// </summary>
public sealed class HashAggregate
{
    public const int SmallGroupLimit = 128;

    private readonly int _groupColumn;
    private readonly string? _groupName;
    private readonly List<AggregateSpec> _specs;

    public HashAggregate(int groupColumn, string? groupName, List<AggregateSpec> specs)
    {
        _groupColumn = groupColumn;
        _groupName = groupName;
        _specs = specs;
    }

    public QueryResult Run(IEnumerable<DataChunk> chunks, int estimatedKeys)
    {
        int width = _specs.Count * 2;
        Span<long> small = stackalloc long[SmallGroupLimit * width];
        Span<long> smallKeys = stackalloc long[SmallGroupLimit];
        int smallCount = 0;

        Dictionary<long, long[]>? spilled = estimatedKeys > SmallGroupLimit
            ? new Dictionary<long, long[]>(estimatedKeys)
            : null;

        if (_groupColumn < 0)
        {
            // Ungrouped aggregation always produces exactly one row, even over zero rows.
            if (spilled is null)
            {
                smallKeys[0] = 0;
                smallCount = 1;
            }
            else
            {
                spilled[0] = new long[width];
            }
        }

        foreach (DataChunk chunk in chunks)
        {
            for (int s = 0; s < chunk.Selection.Count; s++)
            {
                object?[] row = chunk.Rows[chunk.Selection.Indices[s]];

                long groupKey = 0;
                if (_groupColumn >= 0)
                    groupKey = row[_groupColumn] is long value ? value : long.MinValue;

                if (spilled is null)
                {
                    int found = -1;
                    for (int i = 0; i < smallCount; i++)
                        if (smallKeys[i] == groupKey)
                        {
                            found = i;
                            break;
                        }

                    if (found >= 0)
                    {
                        Accumulate(small.Slice(found * width, width), row);
                        continue;
                    }

                    if (smallCount < SmallGroupLimit)
                    {
                        found = smallCount++;
                        smallKeys[found] = groupKey;
                        Accumulate(small.Slice(found * width, width), row);
                        continue;
                    }

                    // Too many distinct groups for the stack: move the whole state to the heap.
                    spilled = new Dictionary<long, long[]>(SmallGroupLimit * 2);
                    for (int i = 0; i < smallCount; i++)
                    {
                        var copy = new long[width];
                        small.Slice(i * width, width).CopyTo(copy);
                        spilled[smallKeys[i]] = copy;
                    }
                }

                if (!spilled!.TryGetValue(groupKey, out long[]? accumulator))
                {
                    accumulator = new long[width];
                    spilled[groupKey] = accumulator;
                }

                Accumulate(accumulator, row);
            }
        }

        var groups = new List<(long Key, long[] Accumulator)>();

        if (spilled is null)
        {
            for (int i = 0; i < smallCount; i++)
            {
                var copy = new long[width];
                small.Slice(i * width, width).CopyTo(copy);
                groups.Add((smallKeys[i], copy));
            }
        }
        else
        {
            foreach ((long key, long[] accumulator) in spilled) groups.Add((key, accumulator));
        }

        groups.Sort((left, right) => left.Key.CompareTo(right.Key));

        var columns = new List<string>();
        if (_groupName is not null) columns.Add(_groupName);
        foreach (AggregateSpec spec in _specs) columns.Add(spec.Name);

        var rows = new List<object?[]>(groups.Count);
        foreach ((long key, long[] accumulator) in groups)
        {
            var row = new object?[columns.Count];
            int offset = 0;
            if (_groupName is not null) row[offset++] = key;

            for (int i = 0; i < _specs.Count; i++)
            {
                long count = accumulator[i * 2];
                long sum = accumulator[i * 2 + 1];

                row[offset + i] = _specs[i].Kind switch
                {
                    AggregateKind.CountStar => (object?)count,
                    AggregateKind.Count => count,
                    AggregateKind.Sum => count == 0 ? null : (object?)sum,
                    _ => count == 0 ? null : (object?)((double)sum / count)
                };
            }

            rows.Add(row);
        }

        return new QueryResult { Columns = columns, Rows = rows };
    }

    private void Accumulate(Span<long> accumulator, object?[] row)
    {
        for (int i = 0; i < _specs.Count; i++)
        {
            AggregateSpec spec = _specs[i];

            if (spec.Kind == AggregateKind.CountStar)
            {
                accumulator[i * 2]++;
                continue;
            }

            object? value = spec.ColumnIndex >= 0 && spec.ColumnIndex < row.Length
                ? row[spec.ColumnIndex]
                : null;

            if (value is null) continue;

            if (spec.Kind == AggregateKind.Count)
            {
                accumulator[i * 2]++;
                continue;
            }

            if (value is long number)
            {
                accumulator[i * 2]++;
                accumulator[i * 2 + 1] += number;
            }
        }
    }
}
