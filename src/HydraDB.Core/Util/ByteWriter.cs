using System.Text;

namespace HydraDB.Core.Util;

/// <summary>Little-endian append-only binary writer with a growing backing array.</summary>
public sealed class ByteWriter
{
    private byte[] _buffer = new byte[256];
    private int _length;

    public int Length => _length;

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length) return;
        int capacity = _buffer.Length;
        while (capacity < _length + extra) capacity *= 2;
        Array.Resize(ref _buffer, capacity);
    }

    public void Byte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    public void Int(int value)
    {
        Ensure(4);
        BitConverter.TryWriteBytes(_buffer.AsSpan(_length, 4), value);
        _length += 4;
    }

    public void Long(long value)
    {
        Ensure(8);
        BitConverter.TryWriteBytes(_buffer.AsSpan(_length, 8), value);
        _length += 8;
    }

    public void Str(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Int(bytes.Length);
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    public void Value(object? value)
    {
        switch (value)
        {
            case null:
                Byte(0);
                break;
            case long l:
                Byte(1);
                Long(l);
                break;
            case string s:
                Byte(2);
                Str(s);
                break;
            case bool b:
                Byte(3);
                Byte((byte)(b ? 1 : 0));
                break;
            default:
                throw new NotSupportedException($"unsupported value type {value.GetType().Name}");
        }
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();
}
