namespace HydraDB.Core.Exec;

/// <summary>
/// Positions inside a chunk that survived a filter. Filters never move rows: they only
/// narrow this vector, so a chunk can be reused without copying row data.
/// </summary>
public sealed class SelectionVector
{
    public SelectionVector(int capacity) => Indices = new int[capacity];

    public int[] Indices { get; }

    public int Count { get; private set; }

    public void Add(int index) => Indices[Count++] = index;

    public void Clear() => Count = 0;

    /// <summary>Selects the first <paramref name="count"/> rows in order.</summary>
    public void SelectAll(int count)
    {
        for (int i = 0; i < count; i++) Indices[i] = i;
        Count = count;
    }
}

/// <summary>
/// A batch of rows in columnar-friendly form. Reference type on purpose: one instance is
/// filled, filtered, and yielded repeatedly, so the scan allocates once per query rather
/// than once per row. <see cref="Keys"/> holds the filter column already unboxed so
/// <see cref="VectorFilter"/> can load it straight into a Vector&lt;long&gt;.
/// </summary>
public sealed class DataChunk
{
    public const int Capacity = 1024;

    public DataChunk(int columnCount)
    {
        ColumnCount = columnCount;
        Rows = new object?[Capacity][];
        RowIds = new long[Capacity];
        Keys = new long[Capacity];
        KeyIsNull = new bool[Capacity];
        Selection = new SelectionVector(Capacity);
    }

    public int ColumnCount { get; }

    /// <summary>References to the live row arrays. Never copies of them.</summary>
    public object?[][] Rows { get; }

    public long[] RowIds { get; }

    /// <summary>Unboxed filter column, laid out contiguously for SIMD loads.</summary>
    public long[] Keys { get; }

    public bool[] KeyIsNull { get; }

    public SelectionVector Selection { get; }

    public int Count { get; private set; }

    public bool IsFull => Count == Capacity;

    public void Reset()
    {
        Count = 0;
        Selection.Clear();
    }

    public void Append(long rowId, object?[] values, int keyColumn)
    {
        int slot = Count++;
        Rows[slot] = values;
        RowIds[slot] = rowId;

        object? key = keyColumn >= 0 && keyColumn < values.Length ? values[keyColumn] : null;

        if (key is long unboxed)
        {
            Keys[slot] = unboxed;
            KeyIsNull[slot] = false;
        }
        else
        {
            // NULL never matches a comparison, so the payload value is irrelevant.
            Keys[slot] = 0;
            KeyIsNull[slot] = true;
        }
    }
}
