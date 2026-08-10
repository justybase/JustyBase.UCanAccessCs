namespace UCanAccess.File;

/// <summary>
/// A single page of index data (port of Jackcess <c>IndexData.DataPage</c>).
/// </summary>
internal abstract class DataPage
{
    public abstract int PageNumber { get; }

    public abstract bool IsLeaf { get; set; }

    public abstract int PrevPageNumber { get; set; }

    public abstract int NextPageNumber { get; set; }

    public abstract int ChildTailPageNumber { get; set; }

    public abstract int TotalEntrySize { get; set; }

    public abstract byte[] EntryPrefix { get; set; }

    public abstract List<IndexData.Entry> Entries { get; set; }

    public abstract void AddEntry(int idx, IndexData.Entry entry);

    public abstract IndexData.Entry RemoveEntry(int idx);

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>
    /// The size of the entries on this page when compressed by the shared entry prefix.
    /// </summary>
    public int CompressedEntrySize => TotalEntrySize - EntryPrefix.Length * (Entries.Count - 1);
}

/// <summary>
/// Manager of the index pages for an <see cref="IndexData"/> (port of Jackcess
/// <c>IndexPageCache</c>).
/// </summary>
internal sealed class IndexPageCache
{
    private enum UpdateType
    {
        Add,
        Remove,
        Replace,
    }

    /// <summary>max number of pages to cache (unless a write operation is in progress)</summary>
    private const int MaxCacheSize = 25;

    private readonly IndexData _indexData;
    private DataPageMain _rootPage = null!;
    private readonly Dictionary<int, DataPageMain> _dataPages = new();
    private readonly List<CacheDataPage> _modifiedPages = new();

    internal IndexPageCache(IndexData indexData)
    {
        _indexData = indexData;
    }

    internal IndexData IndexData => _indexData;

    private PageChannel PageChannel => _indexData.PageChannel;

    /// <summary>
    /// Sets the root page for this index, must be called before normal usage.
    /// </summary>
    internal void SetRootPageNumber(int pageNumber)
    {
        _rootPage = GetDataPage(pageNumber)!;
        // root page has no parent
        _rootPage.InitParentPage(IndexData.InvalidIndexPageNumber, false);
    }

    /// <summary>
    /// Writes any outstanding changes for this index to the file.
    /// </summary>
    internal void Write()
    {
        // first discard any empty pages
        HandleEmptyPages();
        // next, handle any necessary page splitting
        PreparePagesForWriting();
        // finally, write all the modified pages (which are not being deleted)
        WriteDataPages();
        // after we write everything, we can purge our cache if necessary
        if (_dataPages.Count > MaxCacheSize)
        {
            PurgeOldPages();
        }
    }

    private void HandleEmptyPages()
    {
        for (int i = _modifiedPages.Count - 1; i >= 0; i--)
        {
            CacheDataPage cacheDataPage = _modifiedPages[i];
            if (cacheDataPage.Extra.EntryView.IsEmpty)
            {
                if (!cacheDataPage.Main.IsRoot)
                {
                    DeleteDataPage(cacheDataPage);
                }
                else
                {
                    WriteDataPage(cacheDataPage);
                }
                _modifiedPages.RemoveAt(i);
            }
        }
    }

    private void PreparePagesForWriting()
    {
        bool splitPages;
        int maxPageEntrySize = _indexData.MaxPageEntrySize;

        // we need to continue looping through all the pages until we do not split
        // any pages (because a split may cascade up the tree)
        do
        {
            splitPages = false;

            // we might be adding to this list while iterating, so we can't use an iterator
            for (int i = 0; i < _modifiedPages.Count; ++i)
            {
                CacheDataPage cacheDataPage = _modifiedPages[i];

                if (!cacheDataPage.IsLeaf)
                {
                    // see if we need to update any child tail status
                    DataPageMain dpMain = cacheDataPage.Main;
                    int size = cacheDataPage.Extra.EntryView.Count;
                    if (dpMain.HasChildTail)
                    {
                        if (size == 1)
                        {
                            DemoteTail(cacheDataPage);
                        }
                    }
                    else if (size > 1)
                    {
                        // only a leaf page can become a tail page
                        DataPageMain lastChild = dpMain.GetChildPage(cacheDataPage.Extra.EntryView.GetLast());
                        if (lastChild.Leaf)
                        {
                            PromoteTail(cacheDataPage, lastChild);
                        }
                    }
                }

                // look for pages with more entries than can fit on a page
                if (cacheDataPage.TotalEntrySize > maxPageEntrySize)
                {
                    // make sure the prefix is up-to-date (this may have gotten discarded)
                    cacheDataPage.Extra.UpdateEntryPrefix();

                    // now, see if the page will fit when compressed
                    if (cacheDataPage.CompressedEntrySize > maxPageEntrySize)
                    {
                        // need to split this page
                        splitPages = true;
                        SplitDataPage(cacheDataPage);
                    }
                }
            }
        } while (splitPages);
    }

