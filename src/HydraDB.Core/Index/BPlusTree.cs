using HydraDB.Core.Storage;
using HydraDB.Core.Util;

namespace HydraDB.Core.Index;

public sealed class ConstraintViolationException : Exception
{
    public ConstraintViolationException(string message) : base(message) { }
}

public enum PageType : byte
{
    Free = 0,
    Meta = 1,
    BTreeInternal = 2,
    BTreeLeaf = 3
}

/// <summary>
/// Full-page redo log for the index. Records are grouped: every page image belongs to a
/// group, and a group is only replayed if its terminating GroupEnd record is durable.
/// Because page images are idempotent, replay is safe to run any number of times.
/// </summary>
internal sealed class PageImageLog : IDisposable
{
    private const byte RecordPageImage = 1;
    private const byte RecordGroupEnd = 2;

    private readonly Wal _wal;

    public PageImageLog(string path) => _wal = new Wal(path);

    public long SizeBytes => _wal.SizeBytes;

    public void AppendPageImage(int pageNumber, long lsn, byte[] image)
    {
        var payload = new byte[13 + image.Length];
        payload[0] = RecordPageImage;
        BitConverter.TryWriteBytes(payload.AsSpan(1, 4), pageNumber);
        BitConverter.TryWriteBytes(payload.AsSpan(5, 8), lsn);
        image.CopyTo(payload.AsSpan(13));
        _wal.Append(payload);
    }

    public void AppendGroupEnd(long lsn)
    {
        var payload = new byte[9];
        payload[0] = RecordGroupEnd;
        BitConverter.TryWriteBytes(payload.AsSpan(1, 8), lsn);
        _wal.Append(payload);
    }

    public void Sync() => _wal.Sync();

    public void Reset() => _wal.Reset();

    /// <summary>Returns only complete groups. A trailing group without GroupEnd is dropped.</summary>
    public List<List<(int PageNumber, byte[] Image)>> ReplayGroups()
    {
        var groups = new List<List<(int, byte[])>>();
        var pending = new List<(int, byte[])>();

        foreach (byte[] payload in _wal.Replay())
        {
            if (payload.Length == 0) break;

            switch (payload[0])
            {
                case RecordPageImage:
                {
                    int pageNumber = BitConverter.ToInt32(payload, 1);
                    var image = new byte[payload.Length - 13];
                    payload.AsSpan(13).CopyTo(image);
                    pending.Add((pageNumber, image));
                    break;
                }
                case RecordGroupEnd:
                    groups.Add(pending);
                    pending = new List<(int, byte[])>();
                    break;
                default:
                    // Unknown record: stop, treat the rest as untrusted.
                    return groups;
            }
        }

        // pending is intentionally discarded: an unterminated group never happened.
        return groups;
    }

    public void Dispose() => _wal.Dispose();
}

/// <summary>
/// B+Tree primary index over 64-bit keys, stored in its own page file through the
/// existing <see cref="Pager"/>. Search is O(log_m n) with m computed from the page size.
///
/// Page header, 48 bytes:
///   0  u32 magic 'HBT1'
///   4  u8  page type
///   5  u8  key type (1 = INT64)
///   6  u16 slot count
///   8  i64 page LSN
///   16 i64 next leaf / root page id on the meta page
///   24 i64 prev leaf / allocated page count on the meta page
///   32 i64 reserved / free list head on the meta page
///   40 u32 CRC32 of the page with this field zeroed
///   44 u32 reserved
///
/// Leaf body:     entries of [key:i64][tupleId:i64] in ascending key order.
/// Internal body: children[m+1] at offset 48, then keys[m]. Child i covers keys &lt; keys[i],
///                child m covers the rest.
/// </summary>
public sealed class BPlusTree : IDisposable
{
    public const int HeaderSize = 48;
    public const int KeySize = 8;
    public const int ChildSize = 8;

    private const uint PageMagic = 0x48425431; // 'HBT1'
    private const byte KeyTypeInt64 = 1;

    private const int OffsetMagic = 0;
    private const int OffsetPageType = 4;
    private const int OffsetKeyType = 5;
    private const int OffsetSlotCount = 6;
    private const int OffsetPageLsn = 8;
    private const int OffsetNextLeaf = 16;
    private const int OffsetPrevLeaf = 24;
    private const int OffsetReserved = 32;
    private const int OffsetCrc = 40;

