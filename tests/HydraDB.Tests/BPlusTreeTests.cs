using HydraDB.Core;
using HydraDB.Core.Index;
using HydraDB.Core.Sql;
using HydraDB.Core.Storage;
using Xunit;

namespace HydraDB.Tests;

public sealed class BPlusTreeTests
{
    private static (string PagePath, string LogPath) NewPaths()
    {
        string directory = Directory.CreateTempSubdirectory("hydrabtree").FullName;
        return (Path.Combine(directory, "pk.idx"), Path.Combine(directory, "pk.idxlog"));
    }

    [Fact]
    public void OrderFollowsPageSize()
    {
        Assert.Equal(508, BPlusTree.OrderFor(8192));
        Assert.Equal(252, BPlusTree.OrderFor(4096));

        (string pagePath, string logPath) = NewPaths();
        using var tree = new BPlusTree(pagePath, logPath);

        Assert.Equal(BPlusTree.OrderFor(Pager.PageSize), tree.Order);
    }

    [Fact]
    public void TenThousandSequentialKeysAreAllFound()
    {
        (string pagePath, string logPath) = NewPaths();
        using var tree = new BPlusTree(pagePath, logPath);

        for (long key = 1; key <= 10_000; key++) tree.Insert(key, key * 2);

        tree.Validate();
        Assert.Equal(10_000L, tree.KeyCount);
        Assert.True(tree.Height > 1, "10k keys must not fit in a single leaf");

        for (long key = 1; key <= 10_000; key++) Assert.Equal(key * 2, tree.Search(key));

        Assert.Null(tree.Search(0));
        Assert.Null(tree.Search(10_001));
        Assert.Equal(101, tree.Range(500, 600).Count());
    }

    [Fact]
    public void TenThousandReverseKeysAreAllFound()
    {
        (string pagePath, string logPath) = NewPaths();
        using var tree = new BPlusTree(pagePath, logPath);

        for (long key = 10_000; key >= 1; key--) tree.Insert(key, key * 2);

        tree.Validate();
        Assert.Equal(10_000L, tree.KeyCount);
        Assert.True(tree.Height > 1, "10k keys must not fit in a single leaf");

        for (long key = 10_000; key >= 1; key--) Assert.Equal(key * 2, tree.Search(key));

        Assert.Null(tree.Search(0));
        Assert.Null(tree.Search(10_001));
        Assert.Equal(101, tree.Range(500, 600).Count());
    }

    [Fact]
    public void DuplicateKeyThrowsConstraintViolation()
    {
        (string pagePath, string logPath) = NewPaths();
        using var tree = new BPlusTree(pagePath, logPath);

        tree.Insert(7, 100);
        Assert.Throws<ConstraintViolationException>(() => tree.Insert(7, 200));

        // Upsert is the MVCC path: it repoints a key whose old row is no longer visible.
        tree.Upsert(7, 300);
        Assert.Equal(300, tree.Search(7));
        Assert.Equal(1, tree.KeyCount);
    }

    [Fact]
    public void CrashBeforeGroupEndLeavesTreeAtPreviousState()
    {
        (string pagePath, string logPath) = NewPaths();
        int order = BPlusTree.OrderFor(Pager.PageSize);
        int durableKeys = order + 1; // guarantees at least one completed leaf split

        using (var tree = new BPlusTree(pagePath, logPath))
        {
            for (int i = 0; i < durableKeys; i++) tree.Insert(i, i * 10L);
            tree.Validate();

            // Fill the right-hand leaf back up, then crash inside the next split.
            tree.SimulateCrashBeforeGroupEnd = true;
            for (int i = durableKeys; i < durableKeys + order; i++) tree.Insert(i, i * 10L);
        }

        using var reopened = new BPlusTree(pagePath, logPath);

        reopened.Validate();
        Assert.Equal(durableKeys, reopened.KeyCount);
        for (int i = 0; i < durableKeys; i++) Assert.Equal(i * 10L, reopened.Search(i));
        Assert.Null(reopened.Search(durableKeys));
    }

    [Fact]
    public void SearchWorksAfterCheckpointAndReopen()
    {
        (string pagePath, string logPath) = NewPaths();
        const int count = 3_000;

        using (var tree = new BPlusTree(pagePath, logPath))
        {
            for (int i = 0; i < count; i++) tree.Insert(i, i + 1L);
            Assert.True(tree.LogBytes > 0);

            tree.Checkpoint();
            Assert.Equal(0, tree.LogBytes);

            for (int i = 0; i < count; i++) Assert.Equal(i + 1L, tree.Search(i));
        }

        using var reopened = new BPlusTree(pagePath, logPath);
        reopened.Validate();
        for (int i = 0; i < count; i++) Assert.Equal(i + 1L, reopened.Search(i));
    }

    [Fact]
    public void RangeScanWalksLeafChain()
    {
        (string pagePath, string logPath) = NewPaths();
        using var tree = new BPlusTree(pagePath, logPath);

        for (int i = 0; i < 2_000; i++) tree.Insert(i, i);

        var slice = tree.Range(1_190, 1_210).ToList();

        Assert.Equal(21, slice.Count);
        Assert.Equal(1_190, slice[0].Key);
        Assert.Equal(1_210, slice[^1].Key);
        Assert.True(slice.Zip(slice.Skip(1)).All(pair => pair.First.Key < pair.Second.Key));
    }

    [Fact]
    public void EngineUsesIndexAndStillRejectsDuplicatePrimaryKeys()
    {
        string directory = Directory.CreateTempSubdirectory("hydraidx").FullName;

        using (var engine = new Engine(directory))
        {
            engine.Execute("create table t (id int primary key, label text)");
            for (int i = 1; i <= 1_000; i++)
                engine.Execute($"insert into t values ({i}, 'row{i}')");

            Assert.True(engine.Indexes.TryGetTable("t", out Table table));
            Assert.Equal(1_000, table.PrimaryKeyIndex.KeyCount);
            Assert.Equal(500L, table.PrimaryKeyIndex.Search(500));

            Assert.Throws<SqlException>(() => engine.Execute("insert into t values (500, 'clone')"));

            // A deleted key frees its index slot for reuse.
            engine.Execute("delete from t where id = 500");
            engine.Execute("insert into t values (500, 'reused')");
            engine.Checkpoint();
        }

        using var reopened = new Engine(directory);

        Assert.True(reopened.Indexes.TryGetTable("t", out Table rebuilt));
        rebuilt.PrimaryKeyIndex.Validate();
        Assert.Equal(1_000, rebuilt.PrimaryKeyIndex.KeyCount);

        var rows = reopened.Execute("select label from t where id = 500").Rows;
        Assert.Single(rows);
        Assert.Equal("reused", rows[0][0]);
    }
}