    private void WriteDataPages()
    {
        foreach (CacheDataPage cacheDataPage in _modifiedPages)
        {
            if (cacheDataPage.Extra.EntryView.IsEmpty)
            {
                throw new InvalidOperationException("Unexpected empty page " + cacheDataPage);
            }
            WriteDataPage(cacheDataPage);
        }
        _modifiedPages.Clear();
    }

    /// <summary>
    /// Returns a CacheDataPage for the given page number, may be <c>null</c> if the given
    /// page number is invalid. Loads the given page if necessary.
    /// </summary>
    internal DataPage? GetCacheDataPage(int pageNumber)
    {
        DataPageMain? main = GetDataPage(pageNumber);
        return main != null ? new CacheDataPage(this, main) : null;
    }

    private DataPageMain? GetDataPage(int pageNumber)
    {
        if (_dataPages.TryGetValue(pageNumber, out DataPageMain? dataPage))
        {
            return dataPage;
        }
        if (pageNumber > IndexData.InvalidIndexPageNumber)
        {
            dataPage = ReadDataPage(pageNumber);
            _dataPages[pageNumber] = dataPage;
            return dataPage;
        }
        return null;
    }

    private void WriteDataPage(CacheDataPage cacheDataPage)
    {
        _indexData.WriteDataPage(cacheDataPage);

        // lastly, mark the page as no longer modified
        cacheDataPage.Extra.Modified = false;
    }

    private void DeleteDataPage(CacheDataPage cacheDataPage)
    {
        // free this database page
        PageChannel.DeallocatePage(cacheDataPage.Main.PageNumber);

        // discard from our cache
        _dataPages.Remove(cacheDataPage.Main.PageNumber);

        // lastly, mark the page as no longer modified
        cacheDataPage.Extra.Modified = false;
    }

    private DataPageMain ReadDataPage(int pageNumber)
    {
        var main = new DataPageMain(this, pageNumber);
        var extra = new DataPageExtra();
        var cacheDataPage = new CacheDataPage(this, main, extra);
        _indexData.ReadDataPage(cacheDataPage);

        // associate the extra info with the main data page
        main.Extra = extra;

        return main;
    }

    private IndexData.Entry RemoveEntry(CacheDataPage cacheDataPage, int entryIdx)
        => UpdateEntry(cacheDataPage, entryIdx, null, UpdateType.Remove);

    private void AddEntry(CacheDataPage cacheDataPage, int entryIdx, IndexData.Entry newEntry)
        => UpdateEntry(cacheDataPage, entryIdx, newEntry, UpdateType.Add);

    private IndexData.Entry UpdateEntry(CacheDataPage cacheDataPage, int entryIdx, IndexData.Entry? newEntry, UpdateType upType)
    {
        DataPageMain dpMain = cacheDataPage.Main;
        DataPageExtra dpExtra = cacheDataPage.Extra;

        if (newEntry != null)
        {
            ValidateEntryForPage(dpMain, newEntry);
        }

        // note, it's slightly ucky, but we need to load the parent page before we
        // start mucking with our entries because our parent may use our entries.
        CacheDataPage? parentDataPage = !dpMain.IsRoot ? new CacheDataPage(this, dpMain.GetParentPage()) : null;

        IndexData.Entry oldLastEntry = dpExtra.EntryView.GetLast();
        IndexData.Entry? oldEntry = null;
        int entrySizeDiff = 0;

        switch (upType)
        {
            case UpdateType.Add:
                dpExtra.EntryView.Add(entryIdx, newEntry!);
                entrySizeDiff += newEntry!.Size;
                break;
            case UpdateType.Replace:
                oldEntry = dpExtra.EntryView.Set(entryIdx, newEntry!);
                entrySizeDiff += newEntry!.Size - oldEntry.Size;
                break;
            case UpdateType.Remove:
                oldEntry = dpExtra.EntryView.Remove(entryIdx);
                entrySizeDiff -= oldEntry.Size;
                break;
            default:
                throw new InvalidOperationException("unknown update type " + upType);
        }

        bool updateLast = !ReferenceEquals(oldLastEntry, dpExtra.EntryView.GetLast());

        // child tail entry updates do not modify the page
        if (!updateLast || !dpMain.HasChildTail)
        {
            dpExtra.TotalEntrySize += entrySizeDiff;
            SetModified(cacheDataPage);

            // for now, just clear the prefix, we'll fix it later
            dpExtra.EntryPrefix = Array.Empty<byte>();
        }

        if (dpExtra.EntryView.IsEmpty)
        {
            // this page is dead
            RemoveDataPage(parentDataPage!, cacheDataPage, oldLastEntry);
            return oldEntry!;
        }

        // determine if we need to update our parent page
        if (!updateLast || dpMain.IsRoot)
        {
            // no parent
            return oldEntry!;
        }

        // the update to the last entry needs to be propagated to our parent
        ReplaceParentEntry(parentDataPage!, cacheDataPage, oldLastEntry);
        return oldEntry!;
    }