    private const int MetaOffsetRoot = OffsetNextLeaf;
    private const int MetaOffsetPageCount = OffsetPrevLeaf;
    private const int MetaOffsetFreeList = OffsetReserved;
    private const int MetaOffsetKeyCount = 56;
    private const int MetaOffsetHeight = 64;

    private const int MetaPage = 0;

    private readonly Pager _pager;
    private readonly PageImageLog _log;
    private readonly Dictionary<int, byte[]> _cache = new();
    private readonly SortedSet<int> _dirty = new();
    private readonly int _pageSize;
    private readonly int _order;

    private int _rootPageId;
    private int _pageCount;
    private int _freeListHead;
    private long _keyCount;
    private int _height;
    private long _lsn;

    public BPlusTree(string pagePath, string logPath)
    {
        _pager = new Pager(pagePath);
        _log = new PageImageLog(logPath);
        _pageSize = Pager.PageSize;
        _order = OrderFor(_pageSize);

        Recover();

        if (_pager.PageCount == 0) InitializeEmpty();
        else LoadMeta();
    }

    /// <summary>m = (pageSize - header - 16) / (keySize + 8). 8 KiB pages give m = 508.</summary>
    public static int OrderFor(int pageSize) => (pageSize - HeaderSize - 16) / (KeySize + ChildSize);

    public int Order => _order;
    public int Height => _height;
    public long KeyCount => _keyCount;
    public int RootPageId => _rootPageId;
    public long LogBytes => _log.SizeBytes;

    /// <summary>Test hook: append page images and fsync, but skip the GroupEnd marker and the page writes.</summary>
    public bool SimulateCrashBeforeGroupEnd { get; set; }

    // ---------------------------------------------------------------- search

    public long? Search(long key)
    {
        int pageNumber = _rootPageId;

        while (true)
        {
            byte[] page = Read(pageNumber);

            if (TypeOf(page) == PageType.BTreeLeaf)
            {
                int slot = LeafBinarySearch(page, key);
                return slot >= 0 ? LeafValue(page, slot) : null;
            }

            pageNumber = (int)Child(page, ChildIndexFor(page, key));
        }
    }

    /// <summary>Ascending range scan that walks sibling pointers instead of re-descending.</summary>
    public IEnumerable<(long Key, long TupleId)> Range(long fromKey, long toKey)
    {
        int pageNumber = _rootPageId;
        while (TypeOf(Read(pageNumber)) != PageType.BTreeLeaf)
            pageNumber = (int)Child(Read(pageNumber), ChildIndexFor(Read(pageNumber), fromKey));

        while (pageNumber >= 0)
        {
            byte[] leaf = Read(pageNumber);
            int count = SlotCount(leaf);

            for (int i = 0; i < count; i++)
            {
                long key = LeafKey(leaf, i);
                if (key < fromKey) continue;
                if (key > toKey) yield break;
                yield return (key, LeafValue(leaf, i));
            }

            long next = ReadInt64(leaf, OffsetNextLeaf);
            pageNumber = next < 0 ? -1 : (int)next;
        }
    }

    // ---------------------------------------------------------------- insert

    /// <summary>Inserts a new key. Throws when the key already exists.</summary>
    public void Insert(long key, long tupleId) => Write(key, tupleId, replaceExisting: false);

    /// <summary>Inserts, or repoints an existing key at a new tuple.</summary>
    public void Upsert(long key, long tupleId) => Write(key, tupleId, replaceExisting: true);

    private void Write(long key, long tupleId, bool replaceExisting)
    {
        Split split = InsertInto(_rootPageId, key, tupleId, replaceExisting);

        if (split.Happened)
        {
            int newRoot = Allocate();
            byte[] page = NewPage(PageType.BTreeInternal);
            SetSlotCount(page, 1);
            SetChild(page, 0, _rootPageId);
            SetChild(page, 1, split.RightPageId);
            SetKey(page, 0, split.SeparatorKey);
            MarkDirty(newRoot, page);

            _rootPageId = newRoot;
            _height++;
        }

        FlushGroup();
    }

    private readonly struct Split
    {
        public Split(long separatorKey, int rightPageId)
        {
            Happened = true;
            SeparatorKey = separatorKey;
            RightPageId = rightPageId;
        }

        public bool Happened { get; }
        public long SeparatorKey { get; }
        public int RightPageId { get; }
    }

