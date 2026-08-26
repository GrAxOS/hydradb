using System.Numerics;

namespace HydraDB.Core.Exec;

/// <summary>Comparison operators the vectorized filter can evaluate.</summary>
public enum CompareOp
{
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual
}

/// <summary>
/// SIMD predicate evaluation over a chunk's unboxed key column. Allocation-free: the only
/// values created are structs, and the surviving positions are written into the chunk's
/// existing selection vector. The tail that does not fill a full vector is done scalar.
/// </summary>
public static class VectorFilter
{
    public static void Apply(DataChunk chunk, CompareOp op, long threshold)
    {
        chunk.Selection.Clear();

        long[] keys = chunk.Keys;
        bool[] isNull = chunk.KeyIsNull;
        int count = chunk.Count;
        int width = Vector<long>.Count;
        var probe = new Vector<long>(threshold);
        int i = 0;

        for (; i + width <= count; i += width)
        {
            var block = new Vector<long>(keys, i);

            Vector<long> mask = op switch
            {
                CompareOp.Equal => Vector.Equals(block, probe),
                CompareOp.NotEqual => Vector.OnesComplement(Vector.Equals(block, probe)),
                CompareOp.Less => Vector.LessThan(block, probe),
                CompareOp.LessOrEqual => Vector.LessThanOrEqual(block, probe),
                CompareOp.Greater => Vector.GreaterThan(block, probe),
                _ => Vector.GreaterThanOrEqual(block, probe)
            };

            for (int lane = 0; lane < width; lane++)
            {
                int position = i + lane;
                if (mask[lane] != 0 && !isNull[position]) chunk.Selection.Add(position);
            }
        }

        for (; i < count; i++)
            if (!isNull[i] && Matches(keys[i], op, threshold)) chunk.Selection.Add(i);
    }

    public static bool Matches(long value, CompareOp op, long threshold) => op switch
    {
        CompareOp.Equal => value == threshold,
        CompareOp.NotEqual => value != threshold,
        CompareOp.Less => value < threshold,
        CompareOp.LessOrEqual => value <= threshold,
        CompareOp.Greater => value > threshold,
        _ => value >= threshold
    };
}