    private void RemoveDataPage(CacheDataPage parentDataPage, CacheDataPage cacheDataPage, IndexData.Entry oldLastEntry)
    {
        DataPageMain dpMain = cacheDataPage.Main;
        DataPageExtra dpExtra = cacheDataPage.Extra;

        if (dpMain.HasChildTail)
        {
            throw new InvalidOperationException("Still has child tail?");
        }

        if (dpExtra.TotalEntrySize != 0)
        {
            throw new InvalidOperationException($"Empty page but size is not 0? {dpExtra.TotalEntrySize}, {cacheDataPage}");
        }

        if (dpMain.IsRoot)
        {
            // clear out this page (we don't actually remove it)
            dpExtra.EntryPrefix = Array.Empty<byte>();
            // when the root page becomes empty, it becomes a leaf page again
            dpMain.Leaf = true;
            return;
        }

        // remove this page from its parent page
        UpdateParentEntry(parentDataPage, cacheDataPage, oldLastEntry, null, UpdateType.Remove);

        // remove this page from any next/prev pages
        RemoveFromPeers(cacheDataPage);
    }

    private void RemoveFromPeers(CacheDataPage cacheDataPage)
    {
        DataPageMain dpMain = cacheDataPage.Main;

        int prevPageNumber = dpMain.PrevPageNumber;
        int nextPageNumber = dpMain.NextPageNumber;

        DataPageMain? prevMain = dpMain.GetPrevPage();
        if (prevMain != null)
        {
            SetModified(new CacheDataPage(this, prevMain));
            prevMain.NextPageNumber = nextPageNumber;
        }

        DataPageMain? nextMain = dpMain.GetNextPage();
        if (nextMain != null)
        {
            SetModified(new CacheDataPage(this, nextMain));
            nextMain.PrevPageNumber = prevPageNumber;
        }
    }

    private void AddParentEntry(CacheDataPage parentDataPage, CacheDataPage childDataPage)
    {
        DataPageExtra childExtra = childDataPage.Extra;
        UpdateParentEntry(parentDataPage, childDataPage, null, childExtra.EntryView.GetLast(), UpdateType.Add);
    }

    private void ReplaceParentEntry(CacheDataPage parentDataPage, CacheDataPage childDataPage, IndexData.Entry oldEntry)
    {
        DataPageExtra childExtra = childDataPage.Extra;
        UpdateParentEntry(parentDataPage, childDataPage, oldEntry, childExtra.EntryView.GetLast(), UpdateType.Replace);
    }

