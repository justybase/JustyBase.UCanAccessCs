using System.Collections;

namespace UCanAccess.File;

/// <summary>
/// Describes which database pages a particular table uses (port of Jackcess <c>UsageMap</c>).
/// </summary>
internal sealed class UsageMap
{
    /// <summary>inline map type</summary>
    private const byte MapTypeInline = 0x0;

    /// <summary>reference map type, for maps that are too large to fit inline</summary>
    private const byte MapTypeReference = 0x1;

    private readonly PageChannel _pageChannel;
    private readonly JetFormat _format;
    private readonly byte[] _tableBuffer;
    private readonly int _tablePageNum;
    private readonly int _rowStart;
    private int _startOffset;
    private int _startPage;
    private int _endPage;
    private bool _isGlobal;
    private bool _isReference;
    private readonly BitArray _pageNumbers = new(1024);
    private readonly List<int> _referenceMapPages = new();

    private UsageMap(PageChannel pageChannel, JetFormat format, byte[] tableBuffer, int pageNum, int rowStart)
    {
        _pageChannel = pageChannel;
        _format = format;
        _tableBuffer = tableBuffer;
        _tablePageNum = pageNum;
        _rowStart = rowStart;
        _startOffset = _rowStart + format.OffsetUsageMapStart;
    }

    /// <summary>
    /// Reads a usage map declaration from the given buffer at the current position
    /// (which must point at a 4-byte row/page reference). Pages are read through the
    /// table's shared page-buffer cache so that multiple maps on the same page stay
    /// consistent when written.
    /// </summary>
    public static UsageMap Read(Database database, byte[] defPageBuffer, int defPageNum, ref int position, Dictionary<int, byte[]> pageCache)
    {
        int umapRowNum = defPageBuffer[position];
        int umapPageNum = ByteUtil.Get3ByteInt(defPageBuffer, position + 1);
        position += 4;

        if (umapPageNum <= 0)
        {
            throw new DatabaseException($"Invalid usage map page number {umapPageNum}");
        }

        JetFormat format = database.Format;
        PageChannel pageChannel = database.PageChannel;

        byte[] tableBuffer;
        if (umapPageNum == defPageNum)
        {
            tableBuffer = defPageBuffer;
        }
        else if (pageCache.TryGetValue(umapPageNum, out byte[]? cached))
        {
            tableBuffer = cached;
        }
        else
        {
            tableBuffer = new byte[format.PageSize];
            pageChannel.ReadPage(tableBuffer, umapPageNum);
            pageCache[umapPageNum] = tableBuffer;
        }

        short rowStart = Table.FindRowStart(tableBuffer, umapRowNum, format);
        short rowEnd = Table.FindRowEnd(tableBuffer, umapRowNum, format);

        byte mapType = tableBuffer[rowStart];
        var rtn = new UsageMap(pageChannel, format, tableBuffer, umapPageNum, rowStart);
        rtn.InitHandler(mapType, false, rowEnd);
        return rtn;
    }

    /// <summary>
    /// Reads the global usage map (always at page 1, row 0).
    /// </summary>
    public static UsageMap ReadGlobal(PageChannel pageChannel, int pageNum, int rowNum)
        => Read(pageChannel, pageChannel.Format, pageNum, rowNum, true);

    private static UsageMap Read(PageChannel pageChannel, JetFormat format, int pageNum, int rowNum, bool isGlobal)
    {
        if (pageNum <= 0)
        {
            throw new DatabaseException($"Invalid usage map page number {pageNum}");
        }

        byte[] tableBuffer = new byte[format.PageSize];
        pageChannel.ReadPage(tableBuffer, pageNum);

        short rowStart = Table.FindRowStart(tableBuffer, rowNum, format);
        short rowEnd = Table.FindRowEnd(tableBuffer, rowNum, format);

        byte mapType = tableBuffer[rowStart];
        var rtn = new UsageMap(pageChannel, format, tableBuffer, pageNum, rowStart);
        rtn.InitHandler(mapType, isGlobal, rowEnd);
        return rtn;
    }

    private void InitHandler(byte mapType, bool isGlobal, int rowEnd)
    {
        _isGlobal = isGlobal;
        if (mapType == MapTypeInline)
        {
            _isReference = false;
            InitInline(rowEnd);
        }
        else if (mapType == MapTypeReference)
        {
            _isReference = true;
            InitReference(rowEnd);
        }
        else
        {
            throw new DatabaseException($"Unrecognized map type: {mapType}");
        }
    }

