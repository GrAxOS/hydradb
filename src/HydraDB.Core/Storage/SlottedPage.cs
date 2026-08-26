namespace HydraDB.Core.Storage;

/// <summary>
/// Slotted page layout:
/// [slotCount:i32][freeEnd:i32][slot0..slotN-1 (offset:u16, length:u16)] ... free ... [recordN-1..record0]
/// Slots grow forward from the header, records grow backward from the end of the page.
/// </summary>
public sealed class SlottedPage
{
    private const int HeaderSize = 8;
    private const int SlotSize = 4;

    private readonly byte[] _buffer;

    public SlottedPage()
    {
        _buffer = new byte[Pager.PageSize];
        SlotCount = 0;
        FreeEnd = Pager.PageSize;
    }

    public SlottedPage(byte[] buffer)
    {
        if (buffer.Length != Pager.PageSize)
            throw new ArgumentException("buffer is not a page", nameof(buffer));
        _buffer = buffer;
    }

    public byte[] Buffer => _buffer;

    public int SlotCount
    {
        get => BitConverter.ToInt32(_buffer, 0);
        private set => BitConverter.GetBytes(value).CopyTo(_buffer, 0);
    }

    public int FreeEnd
    {
        get => BitConverter.ToInt32(_buffer, 4);
        private set => BitConverter.GetBytes(value).CopyTo(_buffer, 4);
    }

    public int FreeSpace => FreeEnd - (HeaderSize + SlotCount * SlotSize);

    public bool TryAdd(ReadOnlySpan<byte> record)
    {
        if (record.Length + SlotSize > FreeSpace) return false;

        int offset = FreeEnd - record.Length;
        record.CopyTo(_buffer.AsSpan(offset, record.Length));

        int slotPosition = HeaderSize + SlotCount * SlotSize;
        BitConverter.GetBytes((ushort)offset).CopyTo(_buffer, slotPosition);
        BitConverter.GetBytes((ushort)record.Length).CopyTo(_buffer, slotPosition + 2);

        FreeEnd = offset;
        SlotCount += 1;
        return true;
    }

    public ReadOnlySpan<byte> Get(int index)
    {
        if (index < 0 || index >= SlotCount) throw new ArgumentOutOfRangeException(nameof(index));
        int slotPosition = HeaderSize + index * SlotSize;
        int offset = BitConverter.ToUInt16(_buffer, slotPosition);
        int length = BitConverter.ToUInt16(_buffer, slotPosition + 2);
        return _buffer.AsSpan(offset, length);
    }
}