    private void UpdateParentEntry(CacheDataPage parentDataPage, CacheDataPage childDataPage, IndexData.Entry? oldEntry, IndexData.Entry? newEntry, UpdateType upType)
    {
        DataPageMain childMain = childDataPage.Main;
        DataPageExtra parentExtra = parentDataPage.Extra;

        if (childMain.IsTail && upType != UpdateType.Remove)
        {
            // for add or replace, update the child tail info before updating the parent entries
            UpdateParentTail(parentDataPage, childDataPage, upType);
        }

        if (oldEntry != null)
        {
            oldEntry = oldEntry.AsNodeEntry(childMain.PageNumber);
        }
        if (newEntry != null)
        {
            newEntry = newEntry.AsNodeEntry(childMain.PageNumber);
        }

        bool expectFound = true;
        int idx = 0;

        switch (upType)
        {
            case UpdateType.Add:
                expectFound = false;
                idx = parentExtra.EntryView.Find(newEntry!);
                break;
            case UpdateType.Replace:
            case UpdateType.Remove:
                idx = parentExtra.EntryView.Find(oldEntry!);
                break;
            default:
                throw new InvalidOperationException("unknown update type " + upType);
        }

        if (idx < 0)
        {
            if (expectFound)
            {
                throw new InvalidOperationException($"Could not find child entry in parent; childEntry {oldEntry}; parent {parentDataPage}");
            }
            idx = IndexData.MissingIndexToInsertionPoint(idx);
        }
        else if (!expectFound)
        {
            throw new InvalidOperationException($"Unexpectedly found child entry in parent; childEntry {newEntry}; parent {parentDataPage}");
        }
        UpdateEntry(parentDataPage, idx, newEntry, upType);

        if (childMain.IsTail && upType == UpdateType.Remove)
        {
            // for remove, update the child tail info after updating the parent entries
            UpdateParentTail(parentDataPage, childDataPage, upType);
        }
    }

    private void UpdateParentTail(CacheDataPage parentDataPage, CacheDataPage childDataPage, UpdateType upType)
    {
        DataPageMain parentMain = parentDataPage.Main;

        int newChildTailPageNumber = upType == UpdateType.Remove ? IndexData.InvalidIndexPageNumber : childDataPage.Main.PageNumber;
        if (!parentMain.IsChildTailPageNumber(newChildTailPageNumber))
        {
            SetModified(parentDataPage);
            parentMain.ChildTailPageNumber = newChildTailPageNumber;
        }
    }

    private static void ValidateEntryForPage(DataPageMain dpMain, IndexData.Entry entry)
    {
        if (dpMain.Leaf != entry.IsLeafEntry)
        {
            throw new InvalidOperationException($"Trying to update page with wrong entry type; pageLeaf {dpMain.Leaf}, entryLeaf {entry.IsLeafEntry}");
        }
    }

    private void SplitDataPage(CacheDataPage origDataPage)
    {
        DataPageMain origMain = origDataPage.Main;
        DataPageExtra origExtra = origDataPage.Extra;

        SetModified(origDataPage);

        int numEntries = origExtra.Entries.Count;
        if (numEntries < 2)
        {
            throw new InvalidOperationException($"Cannot split page with less than 2 entries {origDataPage}");
        }

        if (origMain.IsRoot)
        {
            // we can't split the root page directly, so we need to put another page
            // between the root page and its sub-pages, and then split that page.
            CacheDataPage nestedDataPage = NestRootDataPage(origDataPage);

            // now, split this new page instead
            origDataPage = nestedDataPage;
            origMain = nestedDataPage.Main;
            origExtra = nestedDataPage.Extra;
        }

        // note, it's slightly ucky, but we need to load the parent page before we
        // start mucking with our entries because our parent may use our entries.
        DataPageMain parentMain = origMain.GetParentPage();
        var parentDataPage = new CacheDataPage(this, parentMain);

        // so, we will naively move half the entries from one page to a new page.
        CacheDataPage newDataPage = AllocateNewCacheDataPage(parentMain.PageNumber, origMain.Leaf);
        DataPageMain newMain = newDataPage.Main;
        DataPageExtra newExtra = newDataPage.Extra;

        // move first half of the entries from old page to new page (so we do not
        // need to muck with any tail entries)
        int half = (numEntries + 1) / 2;
        for (int i = 0; i < half; i++)
        {
            IndexData.Entry headEntry = origExtra.Entries[i];
            newExtra.TotalEntrySize += headEntry.Size;
            newExtra.Entries.Add(headEntry);
        }
        newExtra.SetEntryView(newMain);

        // remove the moved entries from the old page
        origExtra.Entries.RemoveRange(0, half);
        origExtra.EntryPrefix = Array.Empty<byte>();
        origExtra.TotalEntrySize -= newExtra.TotalEntrySize;

        // insert this new page between the old page and any previous page
        AddToPeersBefore(newDataPage, origDataPage);

        if (!newMain.Leaf)
        {
            // reparent the children pages of the new page
            ReparentChildren(newDataPage);

            // if the children of this page are also node pages, then the next/prev
            // links should not cross parent boundaries
            DataPageMain childMain = newMain.GetChildPage(newExtra.EntryView.GetLast());
            if (!childMain.Leaf)
            {
                SeparateFromNextPeer(new CacheDataPage(this, childMain));
            }
        }

        // lastly, we need to add the new page to the parent page's entries
        AddParentEntry(parentDataPage, newDataPage);
    }

