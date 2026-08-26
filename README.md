# HydraDB

Embedded hybrid SQL engine in C# (.NET 8, zero external dependencies in the core).

SQLite-style: in-process, one directory, no server.
Postgres-style: MVCC snapshot isolation, multiple concurrent writers, first-updater-wins conflict detection.

## Layout

```
src/HydraDB.Core/Storage   Pager (4 KiB pages), SlottedPage, WAL (CRC32 framed, torn-tail truncation)
src/HydraDB.Core/Txn      MvccStore: row versions (Xmin/Xmax), snapshots, vacuum horizon
src/HydraDB.Core/Log      Commit-record codec (one atomic WAL record per transaction)
src/HydraDB.Core/Sql      Lexer, recursive-descent parser, AST
src/HydraDB.Core/Exec     Evaluator, Executor, PK/FK enforcement
src/HydraDB.Core/Graph    Deterministic golden-angle graph layout over the live catalog
src/HydraDB.Cli           REPL
tests/HydraDB.Tests       Durability, isolation, conflict, constraint tests
```

## Durability model

A transaction buffers its ops in memory. `COMMIT` writes exactly one WAL record
(`[len:i32][crc32:u32][payload]`), fsyncs it, then publishes the commit sequence
number. A record is replayed only if its length is sane and its CRC32 matches, so a
torn tail is truncated at open instead of corrupting state. `CHECKPOINT` writes all
committed-live rows into slotted pages, fsyncs the snapshot file, resets the WAL, and
vacuums dead versions.

## Visibility rule

A version `v` is visible to transaction `T` iff

```
(v.Xmin == T.Tid  or  committed(v.Xmin) and commitSeq(v.Xmin) <= T.SnapshotSeq)
and not (v.Xmax != 0 and (v.Xmax == T.Tid or committed(v.Xmax) and commitSeq(v.Xmax) <= T.SnapshotSeq))
```

A write to a version whose `Xmax` belongs to an active transaction, or to a
transaction committed after `T.SnapshotSeq`, raises `SerializationConflictException`.

## Graph layout

Nodes are tables, edges are declared foreign keys. Positions are computed, not
simulated: node `i` sits at `r = sqrt(i+1)`, `theta = i * 2.399963229728653`
(golden angle), which gives a stable, non-overlapping, repeatable layout with no
physics loop. `Heat = writes / max(writes)`.

## Build

```
dotnet run --project src/HydraDB.Cli -- ./hydradata
dotnet test tests/HydraDB.Tests
```

## Supported SQL

`CREATE TABLE` (INT / TEXT / BOOL, `PRIMARY KEY`, `FOREIGN KEY ... REFERENCES`),
`INSERT`, `SELECT` (projection, `WHERE`, `ORDER BY`, `LIMIT`), `UPDATE`, `DELETE`,
`BEGIN` / `COMMIT` / `ROLLBACK`.

## Known limits

No secondary indexes (scans are linear), no joins, no aggregates, DDL is not
rolled back on abort, single-process only.