    private void InitInline(int rowEnd)
    {
        int maxInlinePages = (rowEnd - _startOffset) * 8;
        int startPage = ByteUtil.GetIntLittleEndian(_tableBuffer, _rowStart + 1);
        _startPage = startPage;
        _endPage = startPage + maxInlinePages;
        ProcessMap(_tableBuffer.AsSpan(_startOffset, rowEnd - _startOffset), 0);
    }

    private void InitReference(int rowEnd)
    {
        int maxPagesPerUsageMapPage = (_format.PageSize - _format.OffsetUsageMapPageData) * 8;
        int numUsagePages = (rowEnd - _rowStart - 1) / 4;
        _startOffset = _format.OffsetUsageMapPageData;
        _startPage = 0;
        _endPage = numUsagePages * maxPagesPerUsageMapPage;

        for (int i = 0; i < numUsagePages; i++)
        {
            int mapPageNum = ByteUtil.GetIntLittleEndian(_tableBuffer, _rowStart + _format.OffsetReferenceMapPageNumbers + i * 4);
            if (mapPageNum > 0)
            {
                byte[] mapPageBuffer = new byte[_format.PageSize];
                _pageChannel.ReadPage(mapPageBuffer, mapPageNum);
                byte pageType = mapPageBuffer[0];
                if (pageType != PageTypes.UsageMap)
                {
                    throw new DatabaseException(
                        $"Looking for usage map at page {mapPageNum}, but page type is {pageType}");
                }
                ProcessMap(mapPageBuffer.AsSpan(_format.OffsetUsageMapPageData, _format.PageSize - _format.OffsetUsageMapPageData),
                    maxPagesPerUsageMapPage * i);
                _referenceMapPages.Add(mapPageNum);
            }
        }
    }

    private void ProcessMap(ReadOnlySpan<byte> buffer, int bufferStartPage)
    {
        int byteCount = 0;
        foreach (byte b in buffer)
        {
            if (b != 0)
            {
                for (int i = 0; i < 8; i++)
                {
                    if ((b & (1 << i)) != 0)
                    {
                        int pageNumberOffset = byteCount * 8 + i + bufferStartPage;
                        int pageNumber = BitIndexToPageNumber(pageNumberOffset, PageChannelImpl.InvalidPageNumber);
                        if (!IsPageWithinRange(pageNumber))
                        {
                            throw new DatabaseException(
                                $"found page number {pageNumber} in usage map outside of expected range {_startPage} to {_endPage}");
                        }
                        EnsureCapacity(pageNumberOffset);
                        _pageNumbers.Set(pageNumberOffset, true);
                    }
                }
            }
            byteCount++;
        }
    }

    private void EnsureCapacity(int index)
    {
        if (index >= _pageNumbers.Length)
        {
            _pageNumbers.Length = Math.Max(_pageNumbers.Length * 2, index + 1);
        }
    }

    private int BitIndexToPageNumber(int bitIndex, int invalidPageNumber)
        => bitIndex >= 0 ? _startPage + bitIndex : invalidPageNumber;

    private int PageNumberToBitIndex(int pageNumber)
        => pageNumber >= 0 ? pageNumber - _startPage : -1;

    private bool IsPageWithinRange(int pageNumber) => pageNumber >= _startPage && pageNumber < _endPage;

    public bool ContainsPageNumber(int pageNumber)
        => IsPageWithinRange(pageNumber) && pageNumber >= _startPage && _pageNumbers.Get(pageNumber - _startPage);

    /// <summary>
    /// Adds a page to this usage map and persists the change.
    /// </summary>
    public void AddPageNumber(int pageNumber) => AddOrRemovePageNumber(pageNumber, true);

    /// <summary>
    /// Removes a page from this usage map and persists the change.
    /// </summary>
    public void RemovePageNumber(int pageNumber) => AddOrRemovePageNumber(pageNumber, false);

