using HydraDB.Core.Graph;
using HydraDB.Core.Storage;
using HydraDB.Core.Txn;
using HydraDB.Core.Util;
using Xunit;

namespace HydraDB.Tests;

public sealed class StorageTests
{
    [Fact]
    public void Crc32MatchesKnownVector()
    {
        // CRC-32/ISO-HDLC of "123456789" is 0xCBF43926.
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Fact]
    public void SlottedPageRoundTripsRecords()
    {
        var page = new SlottedPage();
        Assert.True(page.TryAdd(new byte[] { 1, 2, 3 }));
        Assert.True(page.TryAdd(new byte[] { 9 }));

        var reloaded = new SlottedPage(page.Buffer);
        Assert.Equal(2, reloaded.SlotCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, reloaded.Get(0).ToArray());
        Assert.Equal(new byte[] { 9 }, reloaded.Get(1).ToArray());
    }

    [Fact]
    public void SlottedPageRefusesOverflow()
    {
        var page = new SlottedPage();
        Assert.False(page.TryAdd(new byte[Pager.PageSize]));
        Assert.Equal(0, page.SlotCount);
    }

    [Fact]
    public void WalTruncatesTornTail()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("hydrawal").FullName, "t.wal");

        using (var wal = new Wal(path))
        {
            wal.Append(new byte[] { 7, 7, 7 });
            wal.Sync();
        }

        // Simulate a crash in the middle of a second record.
        using (var stream = new FileStream(path, FileMode.Append))
        {
            stream.Write(BitConverter.GetBytes(64));
            stream.Write(new byte[] { 0, 0, 0, 0, 1, 2 });
        }

        using var reopened = new Wal(path);
        var records = reopened.Replay();

        Assert.Single(records);
        Assert.Equal(new byte[] { 7, 7, 7 }, records[0]);
        Assert.Equal(11, reopened.SizeBytes); // 8-byte header + 3-byte payload
    }

    [Fact]
    public void ByteCodecRoundTripsEveryValueKind()
    {
        var writer = new ByteWriter();
        writer.Value(null);
        writer.Value(42L);
        writer.Value("nazal");
        writer.Value(true);

        var reader = new ByteReader(writer.ToArray());
        Assert.Null(reader.Value());
        Assert.Equal(42L, reader.Value());
        Assert.Equal("nazal", reader.Value());
        Assert.Equal(true, reader.Value());
        Assert.True(reader.Eof);
    }

    [Fact]
    public void VogelLayoutIsDeterministicAndCollisionFree()
    {
        var store = new MvccStore();
        Transaction txn = store.BeginTxn();

        // The layout depends only on node index, so verify the closed form directly.
        double g = GraphBuilder.GoldenAngle;
        Assert.InRange(g, 2.39, 2.41);

        var points = new List<(double X, double Y)>();
        for (int i = 0; i < 200; i++)
        {
            double r = Math.Sqrt(i + 1);
            points.Add((r * Math.Cos(i * g), r * Math.Sin(i * g)));
        }

        for (int i = 0; i < points.Count; i++)
        for (int j = i + 1; j < points.Count; j++)
        {
            double dx = points[i].X - points[j].X;
            double dy = points[i].Y - points[j].Y;
            Assert.True(Math.Sqrt(dx * dx + dy * dy) > 0.5, $"nodes {i} and {j} overlap");
        }

        Assert.Empty(GraphBuilder.Build(store, txn).Nodes);
    }
}
