using HydraDB.Core;
using HydraDB.Core.Exec;
using HydraDB.Core.Graph;

string directory = args.Length > 0
    ? args[0]
    : Path.Combine(Directory.GetCurrentDirectory(), "hydradata");

using var engine = new Engine(directory);

Console.WriteLine($"HydraDB  {directory}");
Console.WriteLine("SQL, or: .tables  .graph  .stats  .checkpoint  .vacuum  .exit");

while (true)
{
    Console.Write("hydra> ");
    string? line = Console.ReadLine();
    if (line is null) break;

    line = line.Trim();
    if (line.Length == 0) continue;
    if (line is ".exit" or ".quit") break;

    try
    {
        switch (line)
        {
            case ".tables":
                foreach (var table in engine.Store.Tables.Values)
                {
                    string columns = string.Join(", ", table.Def.Columns.Select(c =>
                        $"{c.Name} {c.Type}{(c.PrimaryKey ? " PK" : string.Empty)}"));
                    Console.WriteLine($"{table.Def.Name}({columns})");
                }
                break;

            case ".graph":
            {
                var probe = engine.Begin();
                Console.WriteLine(GraphBuilder.Build(engine.Store, probe).ToJson());
                engine.Rollback(probe);
                break;
            }

            case ".stats":
                Console.WriteLine(
                    $"tables={engine.Store.Tables.Count} " +
                    $"commits={engine.Commits} " +
                    $"commitSeq={engine.Store.CommitSeq} " +
                    $"wal={engine.WalBytes}B");
                break;

            case ".checkpoint":
                engine.Checkpoint();
                Console.WriteLine($"checkpoint done, wal={engine.WalBytes}B");
                break;

            case ".vacuum":
                Console.WriteLine($"{engine.Store.Vacuum()} dead version(s) reclaimed");
                break;

            default:
                Print(engine.Execute(line));
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"error: {ex.Message}");
    }
}

static void Print(QueryResult result)
{
    if (result.Columns.Count == 0)
    {
        Console.WriteLine(result.Text ?? "ok");
        return;
    }

    Console.WriteLine(string.Join(" | ", result.Columns));
    foreach (var row in result.Rows)
        Console.WriteLine(string.Join(" | ", row.Select(v => v?.ToString() ?? "NULL")));
    Console.WriteLine($"({result.Rows.Count} row(s))");
}