    private void AddOrRemovePageNumber(int pageNumber, bool add)
    {
        if (_isGlobal && !add)
        {
            // for the global map, out-of-range pages are assumed "on"; only handle
            // pages within the current range
            if (!IsPageWithinRange(pageNumber))
            {
                return;
            }
        }

        if (_isReference)
        {
            int maxPagesPerUsageMapPage = (_format.PageSize - _format.OffsetUsageMapPageData) * 8;
            int pageIndex = pageNumber / maxPagesPerUsageMapPage;
            while (pageIndex >= _referenceMapPages.Count)
            {
                // allocate a new usage map page for this range
                int mapPageNum = _pageChannel.AllocateNewPage();
                var mapPageBuffer = new byte[_format.PageSize];
                mapPageBuffer[0] = PageTypes.UsageMap;
                mapPageBuffer[1] = 0x01;
                _pageChannel.WritePage(mapPageBuffer, mapPageNum);

                int refOffset = _rowStart + _format.OffsetReferenceMapPageNumbers + _referenceMapPages.Count * 4;
                _tableBuffer[refOffset] = (byte)mapPageNum;
                _tableBuffer[refOffset + 1] = (byte)(mapPageNum >> 8);
                _tableBuffer[refOffset + 2] = (byte)(mapPageNum >> 16);
                _tableBuffer[refOffset + 3] = (byte)(mapPageNum >> 24);
                _referenceMapPages.Add(mapPageNum);
                _endPage = _referenceMapPages.Count * maxPagesPerUsageMapPage;
                WriteTable();
            }
            int existingMapPageNum = _referenceMapPages[pageIndex];
            byte[] refMapPageBuffer = new byte[_format.PageSize];
            _pageChannel.ReadPage(refMapPageBuffer, existingMapPageNum);
            UpdateMap(pageNumber, pageNumber - maxPagesPerUsageMapPage * pageIndex, refMapPageBuffer, add);
            _pageChannel.WritePage(refMapPageBuffer, existingMapPageNum);
            return;
        }

        // inline map
        if (!IsPageWithinRange(pageNumber) && !_isGlobal)
        {
            throw new DatabaseException(
                $"Page number {pageNumber} is out of supported range {_startPage} to {_endPage}");
        }
        int bufferRelativePageNumber = PageNumberToBitIndex(pageNumber);
        if (bufferRelativePageNumber < 0 || bufferRelativePageNumber >= _pageNumbers.Length)
        {
            // global map: out-of-range adds are no-ops (bits assumed on)
            if (_isGlobal)
            {
                return;
            }
            throw new DatabaseException(
                $"Page number {pageNumber} is out of supported range {_startPage} to {_endPage}");
        }
        UpdateMap(pageNumber, bufferRelativePageNumber, _tableBuffer, add);
        WriteTable();
    }

    private void UpdateMap(int absolutePageNumber, int bufferRelativePageNumber, byte[] buffer, bool add)
    {
        int offset = _startOffset + bufferRelativePageNumber / 8;
        int bitmask = 1 << (bufferRelativePageNumber % 8);
        byte b = buffer[offset];
        int pageNumberOffset = PageNumberToBitIndex(absolutePageNumber);

        if (add)
        {
            b |= (byte)bitmask;
            EnsureCapacity(pageNumberOffset);
            _pageNumbers.Set(pageNumberOffset, true);
        }
        else
        {
            b &= (byte)~bitmask;
            if (pageNumberOffset >= 0 && pageNumberOffset < _pageNumbers.Length)
            {
                _pageNumbers.Set(pageNumberOffset, false);
            }
        }
        buffer[offset] = b;
    }

    private void WriteTable()
    {
        // write the row data (from the row start) back to the map declaration page
        int rowEnd = _rowStart + _format.UsageMapTableByteLength;
        _pageChannel.WritePage(_tableBuffer, _tablePageNum, _rowStart);
    }

    public PageCursor Cursor() => new(this);

    /// <summary>usage-map pages referenced by this map, for safe definition cleanup</summary>
    internal IReadOnlyCollection<int> ReferenceMapPageNumbers => _referenceMapPages;

    private int GetNextBitIndex(int curIndex)
    {
        for (int i = curIndex + 1; i < _pageNumbers.Length; i++)
        {
            if (_pageNumbers.Get(i))
            {
                return i;
            }
        }
        return -1;
    }

    private int GetPrevBitIndex(int curIndex)
    {
        for (int i = curIndex - 1; i >= 0; i--)
        {
            if (_pageNumbers.Get(i))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Utility class to traverse over the pages in the UsageMap.
    /// </summary>
    public sealed class PageCursor
    {
        private readonly UsageMap _map;
        private int _curIndex = -1;

        internal PageCursor(UsageMap map)
        {
            _map = map;
        }

        public int GetNextPage()
        {
            int bitIndex = _map.GetNextBitIndex(_curIndex);
            if (bitIndex < 0)
            {
                return PageChannelImpl.InvalidPageNumber;
            }
            _curIndex = bitIndex;
            return _map.BitIndexToPageNumber(bitIndex, PageChannelImpl.InvalidPageNumber);
        }

        public int GetPreviousPage()
        {
            int bitIndex = _map.GetPrevBitIndex(_curIndex);
            if (bitIndex < 0)
            {
                return PageChannelImpl.InvalidPageNumber;
            }
            _curIndex = bitIndex;
            return _map.BitIndexToPageNumber(bitIndex, PageChannelImpl.InvalidPageNumber);
        }
    }
}
