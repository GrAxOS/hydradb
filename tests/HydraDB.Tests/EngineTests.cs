using HydraDB.Core;
using HydraDB.Core.Sql;
using HydraDB.Core.Txn;
using Xunit;

namespace HydraDB.Tests;

public sealed class EngineTests
{
    private static string NewDirectory() => Directory.CreateTempSubdirectory("hydradb").FullName;

    private static Engine Seeded(string directory)
    {
        var engine = new Engine(directory);
        engine.Execute("create table accounts (id int primary key, owner text, balance int)");
        engine.Execute("insert into accounts values (1, 'ada', 100), (2, 'linus', 50)");
        return engine;
    }

    [Fact]
    public void ProjectsFiltersAndOrders()
    {
        using Engine engine = Seeded(NewDirectory());

        var result = engine.Execute("select owner, balance from accounts where balance > 40 order by balance desc");

        Assert.Equal(new[] { "owner", "balance" }, result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("ada", result.Rows[0][0]);
        Assert.Equal(100L, result.Rows[0][1]);
        Assert.Equal("linus", result.Rows[1][0]);
    }

    [Fact]
    public void SurvivesReopenThroughWalReplay()
    {
        string directory = NewDirectory();

        using (Engine engine = Seeded(directory))
        {
            engine.Execute("update accounts set balance = 999 where id = 2");
            engine.Execute("delete from accounts where id = 1");
        }

        using var reopened = new Engine(directory);
        var rows = reopened.Execute("select id, balance from accounts").Rows;

        Assert.Single(rows);
        Assert.Equal(2L, rows[0][0]);
        Assert.Equal(999L, rows[0][1]);
    }

    [Fact]
    public void CheckpointEmptiesWalAndKeepsData()
    {
        string directory = NewDirectory();

        using (Engine engine = Seeded(directory))
        {
            Assert.True(engine.WalBytes > 0);
            engine.Checkpoint();
            Assert.Equal(0, engine.WalBytes);
        }

        using var reopened = new Engine(directory);
        Assert.Equal(2, reopened.Execute("select * from accounts").Rows.Count);
    }

    [Fact]
    public void SnapshotHidesUncommittedWrites()
    {
        using Engine engine = Seeded(NewDirectory());

        Transaction writer = engine.Begin();
        engine.Execute("insert into accounts values (3, 'grace', 10)", writer);

        Transaction reader = engine.Begin();
        Assert.Equal(2, engine.Execute("select id from accounts", reader).Rows.Count);
        Assert.Equal(3, engine.Execute("select id from accounts", writer).Rows.Count);

        engine.Commit(writer);

        // The old snapshot is stable; a new one sees the commit.
        Assert.Equal(2, engine.Execute("select id from accounts", reader).Rows.Count);
        Transaction later = engine.Begin();
        Assert.Equal(3, engine.Execute("select id from accounts", later).Rows.Count);
    }

    [Fact]
    public void ConcurrentWriteToSameRowConflicts()
    {
        using Engine engine = Seeded(NewDirectory());

        Transaction first = engine.Begin();
        Transaction second = engine.Begin();

        engine.Execute("update accounts set balance = 1 where id = 1", first);

        Assert.Throws<SerializationConflictException>(() =>
            engine.Execute("update accounts set balance = 2 where id = 1", second));
    }

    [Fact]
    public void RollbackDiscardsWrites()
    {
        using Engine engine = Seeded(NewDirectory());

        Transaction txn = engine.Begin();
        engine.Execute("insert into accounts values (9, 'nobody', 0)", txn);
        engine.Rollback(txn);

        Assert.Equal(2, engine.Execute("select id from accounts").Rows.Count);
    }

    [Fact]
    public void EnforcesPrimaryKeyAndForeignKey()
    {
        using Engine engine = Seeded(NewDirectory());

        Assert.Throws<SqlException>(() =>
            engine.Execute("insert into accounts values (1, 'clone', 0)"));

        engine.Execute("create table entries (id int primary key, account int, foreign key (account) references accounts(id))");
        engine.Execute("insert into entries values (1, 2)");

        Assert.Throws<SqlException>(() =>
            engine.Execute("insert into entries values (2, 77)"));
    }

    [Fact]
    public void RejectsTypeMismatch()
    {
        using Engine engine = Seeded(NewDirectory());

        Assert.Throws<SqlException>(() =>
            engine.Execute("insert into accounts values (3, 5, 10)"));
    }
}