    private CacheDataPage NestRootDataPage(CacheDataPage rootDataPage)
    {
        DataPageMain rootMain = rootDataPage.Main;
        DataPageExtra rootExtra = rootDataPage.Extra;

        if (!rootMain.IsRoot)
        {
            throw new ArgumentException("should be called with root, duh");
        }

        CacheDataPage newDataPage = AllocateNewCacheDataPage(rootMain.PageNumber, rootMain.Leaf);
        DataPageMain newMain = newDataPage.Main;
        DataPageExtra newExtra = newDataPage.Extra;

        // move entries to new page
        newMain.ChildTailPageNumber = rootMain.ChildTailPageNumber;
        newExtra.Entries = rootExtra.Entries;
        newExtra.EntryPrefix = rootExtra.EntryPrefix;
        newExtra.TotalEntrySize = rootExtra.TotalEntrySize;
        newExtra.SetEntryView(newMain);

        if (!newMain.Leaf)
        {
            // we need to re-parent all the child pages
            ReparentChildren(newDataPage);
        }

        // clear the root page
        rootMain.Leaf = false;
        rootMain.ChildTailPageNumber = IndexData.InvalidIndexPageNumber;
        rootExtra.Entries = new List<IndexData.Entry>();
        rootExtra.EntryPrefix = Array.Empty<byte>();
        rootExtra.TotalEntrySize = 0;
        rootExtra.SetEntryView(rootMain);

        // add the new page as the first child of the root page
        AddParentEntry(rootDataPage, newDataPage);

        return newDataPage;
    }

    private CacheDataPage AllocateNewCacheDataPage(int parentPageNumber, bool isLeaf)
    {
        var dpMain = new DataPageMain(this, PageChannel.AllocateNewPage());
        var dpExtra = new DataPageExtra();
        dpMain.InitParentPage(parentPageNumber, false);
        dpMain.Leaf = isLeaf;
        dpMain.PrevPageNumber = IndexData.InvalidIndexPageNumber;
        dpMain.NextPageNumber = IndexData.InvalidIndexPageNumber;
        dpMain.ChildTailPageNumber = IndexData.InvalidIndexPageNumber;
        dpExtra.Entries = new List<IndexData.Entry>();
        dpExtra.EntryPrefix = Array.Empty<byte>();
        dpMain.Extra = dpExtra;

        // add to our page cache
        _dataPages[dpMain.PageNumber] = dpMain;

        // update owned pages cache
        _indexData.AddOwnedPage(dpMain.PageNumber);

        // needs to be written out
        var cacheDataPage = new CacheDataPage(this, dpMain, dpExtra);
        SetModified(cacheDataPage);

        return cacheDataPage;
    }

    private void AddToPeersBefore(CacheDataPage newDataPage, CacheDataPage origDataPage)
    {
        DataPageMain origMain = origDataPage.Main;
        DataPageMain newMain = newDataPage.Main;

        DataPageMain? prevMain = origMain.GetPrevPage();

        newMain.NextPageNumber = origMain.PageNumber;
        newMain.PrevPageNumber = origMain.PrevPageNumber;
        origMain.PrevPageNumber = newMain.PageNumber;

        if (prevMain != null)
        {
            SetModified(new CacheDataPage(this, prevMain));
            prevMain.NextPageNumber = newMain.PageNumber;
        }
    }

    private void SeparateFromNextPeer(CacheDataPage cacheDataPage)
    {
        DataPageMain dpMain = cacheDataPage.Main;

        SetModified(cacheDataPage);

        DataPageMain nextMain = dpMain.GetNextPage()!;
        SetModified(new CacheDataPage(this, nextMain));

        nextMain.PrevPageNumber = IndexData.InvalidIndexPageNumber;
        dpMain.NextPageNumber = IndexData.InvalidIndexPageNumber;
    }

    private void ReparentChildren(CacheDataPage cacheDataPage)
    {
        DataPageMain dpMain = cacheDataPage.Main;
        DataPageExtra dpExtra = cacheDataPage.Extra;

        // note, the "parent" page number is not actually persisted, so we do not
        // need to mark any updated pages as modified
        foreach (IndexData.Entry entry in dpExtra.EntryView)
        {
            int childPageNumber = entry.SubPageNumber!.Value;
            if (_dataPages.TryGetValue(childPageNumber, out DataPageMain? childMain))
            {
                childMain.SetParentPage(dpMain.PageNumber, dpMain.IsChildTailPageNumber(childPageNumber));
            }
        }
    }

