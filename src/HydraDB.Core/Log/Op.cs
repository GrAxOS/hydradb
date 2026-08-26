using HydraDB.Core.Catalog;
using HydraDB.Core.Util;

namespace HydraDB.Core.Log;

public enum OpKind : byte
{
    CreateTable = 1,
    Insert = 2,
    Delete = 3
}

/// <summary>A single redo operation. An UPDATE is logged as Delete followed by Insert with the same row id.</summary>
public sealed class Op
{
    public OpKind Kind;
    public string Table = string.Empty;
    public long RowId;
    public object?[]? Values;
    public TableDef? Def;

    public static Op CreateTable(TableDef def) =>
        new Op { Kind = OpKind.CreateTable, Table = def.Name, Def = def };

    public static Op Insert(string table, long rowId, object?[] values) =>
        new Op { Kind = OpKind.Insert, Table = table, RowId = rowId, Values = values };

    public static Op Delete(string table, long rowId) =>
        new Op { Kind = OpKind.Delete, Table = table, RowId = rowId };
}

/// <summary>Encodes a whole transaction as one WAL record, so commits are atomic by construction.</summary>
public static class OpCodec
{
    public static byte[] EncodeCommit(long tid, List<Op> ops)
    {
        var writer = new ByteWriter();
        writer.Long(tid);
        writer.Int(ops.Count);
        foreach (Op op in ops) Encode(writer, op);
        return writer.ToArray();
    }

    public static List<Op> DecodeCommit(byte[] payload)
    {
        var reader = new ByteReader(payload);
        reader.Long();
        int count = reader.Int();
        var ops = new List<Op>(count);
        for (int i = 0; i < count; i++) ops.Add(Decode(reader));
        return ops;
    }

    private static void Encode(ByteWriter writer, Op op)
    {
        writer.Byte((byte)op.Kind);
        writer.Str(op.Table);

        switch (op.Kind)
        {
            case OpKind.CreateTable:
            {
                TableDef def = op.Def ?? throw new InvalidOperationException("CreateTable op without a definition");
                writer.Int(def.Columns.Count);
                foreach (Column column in def.Columns)
                {
                    writer.Str(column.Name);
                    writer.Byte((byte)column.Type);
                    writer.Byte((byte)(column.PrimaryKey ? 1 : 0));
                }
                writer.Int(def.ForeignKeys.Count);
                foreach (ForeignKey fk in def.ForeignKeys)
                {
                    writer.Str(fk.Column);
                    writer.Str(fk.ReferencedTable);
                    writer.Str(fk.ReferencedColumn);
                }
                break;
            }
            case OpKind.Insert:
            {
                object?[] values = op.Values ?? throw new InvalidOperationException("Insert op without values");
                writer.Long(op.RowId);
                writer.Int(values.Length);
                foreach (object? value in values) writer.Value(value);
                break;
            }
            case OpKind.Delete:
                writer.Long(op.RowId);
                break;
            default:
                throw new InvalidOperationException($"unknown op kind {op.Kind}");
        }
    }

    private static Op Decode(ByteReader reader)
    {
        var kind = (OpKind)reader.Byte();
        string table = reader.Str();

        switch (kind)
        {
            case OpKind.CreateTable:
            {
                int columnCount = reader.Int();
                var columns = new List<Column>(columnCount);
                for (int i = 0; i < columnCount; i++)
                {
                    string name = reader.Str();
                    var type = (ColumnType)reader.Byte();
                    bool primaryKey = reader.Byte() != 0;
                    columns.Add(new Column(name, type, primaryKey));
                }

                int fkCount = reader.Int();
                var foreignKeys = new List<ForeignKey>(fkCount);
                for (int i = 0; i < fkCount; i++)
                {
                    string column = reader.Str();
                    string refTable = reader.Str();
                    string refColumn = reader.Str();
                    foreignKeys.Add(new ForeignKey(column, refTable, refColumn));
                }

                return Op.CreateTable(new TableDef(table, columns, foreignKeys));
            }
            case OpKind.Insert:
            {
                long rowId = reader.Long();
                int valueCount = reader.Int();
                var values = new object?[valueCount];
                for (int i = 0; i < valueCount; i++) values[i] = reader.Value();
                return Op.Insert(table, rowId, values);
            }
            case OpKind.Delete:
                return Op.Delete(table, reader.Long());
            default:
                throw new InvalidDataException($"unknown op kind {kind}");
        }
    }
}
