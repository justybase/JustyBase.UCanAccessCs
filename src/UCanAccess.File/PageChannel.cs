namespace UCanAccess.File;

/// <summary>
/// Reads and writes database pages in an MS Access file (port of Jackcess <c>PageChannel</c>).
/// </summary>
public sealed class PageChannel : IDisposable
{
    private readonly Stream _channel;
    private readonly JetFormat _format;
    private readonly IAccessPageCodec? _codec;
    private readonly bool _closeChannel;
    private readonly object _sync = new();
    private bool _disposed;
    private UsageMap? _globalUsageMap;
    private int _writeCount;
    private int _batchDepth;
    private readonly Dictionary<int, byte[]> _dirtyPages = new();

    internal PageChannel(Stream channel, JetFormat format, bool closeChannel, IAccessPageCodec? codec = null)
    {
        _channel = channel;
        _format = format;
        _closeChannel = closeChannel;
        _codec = codec;
    }

    public JetFormat Format => _format;

    public long FileSize
    {
        get
        {
            lock (_sync)
            {
                return _channel.Length;
            }
        }
    }

    public bool IsWritable => _channel.CanWrite;

    /// <summary>
    /// Reads the given page into the buffer (which must be at least <see cref="JetFormat.PageSize"/> bytes).
    /// </summary>
    public void ReadPage(byte[] buffer, int pageNumber)
    {
        lock (_sync)
        {
            EnsureUsable();
            ValidateBuffer(buffer);
            if (pageNumber == 0)
            {
                ReadRootPage(buffer);
                return;
            }
            ValidatePageNumber(pageNumber);

            if (_dirtyPages.TryGetValue(pageNumber, out byte[]? dirtyPage))
            {
                Array.Copy(dirtyPage, buffer, _format.PageSize);
                return;
            }

            long offset = GetPageOffset(pageNumber);
            int bytesRead = ReadAt(buffer, 0, _format.PageSize, offset);
            if (bytesRead != _format.PageSize)
            {
                throw new DatabaseException(
                    $"Failed attempting to read {_format.PageSize} bytes from page {pageNumber}, only read {bytesRead}");
            }
            _codec?.DecodePage(pageNumber, buffer, buffer);
        }
    }

    /// <summary>
    /// Writes the given page (or part of a page) to disk.
    /// </summary>
    public void WritePage(byte[] page, int pageNumber, int pageOffset = 0)
    {
        lock (_sync)
        {
            EnsureUsable();
            AssertWritable();
            ValidateBuffer(page);
            if (pageOffset < 0 || pageOffset > _format.PageSize)
            {
                throw new ArgumentOutOfRangeException(nameof(pageOffset));
            }
            ValidatePageNumber(pageNumber);

            if (pageNumber == 0 && _codec == null)
            {
                // re-mask header
                ApplyHeaderMask(page);
            }
            try
            {
                if (_batchDepth > 0)
                {
                    CacheDirtyPage(page, pageNumber, pageOffset);
                }
                else
                {
                    WriteEncodedPage(page, pageNumber, pageOffset);
                }
            }
            finally
            {
                if (pageNumber == 0 && _codec == null)
                {
                    // de-mask header again so in-memory buffer stays usable
                    ApplyHeaderMask(page);
                }
            }
        }
    }

    /// <summary>
    /// Allocates a new page at the end of the database, marking it used in the global usage map.
    /// </summary>
    public int AllocateNewPage()
    {
        lock (_sync)
        {
            EnsureUsable();
            AssertWritable();
            long size = _channel.Length;
            if (size >= _format.MaxDatabaseSize)
            {
                throw new DatabaseException($"Database is at maximum size {_format.MaxDatabaseSize}");
            }
            if (size % _format.PageSize != 0)
            {
                throw new DatabaseException(
                    $"Database corrupted, file size {size} is not multiple of page size {_format.PageSize}");
            }

            // push a single byte to the end of the file to extend it by a page worth
            _channel.Position = size + _format.PageSize - 1;
            _channel.WriteByte(0);

            int pageNumber = (int)(size / _format.PageSize);
            GetGlobalUsageMap().RemovePageNumber(pageNumber);
            if (_codec != null)
            {
                WritePage(new byte[_format.PageSize], pageNumber);
            }
            return pageNumber;
        }
    }