    private void DemoteTail(CacheDataPage cacheDataPage)
    {
        // there's only one entry on the page, and it's the tail. make it a normal entry
        DataPageMain dpMain = cacheDataPage.Main;
        DataPageExtra dpExtra = cacheDataPage.Extra;

        SetModified(cacheDataPage);

        DataPageMain tailMain = dpMain.GetChildTailPage();
        var tailDataPage = new CacheDataPage(this, tailMain);

        // move the tail entry to the last normal entry
        UpdateParentTail(cacheDataPage, tailDataPage, UpdateType.Remove);
        IndexData.Entry tailEntry = dpExtra.EntryView.DemoteTail();
        dpExtra.TotalEntrySize += tailEntry.Size;
        dpExtra.EntryPrefix = Array.Empty<byte>();

        tailMain.SetParentPage(dpMain.PageNumber, false);
    }

    private void PromoteTail(CacheDataPage cacheDataPage, DataPageMain lastMain)
    {
        // there's no tail currently on this page, make last entry a tail
        DataPageMain dpMain = cacheDataPage.Main;
        DataPageExtra dpExtra = cacheDataPage.Extra;

        SetModified(cacheDataPage);

        var lastDataPage = new CacheDataPage(this, lastMain);

        // move the "last" normal entry to the tail entry
        UpdateParentTail(cacheDataPage, lastDataPage, UpdateType.Add);
        IndexData.Entry lastEntry = dpExtra.EntryView.PromoteTail();
        dpExtra.TotalEntrySize -= lastEntry.Size;
        dpExtra.EntryPrefix = Array.Empty<byte>();

        lastMain.SetParentPage(dpMain.PageNumber, true);
    }

    /// <summary>
    /// Finds the index page on which the given entry does or should reside.
    /// </summary>
    internal DataPage FindCacheDataPage(IndexData.Entry e)
    {
        DataPageMain curPage = _rootPage;
        while (true)
        {
            if (curPage.Leaf)
            {
                // nowhere to go from here
                return new CacheDataPage(this, curPage);
            }

            DataPageExtra extra = curPage.Extra;

            // need to descend
            int idx = extra.EntryView.Find(e);
            if (idx < 0)
            {
                idx = IndexData.MissingIndexToInsertionPoint(idx);
                if (idx == extra.EntryView.Count)
                {
                    // just move to last child page
                    idx--;
                }
            }

            IndexData.Entry nodeEntry = extra.EntryView[idx];
            curPage = curPage.GetChildPage(nodeEntry);
        }
    }

    /// <summary>
    /// Marks the given index page as modified and saves it for writing, if necessary.
    /// </summary>
    private void SetModified(CacheDataPage cacheDataPage)
    {
        if (!cacheDataPage.Extra.Modified)
        {
            _modifiedPages.Add(cacheDataPage);
            cacheDataPage.Extra.Modified = true;
        }
    }

    private static byte[] FindCommonPrefix(IndexData.Entry e1, IndexData.Entry e2)
    {
        byte[] b1 = e1.EntryBytes!;
        byte[] b2 = e2.EntryBytes!;

        int maxLen = b1.Length;
        byte[] prefix = b1;
        if (b1.Length > b2.Length)
        {
            maxLen = b2.Length;
            prefix = b2;
        }

        int len = 0;
        while (len < maxLen && b1[len] == b2[len])
        {
            len++;
        }

        if (len < prefix.Length)
        {
            if (len == 0)
            {
                return Array.Empty<byte>();
            }

            // need new prefix
            prefix = ByteUtil.CopyOf(prefix, 0, len);
        }

        return prefix;
    }

