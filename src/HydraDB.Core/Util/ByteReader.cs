using System.Text;

namespace HydraDB.Core.Util;

/// <summary>Little-endian sequential reader, mirror of <see cref="ByteWriter"/>.</summary>
public sealed class ByteReader
{
    private readonly byte[] _buffer;
    private int _position;

    public ByteReader(byte[] buffer, int position = 0)
    {
        _buffer = buffer;
        _position = position;
    }

    public bool Eof => _position >= _buffer.Length;

    public byte Byte() => _buffer[_position++];

    public int Int()
    {
        int value = BitConverter.ToInt32(_buffer, _position);
        _position += 4;
        return value;
    }

    public long Long()
    {
        long value = BitConverter.ToInt64(_buffer, _position);
        _position += 8;
        return value;
    }

    public string Str()
    {
        int count = Int();
        string value = Encoding.UTF8.GetString(_buffer, _position, count);
        _position += count;
        return value;
    }

    public object? Value()
    {
        byte tag = Byte();
        switch (tag)
        {
            case 0: return null;
            case 1: return Long();
            case 2: return Str();
            case 3: return Byte() != 0;
            default: throw new InvalidDataException($"bad value tag {tag}");
        }
    }
}