    private Split InsertInto(int pageNumber, long key, long tupleId, bool replaceExisting)
    {
        byte[] page = Read(pageNumber);

        if (TypeOf(page) == PageType.BTreeLeaf)
        {
            int found = LeafBinarySearch(page, key);

            if (found >= 0)
            {
                if (!replaceExisting)
                    throw new ConstraintViolationException($"duplicate primary key {key}");

                SetLeafValue(page, found, tupleId);
                MarkDirty(pageNumber, page);
                return default;
            }

            int insertAt = ~found;
            _keyCount++;

            if (SlotCount(page) < _order)
            {
                LeafInsertAt(page, insertAt, key, tupleId);
                MarkDirty(pageNumber, page);
                return default;
            }

            return SplitLeaf(pageNumber, page, insertAt, key, tupleId);
        }

        int childIndex = ChildIndexFor(page, key);
        Split child = InsertInto((int)Child(page, childIndex), key, tupleId, replaceExisting);
        if (!child.Happened) return default;

        // Re-read: the recursive call may have evicted nothing, but the buffer is shared by design.
        page = Read(pageNumber);

        if (SlotCount(page) < _order)
        {
            InternalInsertAt(page, childIndex, child.SeparatorKey, child.RightPageId);
            MarkDirty(pageNumber, page);
            return default;
        }

        return SplitInternal(pageNumber, page, childIndex, child.SeparatorKey, child.RightPageId);
    }

    private Split SplitLeaf(int pageNumber, byte[] page, int insertAt, long key, long tupleId)
    {
        int count = SlotCount(page);
        int total = count + 1;

        var keys = new long[total];
        var values = new long[total];

        for (int i = 0; i < insertAt; i++)
        {
            keys[i] = LeafKey(page, i);
            values[i] = LeafValue(page, i);
        }

        keys[insertAt] = key;
        values[insertAt] = tupleId;

        for (int i = insertAt; i < count; i++)
        {
            keys[i + 1] = LeafKey(page, i);
            values[i + 1] = LeafValue(page, i);
        }

        int mid = total / 2;
        int rightPageId = Allocate();
        byte[] right = NewPage(PageType.BTreeLeaf);

        SetSlotCount(page, 0);
        for (int i = 0; i < mid; i++) LeafAppend(page, keys[i], values[i]);
        for (int i = mid; i < total; i++) LeafAppend(right, keys[i], values[i]);

        long oldNext = ReadInt64(page, OffsetNextLeaf);
        WriteInt64(page, OffsetNextLeaf, rightPageId);
        WriteInt64(right, OffsetPrevLeaf, pageNumber);
        WriteInt64(right, OffsetNextLeaf, oldNext);

        if (oldNext >= 0)
        {
            byte[] follower = Read((int)oldNext);
            WriteInt64(follower, OffsetPrevLeaf, rightPageId);
            MarkDirty((int)oldNext, follower);
        }

        MarkDirty(pageNumber, page);
        MarkDirty(rightPageId, right);

        // Leaf split copies the separator up; the key stays in the right leaf.
        return new Split(keys[mid], rightPageId);
    }

    private Split SplitInternal(int pageNumber, byte[] page, int childIndex, long separatorKey, int rightChildId)
    {
        int count = SlotCount(page);

        var keys = new long[count + 1];
        var children = new long[count + 2];

        for (int i = 0; i < childIndex; i++) keys[i] = Key(page, i);
        keys[childIndex] = separatorKey;
        for (int i = childIndex; i < count; i++) keys[i + 1] = Key(page, i);

        for (int i = 0; i <= childIndex; i++) children[i] = Child(page, i);
        children[childIndex + 1] = rightChildId;
        for (int i = childIndex + 1; i <= count; i++) children[i + 1] = Child(page, i);

        int mid = (count + 1) / 2;
        long pushedUp = keys[mid];

        int rightPageId = Allocate();
        byte[] right = NewPage(PageType.BTreeInternal);

        SetSlotCount(page, mid);
        for (int i = 0; i < mid; i++) SetKey(page, i, keys[i]);
        for (int i = 0; i <= mid; i++) SetChild(page, i, children[i]);

        int rightKeyCount = keys.Length - mid - 1;
        SetSlotCount(right, rightKeyCount);
        for (int i = 0; i < rightKeyCount; i++) SetKey(right, i, keys[mid + 1 + i]);
        for (int i = 0; i <= rightKeyCount; i++) SetChild(right, i, children[mid + 1 + i]);

        MarkDirty(pageNumber, page);
        MarkDirty(rightPageId, right);

        // Internal split pushes the separator up; it is removed from both halves.
        return new Split(pushedUp, rightPageId);
    }

