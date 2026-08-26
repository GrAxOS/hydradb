namespace HydraDB.Core.Storage;

/// <summary>Fixed-size page file. All snapshot I/O goes through here.</summary>
public sealed class Pager : IDisposable
{
    public const int PageSize = 4096;

    private readonly FileStream _file;

    public Pager(string path)
    {
        _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            PageSize, FileOptions.RandomAccess);
    }

    public int PageCount => (int)((_file.Length + PageSize - 1) / PageSize);

    public byte[] Read(int pageNumber)
    {
        var page = new byte[PageSize];
        _file.Seek((long)pageNumber * PageSize, SeekOrigin.Begin);
        int read = 0;
        while (read < PageSize)
        {
            int n = _file.Read(page, read, PageSize - read);
            if (n == 0) break;
            read += n;
        }
        return page;
    }

    public void Write(int pageNumber, byte[] page)
    {
        if (page.Length != PageSize)
            throw new ArgumentException($"page must be exactly {PageSize} bytes", nameof(page));
        _file.Seek((long)pageNumber * PageSize, SeekOrigin.Begin);
        _file.Write(page, 0, PageSize);
    }

    public void Truncate() => _file.SetLength(0);

    public void Flush(bool durable) => _file.Flush(durable);

    public void Dispose()
    {
        _file.Flush(true);
        _file.Dispose();
    }
}
