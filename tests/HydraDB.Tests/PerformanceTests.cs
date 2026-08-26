using System.Text;
using HydraDB.Core;
using HydraDB.Core.Exec;
using HydraDB.Core.Index;
using HydraDB.Core.Sql;
using Xunit;

namespace HydraDB.Tests;

/// <summary>
/// Batch insert, vectorized filtering, index range scans, aggregation, and index stats.
/// Every fixture is generated from a counter, so there is no Random and no seed anywhere.
/// </summary>
public sealed class PerformanceTests
{
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "hydradb-perf-" + Guid.NewGuid().ToString("n"));

    private static Engine NewEngine()
    {
        var engine = new Engine(NewDirectory());
        engine.Execute("create table t (id int primary key, v int)");
        return engine;
    }

    /// <summary>One INSERT with (i, i) for every i in the range.</summary>
    private static string Batch(int from, int to)
    {
        var sql = new StringBuilder("insert into t values ");
        for (int i = from; i <= to; i++)
        {
            if (i > from) sql.Append(", ");
            sql.Append('(').Append(i).Append(", ").Append(i).Append(')');
        }
        return sql.ToString();
    }

    [Fact]
    public void BatchInsert_10000_Atomic()
    {
        using Engine engine = NewEngine();

        Assert.Equal(10_000, engine.Execute(Batch(1, 10_000)).Affected);
        Assert.Equal(10_000, engine.Execute("select id from t").Rows.Count);

        long commits = engine.Commits;

        // The duplicate is only visible inside the batch, and it must still abort the batch.
        Assert.Throws<SqlException>(() =>
            engine.Execute("insert into t values (20001, 1), (20002, 2), (20001, 3)"));

        Assert.Equal(commits, engine.Commits);
        Assert.Equal(10_000, engine.Execute("select id from t").Rows.Count);
        Assert.Empty(engine.Execute("select id from t where id = 20002").Rows);
    }

    [Fact]
    public void BatchInsert_1_Commit()
    {
        using Engine batched = NewEngine();
        long before = batched.Commits;
        batched.Execute(Batch(1, 1_000));
        Assert.Equal(before + 1, batched.Commits);

        using Engine singles = NewEngine();
        long baseline = singles.Commits;
        for (int i = 1; i <= 1_000; i++) singles.Execute($"insert into t values ({i}, {i})");
        Assert.Equal(baseline + 1_000, singles.Commits);

        Assert.Equal(1_000, batched.Execute("select id from t").Rows.Count);
        Assert.Equal(1_000, singles.Execute("select id from t").Rows.Count);
    }

    [Fact]
    public void Vectorized_Filter_Gt()
    {
        using Engine engine = NewEngine();
        engine.Execute(Batch(1, 5_000));

        var rows = engine.Execute("select id from t where v > 4000").Rows;

        Assert.Equal(ScanPlan.HeapVector, engine.LastPlan);
        Assert.Equal(1_000, rows.Count);
        Assert.All(rows, row => Assert.True((long)row[0]! > 4000));
    }

    [Fact]
    public void Range_Uses_BTree()
    {
        using Engine engine = NewEngine();
        engine.Execute(Batch(1, 5_000));

        var rows = engine.Execute("select id from t where id between 100 and 200").Rows;

        Assert.Equal(ScanPlan.IndexRange, engine.LastPlan);
        Assert.Equal(101, rows.Count);
        for (int i = 0; i < rows.Count; i++) Assert.Equal(100L + i, rows[i][0]);
    }

    [Fact]
    public void Stats_FillFactor()
    {
        using Engine engine = NewEngine();
        engine.Execute(Batch(1, 10_000));
        engine.Checkpoint();

        IndexStats stats = engine.GetAllStats()["t"];

        Assert.Equal(252, stats.Order);
        Assert.Equal(10_000L, stats.KeyCount);
        Assert.True(stats.Height > 1, $"height was {stats.Height}");
        Assert.True(stats.CrcOk);
        Assert.Equal(0L, stats.LogBytes);
        Assert.Equal(0, stats.SecondaryCount);
        Assert.InRange(stats.FillFactor, 40.0, 60.0);
    }

    [Fact]
    public void Aggregate_Count_Sum()
    {
        using Engine engine = NewEngine();
        engine.Execute(Batch(1, 100));

        QueryResult result = engine.Execute("select count(*), sum(v), avg(v) from t");

        Assert.Equal(new[] { "count(*)", "sum(v)", "avg(v)" }, result.Columns);
        Assert.Single(result.Rows);
        Assert.Equal(100L, result.Rows[0][0]);
        Assert.Equal(5050L, result.Rows[0][1]);
        Assert.Equal(50.5, (double)result.Rows[0][2]!, 9);
    }
}