    // ---------------------------------------------------------------- durability

    public void Checkpoint()
    {
        FlushGroup();
        _pager.Flush(true);
        _log.Reset();
    }

    /// <summary>Drops every key and returns to a single empty leaf root.</summary>
    public void Truncate()
    {
        _cache.Clear();
        _dirty.Clear();

        _pageCount = 2;
        _freeListHead = -1;
        _rootPageId = 1;
        _keyCount = 0;
        _height = 1;

        byte[] root = NewPage(PageType.BTreeLeaf);
        MarkDirty(_rootPageId, root);
        FlushGroup();
        _pager.Flush(true);
        _log.Reset();
    }

    private void FlushGroup()
    {
        if (_dirty.Count == 0) return;

        long lsn = ++_lsn;
        WriteMeta();

        // Log first: seal every image, append it, terminate the group, fsync.
        foreach (int pageNumber in _dirty)
        {
            byte[] page = _cache[pageNumber];
            WriteInt64(page, OffsetPageLsn, lsn);
            Seal(page);
            _log.AppendPageImage(pageNumber, lsn, page);
        }

        if (SimulateCrashBeforeGroupEnd)
        {
            _log.Sync();
            _dirty.Clear();
            return;
        }

        _log.AppendGroupEnd(lsn);
        _log.Sync();

        // Only now touch the page file. A crash here is covered by replay.
        foreach (int pageNumber in _dirty) _pager.Write(pageNumber, _cache[pageNumber]);

        _dirty.Clear();
    }

    private void Recover()
    {
        List<List<(int PageNumber, byte[] Image)>> groups = _log.ReplayGroups();
        if (groups.Count == 0)
        {
            _log.Reset();
            return;
        }

        foreach (var group in groups)
            foreach ((int pageNumber, byte[] image) in group)
                _pager.Write(pageNumber, image);

        _pager.Flush(true);
        _log.Reset();
        _cache.Clear();
    }

    private void InitializeEmpty()
    {
        _pageCount = 2;
        _freeListHead = -1;
        _rootPageId = 1;
        _keyCount = 0;
        _height = 1;

        byte[] root = NewPage(PageType.BTreeLeaf);
        MarkDirty(_rootPageId, root);
        FlushGroup();
        _pager.Flush(true);
    }

    private void LoadMeta()
    {
        byte[] meta = Read(MetaPage);
        if (TypeOf(meta) != PageType.Meta)
            throw new InvalidDataException("index meta page is corrupt");

        _rootPageId = (int)ReadInt64(meta, MetaOffsetRoot);
        _pageCount = (int)ReadInt64(meta, MetaOffsetPageCount);
        _freeListHead = (int)ReadInt64(meta, MetaOffsetFreeList);
        _keyCount = ReadInt64(meta, MetaOffsetKeyCount);
        _height = (int)ReadInt64(meta, MetaOffsetHeight);
        _lsn = ReadInt64(meta, OffsetPageLsn);
    }

    private void WriteMeta()
    {
        byte[] meta = _cache.TryGetValue(MetaPage, out byte[]? cached) ? cached : NewPage(PageType.Meta);
        WriteInt64(meta, MetaOffsetRoot, _rootPageId);
        WriteInt64(meta, MetaOffsetPageCount, _pageCount);
        WriteInt64(meta, MetaOffsetFreeList, _freeListHead);
        WriteInt64(meta, MetaOffsetKeyCount, _keyCount);
        WriteInt64(meta, MetaOffsetHeight, _height);
        MarkDirty(MetaPage, meta);
    }

    private int Allocate()
    {
        if (_freeListHead >= 0)
        {
            int recycled = _freeListHead;
            byte[] page = Read(recycled);
            _freeListHead = (int)ReadInt64(page, OffsetReserved);
            return recycled;
        }

        return _pageCount++;
    }