    private void PurgeOldPages()
    {
        foreach (DataPageMain dpMain in _dataPages.Values.ToList())
        {
            // note, we never purge the root page
            if (!ReferenceEquals(dpMain, _rootPage))
            {
                _dataPages.Remove(dpMain.PageNumber);
                if (_dataPages.Count <= MaxCacheSize)
                {
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Data page model
    // ------------------------------------------------------------------

    /// <summary>keeps track of the main info for an index page</summary>
    private sealed class DataPageMain
    {
        private readonly IndexPageCache _cache;
        internal readonly int PageNumber;
        internal int PrevPageNumber = IndexData.InvalidIndexPageNumber;
        internal int NextPageNumber = IndexData.InvalidIndexPageNumber;
        internal int ChildTailPageNumber = IndexData.InvalidIndexPageNumber;
        internal int? ParentPageNumber;
        internal bool Leaf;
        internal bool Tail;
        private DataPageExtra _extra = null!;

        internal DataPageMain(IndexPageCache cache, int pageNumber)
        {
            _cache = cache;
            PageNumber = pageNumber;
        }

        internal IndexPageCache Cache => _cache;
        internal bool IsRoot => ReferenceEquals(this, _cache._rootPage);

        internal bool IsTail
        {
            get
            {
                ResolveParent();
                return Tail;
            }
        }

        internal bool HasChildTail => ChildTailPageNumber != IndexData.InvalidIndexPageNumber;

        internal bool IsChildTailPageNumber(int pageNumber) => ChildTailPageNumber == pageNumber;

        internal DataPageMain GetParentPage()
        {
            ResolveParent();
            return _cache.GetDataPage(ParentPageNumber!.Value)!;
        }

        internal void InitParentPage(int? parentPageNumber, bool isTail)
        {
            // only set if not already set
            if (ParentPageNumber == null)
            {
                SetParentPage(parentPageNumber, isTail);
            }
        }

        internal void SetParentPage(int? parentPageNumber, bool isTail)
        {
            ParentPageNumber = parentPageNumber;
            Tail = isTail;
        }

        internal DataPageMain? GetPrevPage() => _cache.GetDataPage(PrevPageNumber);

        internal DataPageMain? GetNextPage() => _cache.GetDataPage(NextPageNumber);

        internal DataPageMain GetChildPage(IndexData.Entry e)
        {
            int childPageNumber = e.SubPageNumber!.Value;
            return GetChildPage(childPageNumber, IsChildTailPageNumber(childPageNumber));
        }

        internal DataPageMain GetChildTailPage() => GetChildPage(ChildTailPageNumber, true)!;

        private DataPageMain GetChildPage(int childPageNumber, bool isTail)
        {
            DataPageMain? child = _cache.GetDataPage(childPageNumber);
            if (child != null)
            {
                // set the parent info for this child (if necessary)
                child.InitParentPage(PageNumber, isTail);
            }
            return child!;
        }

        internal DataPageExtra Extra
        {
            get => _extra;
            set
            {
                value.SetEntryView(this);
                _extra = value;
            }
        }

        private void ResolveParent()
        {
            if (ParentPageNumber == null)
            {
                // the act of searching for the last entry should resolve any parent
                // pages along the path
                Cache.FindCacheDataPage(Extra.EntryView.GetLast());
                if (ParentPageNumber == null)
                {
                    throw new InvalidOperationException("Parent was not resolved");
                }
            }
        }
    }

    /// <summary>keeps track of the extra info for an index page</summary>
    private sealed class DataPageExtra
    {
        internal List<IndexData.Entry> Entries = new();
        internal EntryListView EntryView = null!;
        internal byte[] EntryPrefix = Array.Empty<byte>();
        internal int TotalEntrySize;
        internal bool Modified;

        internal void SetEntryView(DataPageMain main)
        {
            EntryView = new EntryListView(main, this);
        }

        internal void UpdateEntryPrefix()
        {
            if (EntryPrefix.Length == 0)
            {
                // prefix is only related to *real* entries, tail not included
                EntryPrefix = FindCommonPrefix(Entries[0], Entries[Entries.Count - 1]);
            }
        }
    }

    /// <summary>
    /// IndexPageCache implementation of an index <see cref="DataPage"/>.
    /// </summary>
    private sealed class CacheDataPage : DataPage
    {
        private readonly IndexPageCache _cache;
        internal readonly DataPageMain Main;
        internal readonly DataPageExtra Extra;

        internal CacheDataPage(IndexPageCache cache, DataPageMain dataPage)
        {
            _cache = cache;
            Main = dataPage;
            Extra = dataPage.Extra;
        }

        internal CacheDataPage(IndexPageCache cache, DataPageMain dataPage, DataPageExtra extra)
        {
            _cache = cache;
            Main = dataPage;
            Extra = extra;
        }

        public override int PageNumber => Main.PageNumber;

        public override bool IsLeaf { get => Main.Leaf; set => Main.Leaf = value; }

        public override int PrevPageNumber { get => Main.PrevPageNumber; set => Main.PrevPageNumber = value; }

        public override int NextPageNumber { get => Main.NextPageNumber; set => Main.NextPageNumber = value; }

        public override int ChildTailPageNumber { get => Main.ChildTailPageNumber; set => Main.ChildTailPageNumber = value; }

        public override int TotalEntrySize { get => Extra.TotalEntrySize; set => Extra.TotalEntrySize = value; }

        public override byte[] EntryPrefix { get => Extra.EntryPrefix; set => Extra.EntryPrefix = value; }

        public override List<IndexData.Entry> Entries { get => Extra.Entries; set => Extra.Entries = value; }

        public override void AddEntry(int idx, IndexData.Entry entry) => _cache.AddEntry(this, idx, entry);

        public override IndexData.Entry RemoveEntry(int idx) => _cache.RemoveEntry(this, idx);
    }

    /// <summary>
    /// A view of an index page's entries which combines the normal entries and the tail
    /// entry into one collection (port of Jackcess <c>IndexPageCache.EntryListView</c>).
    /// </summary>
    private sealed class EntryListView : System.Collections.Generic.IEnumerable<IndexData.Entry>
    {
        private readonly DataPageExtra _extra;
        private IndexData.Entry? _childTailEntry;

        internal EntryListView(DataPageMain main, DataPageExtra extra)
        {
            if (main.HasChildTail)
            {
                _childTailEntry = main.GetChildTailPage().Extra.EntryView.GetLast().AsNodeEntry(main.ChildTailPageNumber);
            }
            _extra = extra;
        }

        internal List<IndexData.Entry> Entries => _extra.Entries;

        internal bool IsEmpty => Count == 0;

        internal int Count
        {
            get
            {
                int size = Entries.Count;
                if (_childTailEntry != null)
                {
                    size++;
                }
                return size;
            }
        }

        internal IndexData.Entry this[int idx] => IsCurrentChildTailIndex(idx) ? _childTailEntry! : Entries[idx];

        internal bool HasChildTail => _childTailEntry != null;

        internal IndexData.Entry GetLast()
            => _childTailEntry ?? (Entries.Count > 0 ? Entries[Entries.Count - 1] : null!);

        internal IndexData.Entry WithChildTailEntry(IndexData.Entry? newEntry)
        {
            IndexData.Entry old = _childTailEntry!;
            _childTailEntry = newEntry;
            return old;
        }

        internal void Add(int idx, IndexData.Entry newEntry) => Entries.Insert(idx, newEntry);

        internal IndexData.Entry Set(int idx, IndexData.Entry newEntry)
        {
            if (IsCurrentChildTailIndex(idx))
            {
                return WithChildTailEntry(newEntry);
            }
            IndexData.Entry old = Entries[idx];
            Entries[idx] = newEntry;
            return old;
        }

        internal IndexData.Entry Remove(int idx)
        {
            if (IsCurrentChildTailIndex(idx))
            {
                return WithChildTailEntry(null);
            }
            IndexData.Entry old = Entries[idx];
            Entries.RemoveAt(idx);
            return old;
        }

        internal IndexData.Entry DemoteTail()
        {
            IndexData.Entry tail = _childTailEntry!;
            _childTailEntry = null;
            Entries.Add(tail);
            return tail;
        }

        internal IndexData.Entry PromoteTail()
        {
            IndexData.Entry last = Entries[Entries.Count - 1];
            Entries.RemoveAt(Entries.Count - 1);
            _childTailEntry = last;
            return last;
        }

        /// <summary>binary search over the combined entries + tail view (Java Collections.binarySearch semantics)</summary>
        internal int Find(IndexData.Entry e)
        {
            int low = 0;
            int high = Count - 1;
            while (low <= high)
            {
                int mid = (low + high) >> 1;
                int cmp = this[mid].CompareTo(e);
                if (cmp < 0)
                {
                    low = mid + 1;
                }
                else if (cmp > 0)
                {
                    high = mid - 1;
                }
                else
                {
                    return mid;
                }
            }
            return -(low + 1);
        }

        private bool IsCurrentChildTailIndex(int idx) => idx == Entries.Count;

        public System.Collections.Generic.IEnumerator<IndexData.Entry> GetEnumerator()
        {
            foreach (IndexData.Entry entry in Entries)
            {
                yield return entry;
            }
            if (_childTailEntry != null)
            {
                yield return _childTailEntry;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