    /// <summary>
    /// Deallocates a previously used page, returning it to the global usage map.
    /// </summary>
    public void DeallocatePage(int pageNumber)
    {
        lock (_sync)
        {
            EnsureUsable();
            AssertWritable();
            if (pageNumber <= 1)
            {
                throw new DatabaseException($"Cannot deallocate reserved page {pageNumber}");
            }
            ValidatePageNumber(pageNumber);

            // wipe the page header
            _dirtyPages.Remove(pageNumber);
            if (_codec == null)
            {
                var invalid = new byte[] { PageTypes.Invalid, 0, 0, 0 };
                WriteAt(invalid, 0, invalid.Length, GetPageOffset(pageNumber));
            }
            else
            {
                var page = new byte[_format.PageSize];
                ReadPage(page, pageNumber);
                page[0] = PageTypes.Invalid;
                page[1] = page[2] = page[3] = 0;
                WritePage(page, pageNumber);
            }

            GetGlobalUsageMap().AddPageNumber(pageNumber);
        }
    }

    /// <summary>
    /// The global usage map (page 1, row 0); pages outside its range are assumed "on".
    /// </summary>
    internal UsageMap GetGlobalUsageMap()
    {
        lock (_sync)
        {
            EnsureUsable();
            if (_globalUsageMap == null)
            {
                _globalUsageMap = UsageMap.ReadGlobal(this, 1, 0);
            }
            return _globalUsageMap;
        }
    }

    public void StartWrite()
    {
        lock (_sync)
        {
            EnsureUsable();
            _writeCount++;
        }
    }

    /// <summary>whether a logical write operation is in progress</summary>
    internal bool IsWriting => _writeCount > 0;

    public void FinishWrite()
    {
        lock (_sync)
        {
            EnsureUsable();
            if (_writeCount > 0)
            {
                _writeCount--;
            }
            if (_writeCount == 0 && _channel.CanWrite)
            {
                _channel.Flush();
            }
        }
    }

    internal void BeginBatch()
    {
        lock (_sync)
        {
            EnsureUsable();
            AssertWritable();
            _batchDepth++;
            _writeCount++;
        }
    }

    internal void FinishBatch()
    {
        lock (_sync)
        {
            EnsureUsable();
            if (_batchDepth == 0)
            {
                throw new InvalidOperationException("No write batch is active.");
            }
            _batchDepth--;
            if (_writeCount > 0)
            {
                _writeCount--;
            }
            if (_batchDepth == 0)
            {
                FlushDirtyPages();
                if (_channel.CanWrite)
                {
                    _channel.Flush();
                }
            }
        }
    }

    private void AssertWritable()
    {
        if (!_channel.CanWrite)
        {
            throw new DatabaseException("The database was opened read-only.");
        }
    }

    private int WriteAt(byte[] buffer, int offset, int length, long position)
    {
        if (_channel.CanSeek)
        {
            _channel.Position = position;
        }
        _channel.Write(buffer, offset, length);
        return length;
    }

    private void CacheDirtyPage(byte[] page, int pageNumber, int pageOffset)
    {
        byte[] source = page;
        if (pageNumber == 0 && _codec == null)
        {
            source = page.AsSpan(0, _format.PageSize).ToArray();
            ApplyHeaderMask(source);
        }

        if (!_dirtyPages.TryGetValue(pageNumber, out byte[]? dirtyPage))
        {
            dirtyPage = new byte[_format.PageSize];
            if (pageOffset != 0)
            {
                if (pageNumber == 0)
                {
                    ReadRootPage(dirtyPage);
                }
                else
                {
                    ReadPage(dirtyPage, pageNumber);
                }
            }
            _dirtyPages[pageNumber] = dirtyPage;
        }

        Array.Copy(source, pageOffset, dirtyPage, pageOffset, _format.PageSize - pageOffset);
    }

    private void FlushDirtyPages()
    {
        foreach ((int pageNumber, byte[] page) in _dirtyPages.OrderBy(entry => entry.Key))
        {
            if (pageNumber == 0 && _codec == null)
            {
                ApplyHeaderMask(page);
                try
                {
                    WriteEncodedPage(page, pageNumber, 0);
                }
                finally
                {
                    ApplyHeaderMask(page);
                }
            }
            else
            {
                WriteEncodedPage(page, pageNumber, 0);
            }
        }
        _dirtyPages.Clear();
    }