    // ---------------------------------------------------------------- page access

    private byte[] Read(int pageNumber)
    {
        if (_cache.TryGetValue(pageNumber, out byte[]? cached)) return cached;

        byte[] page = _pager.Read(pageNumber);

        if (BitConverter.ToUInt32(page, OffsetMagic) != PageMagic)
            throw new InvalidDataException($"index page {pageNumber} is not a HydraDB btree page");
        if (!VerifyCrc(page))
            throw new InvalidDataException($"index page {pageNumber} failed its CRC check");

        _cache[pageNumber] = page;
        return page;
    }

    private void MarkDirty(int pageNumber, byte[] page)
    {
        _cache[pageNumber] = page;
        _dirty.Add(pageNumber);
    }

    private byte[] NewPage(PageType type)
    {
        var page = new byte[_pageSize];
        BitConverter.TryWriteBytes(page.AsSpan(OffsetMagic, 4), PageMagic);
        page[OffsetPageType] = (byte)type;
        page[OffsetKeyType] = KeyTypeInt64;
        SetSlotCount(page, 0);
        WriteInt64(page, OffsetNextLeaf, -1);
        WriteInt64(page, OffsetPrevLeaf, -1);
        WriteInt64(page, OffsetReserved, -1);
        return page;
    }

    private static void Seal(byte[] page)
    {
        BitConverter.TryWriteBytes(page.AsSpan(OffsetCrc, 4), 0u);
        BitConverter.TryWriteBytes(page.AsSpan(OffsetCrc, 4), Crc32.Compute(page));
    }

    private static bool VerifyCrc(byte[] page)
    {
        uint stored = BitConverter.ToUInt32(page, OffsetCrc);
        BitConverter.TryWriteBytes(page.AsSpan(OffsetCrc, 4), 0u);
        uint actual = Crc32.Compute(page);
        BitConverter.TryWriteBytes(page.AsSpan(OffsetCrc, 4), stored);
        return stored == actual;
    }

    private static PageType TypeOf(byte[] page) => (PageType)page[OffsetPageType];

    private static int SlotCount(byte[] page) => BitConverter.ToUInt16(page, OffsetSlotCount);

    private static void SetSlotCount(byte[] page, int count) =>
        BitConverter.TryWriteBytes(page.AsSpan(OffsetSlotCount, 2), (ushort)count);

    private static long ReadInt64(byte[] page, int offset) => BitConverter.ToInt64(page, offset);

    private static void WriteInt64(byte[] page, int offset, long value) =>
        BitConverter.TryWriteBytes(page.AsSpan(offset, 8), value);

    // Leaf body: [key][tupleId] pairs.
    private static int LeafOffset(int slot) => HeaderSize + slot * 16;

    private static long LeafKey(byte[] page, int slot) => ReadInt64(page, LeafOffset(slot));

    private static long LeafValue(byte[] page, int slot) => ReadInt64(page, LeafOffset(slot) + 8);

    private static void SetLeafValue(byte[] page, int slot, long tupleId) =>
        WriteInt64(page, LeafOffset(slot) + 8, tupleId);

    private static void LeafAppend(byte[] page, long key, long tupleId)
    {
        int slot = SlotCount(page);
        WriteInt64(page, LeafOffset(slot), key);
        WriteInt64(page, LeafOffset(slot) + 8, tupleId);
        SetSlotCount(page, slot + 1);
    }

    private static void LeafInsertAt(byte[] page, int slot, long key, long tupleId)
    {
        int count = SlotCount(page);
        for (int i = count - 1; i >= slot; i--)
        {
            WriteInt64(page, LeafOffset(i + 1), LeafKey(page, i));
            WriteInt64(page, LeafOffset(i + 1) + 8, LeafValue(page, i));
        }

        WriteInt64(page, LeafOffset(slot), key);
        WriteInt64(page, LeafOffset(slot) + 8, tupleId);
        SetSlotCount(page, count + 1);
    }

    /// <summary>Returns the slot when found, otherwise the bitwise complement of the insertion point.</summary>
    private static int LeafBinarySearch(byte[] page, long key)
    {
        int low = 0;
        int high = SlotCount(page) - 1;

        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            long probe = LeafKey(page, mid);

            if (probe == key) return mid;
            if (probe < key) low = mid + 1;
            else high = mid - 1;
        }

