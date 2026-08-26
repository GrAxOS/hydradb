using HydraDB.Core.Util;

namespace HydraDB.Core.Storage;

/// <summary>
/// Append-only write-ahead log. Record frame: [length:i32][crc32:u32][payload].
/// Replay stops at the first record with an implausible length, a short read, or a CRC
/// mismatch, and the file is truncated to the last fully durable record.
/// </summary>
public sealed class Wal : IDisposable
{
    private const int MaxRecordBytes = 64 * 1024 * 1024;

    private readonly FileStream _file;

    public Wal(string path)
    {
        _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            1 << 16, FileOptions.SequentialScan);
        _file.Seek(0, SeekOrigin.End);
    }

    public long SizeBytes => _file.Length;

    public void Append(byte[] payload)
    {
        Span<byte> header = stackalloc byte[8];
        BitConverter.TryWriteBytes(header[..4], payload.Length);
        BitConverter.TryWriteBytes(header[4..], Crc32.Compute(payload));
        _file.Write(header);
        _file.Write(payload, 0, payload.Length);
    }

    public void Sync() => _file.Flush(true);

    public List<byte[]> Replay()
    {
        var records = new List<byte[]>();
        _file.Seek(0, SeekOrigin.Begin);
        long lastGoodOffset = 0;
        var header = new byte[8];

        while (true)
        {
            if (!ReadExactly(header, 8)) break;
            int length = BitConverter.ToInt32(header, 0);
            uint crc = BitConverter.ToUInt32(header, 4);
            if (length < 0 || length > MaxRecordBytes) break;

            var payload = new byte[length];
            if (!ReadExactly(payload, length)) break;
            if (Crc32.Compute(payload) != crc) break;

            records.Add(payload);
            lastGoodOffset = _file.Position;
        }

        if (_file.Length != lastGoodOffset)
        {
            _file.SetLength(lastGoodOffset);
            _file.Flush(true);
        }

        _file.Seek(0, SeekOrigin.End);
        return records;
    }

    public void Reset()
    {
        _file.SetLength(0);
        _file.Flush(true);
        _file.Seek(0, SeekOrigin.End);
    }

    private bool ReadExactly(byte[] target, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = _file.Read(target, read, count - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public void Dispose()
    {
        _file.Flush(true);
        _file.Dispose();
    }
}