    /// <summary>
    /// Special method for reading the root page, de-masking the header.
    /// </summary>
    public void ReadRootPage(byte[] buffer)
    {
        lock (_sync)
        {
            EnsureUsable();
            ValidateBuffer(buffer);
            if (_dirtyPages.TryGetValue(0, out byte[]? dirtyPage))
            {
                Array.Copy(dirtyPage, buffer, _format.PageSize);
                return;
            }
            int bytesRead = ReadAt(buffer, 0, _format.PageSize, 0L);
            if (bytesRead != _format.PageSize)
            {
                throw new DatabaseException(
                    $"Failed attempting to read {_format.PageSize} bytes from page 0, only read {bytesRead}");
            }
            _codec?.DecodePage(0, buffer, buffer);
            ApplyHeaderMask(buffer);
        }
    }

    private int ReadAt(byte[] buffer, int offset, int length, long position)
    {
        if (_channel.CanSeek)
        {
            _channel.Position = position;
        }
        int total = 0;
        while (total < length)
        {
            int read = _channel.Read(buffer, offset + total, length - total);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    private void ValidatePageNumber(int pageNumber)
    {
        int nextPageNumber = (int)(_channel.Length / _format.PageSize);
        if (pageNumber <= PageChannelImpl.InvalidPageNumber || pageNumber >= nextPageNumber)
        {
            throw new DatabaseException($"invalid page number {pageNumber}");
        }
    }

    private long GetPageOffset(int pageNumber) => (long)pageNumber * _format.PageSize;

    private void WriteEncodedPage(byte[] page, int pageNumber, int pageOffset)
    {
        if (_codec == null)
        {
            WriteAt(page, pageOffset, _format.PageSize - pageOffset,
                GetPageOffset(pageNumber) + pageOffset);
            return;
        }

        // Encryption is page-based.  Merge a partial logical update with the
        // current page before encoding so no CBC block is written in isolation.
        byte[] logical = page;
        if (pageOffset != 0)
        {
            logical = new byte[_format.PageSize];
            if (pageNumber == 0)
            {
                ReadRootPage(logical);
            }
            else
            {
                ReadPage(logical, pageNumber);
            }
            Array.Copy(page, pageOffset, logical, pageOffset, _format.PageSize - pageOffset);
        }

        byte[] encoded = new byte[_format.PageSize];
        if (pageNumber == 0)
        {
            ApplyHeaderMask(logical);
            try
            {
                _codec.EncodePage(pageNumber, logical, encoded);
            }
            finally
            {
                ApplyHeaderMask(logical);
            }
        }
        else
        {
            _codec.EncodePage(pageNumber, logical, encoded);
        }
        WriteAt(encoded, 0, _format.PageSize, GetPageOffset(pageNumber));
    }

    private void ValidateBuffer(byte[] buffer)
    {
        if (buffer.Length < _format.PageSize)
        {
            throw new ArgumentException(
                $"Page buffer must contain at least {_format.PageSize} bytes.", nameof(buffer));
        }
    }

    private void EnsureUsable()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PageChannel));
        }
    }

    /// <summary>
    /// Applies (de-obfuscates / re-obfuscates) the header mask on the root page.
    /// </summary>
    internal void ApplyHeaderMask(byte[] buffer)
    {
        byte[] headerMask = _format.HeaderMask;
        for (int idx = 0; idx < headerMask.Length; ++idx)
        {
            int pos = idx + _format.OffsetMaskedHeader;
            buffer[pos] = (byte)(buffer[pos] ^ headerMask[idx]);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_dirtyPages.Count > 0)
                {
                    FlushDirtyPages();
                    _channel.Flush();
                }
            }
            finally
            {
                _disposed = true;
                try
                {
                    _codec?.Dispose();
                }
                finally
                {
                    if (_closeChannel)
                    {
                        _channel.Dispose();
                    }
                }
            }
        }
    }
}

internal static class PageChannelImpl
{
    public const int InvalidPageNumber = -1;
}