        return ~low;
    }

    // Internal body: children[m+1] then keys[m].
    private int ChildOffset(int index) => HeaderSize + index * ChildSize;

    private int KeyOffset(int index) => HeaderSize + (_order + 1) * ChildSize + index * KeySize;

    private long Child(byte[] page, int index) => ReadInt64(page, ChildOffset(index));

    private void SetChild(byte[] page, int index, long pageNumber) =>
        WriteInt64(page, ChildOffset(index), pageNumber);

    private long Key(byte[] page, int index) => ReadInt64(page, KeyOffset(index));

    private void SetKey(byte[] page, int index, long key) => WriteInt64(page, KeyOffset(index), key);

    /// <summary>First child whose subtree can hold the key: the smallest i with key &lt; keys[i], else m.</summary>
    private int ChildIndexFor(byte[] page, long key)
    {
        int low = 0;
        int high = SlotCount(page);

        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (key < Key(page, mid)) high = mid;
            else low = mid + 1;
        }

        return low;
    }

    private void InternalInsertAt(byte[] page, int keyIndex, long key, int rightChildId)
    {
        int count = SlotCount(page);

        for (int i = count - 1; i >= keyIndex; i--) SetKey(page, i + 1, Key(page, i));
        for (int i = count; i >= keyIndex + 1; i--) SetChild(page, i + 1, Child(page, i));

        SetKey(page, keyIndex, key);
        SetChild(page, keyIndex + 1, rightChildId);
        SetSlotCount(page, count + 1);
    }

    // ---------------------------------------------------------------- invariants

    /// <summary>Structural self-check used by tests: ordering, key ranges, and leaf chain.</summary>
    public void Validate()
    {
        long leafKeys = ValidateSubtree(_rootPageId, long.MinValue, long.MaxValue, out int depth);

        if (depth != _height)
            throw new InvalidDataException($"height {_height} disagrees with measured depth {depth}");

        long chained = 0;
        int pageNumber = LeftmostLeaf();
        long previous = long.MinValue;

        while (pageNumber >= 0)
        {
            byte[] leaf = Read(pageNumber);
            int count = SlotCount(leaf);

            for (int i = 0; i < count; i++)
            {
                long key = LeafKey(leaf, i);
                if (chained > 0 && key <= previous)
                    throw new InvalidDataException("leaf chain is not strictly ascending");
                previous = key;
                chained++;
            }

            long next = ReadInt64(leaf, OffsetNextLeaf);
            pageNumber = next < 0 ? -1 : (int)next;
        }

        if (chained != leafKeys)
            throw new InvalidDataException($"leaf chain holds {chained} keys but the tree holds {leafKeys}");
    }

    private long ValidateSubtree(int pageNumber, long lowerBound, long upperBound, out int depth)
    {
        byte[] page = Read(pageNumber);
        int count = SlotCount(page);

        if (TypeOf(page) == PageType.BTreeLeaf)
        {
            depth = 1;
            for (int i = 0; i < count; i++)
            {
                long key = LeafKey(page, i);
                if (key < lowerBound || key > upperBound)
                    throw new InvalidDataException($"key {key} escapes its subtree bounds");
                if (i > 0 && LeafKey(page, i - 1) >= key)
                    throw new InvalidDataException("leaf keys are not sorted");
            }
            return count;
        }

        if (count == 0) throw new InvalidDataException("internal page has no separators");

        long total = 0;
        int childDepth = -1;

        for (int i = 0; i <= count; i++)
        {
            long low = i == 0 ? lowerBound : Key(page, i - 1);
            long high = i == count ? upperBound : Key(page, i) - 1;
            total += ValidateSubtree((int)Child(page, i), low, high, out int measured);

            if (childDepth < 0) childDepth = measured;
            else if (childDepth != measured)
                throw new InvalidDataException("tree is not height balanced");
        }

        depth = childDepth + 1;
        return total;
    }

    private int LeftmostLeaf()
    {
        int pageNumber = _rootPageId;
        while (TypeOf(Read(pageNumber)) != PageType.BTreeLeaf)
            pageNumber = (int)Child(Read(pageNumber), 0);
        return pageNumber;
    }

    public void Dispose()
    {
        FlushGroup();
        _pager.Flush(true);
        _log.Dispose();
        _pager.Dispose();
    }
}
