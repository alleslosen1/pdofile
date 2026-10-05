namespace Pdofile.Ink;

/// <summary>All ink of an open document, indexed per page by a uniform grid so that
/// erasing and redrawing only look at strokes near the area involved.</summary>
public sealed class InkStore
{
    const float Cell = 48f;

    readonly PageInk[] _pages;
    readonly bool[] _ack;
    long _seq = 1;
    long _importSeq = long.MinValue / 2;
    int _stamp;

    public InkStore(int pageCount)
    {
        _pages = new PageInk[pageCount];
        for (int i = 0; i < pageCount; i++) _pages[i] = new PageInk();
        _ack = new bool[pageCount];
    }

    public int PageCount => _pages.Length;

    /// <summary>Increments on every user-visible change.</summary>
    public int Version { get; private set; }

    /// <summary>Raised with the page and the area (page points) whose rendering changed.</summary>
    public event Action<int, Box>? Changed;

    public long NextSeq() => _seq++;

    public bool HasStrokes(int page) => _pages[page].All.Count > 0;
    public IReadOnlyCollection<Stroke> PageStrokes(int page) => _pages[page].All;
    public float MaxRadius(int page) => _pages[page].MaxR;

    /// <summary>True once the page's original ink was imported (or replaced by recovered ink).</summary>
    public bool IsAcknowledged(int page) => _ack[page];
    public void Acknowledge(int page) => _ack[page] = true;

    public void Add(Stroke s)
    {
        _pages[s.Page].Add(s);
        Version++;
        Changed?.Invoke(s.Page, s.Bounds);
    }

    public void Remove(Stroke s)
    {
        if (!_pages[s.Page].Remove(s)) return;
        Version++;
        Changed?.Invoke(s.Page, s.Bounds);
    }

    /// <summary>Adds ink that already existed in the file (below anything drawn this session).</summary>
    public void ReceiveImport(int page, IReadOnlyList<Stroke> strokes)
    {
        if (_ack[page]) return;
        _ack[page] = true;
        foreach (var s in strokes)
        {
            s.Seq = _importSeq++;
            _pages[page].Add(s);
            Changed?.Invoke(page, s.Bounds);
        }
    }

    /// <summary>Replaces the page's ink with recovered ink, as a user change.</summary>
    public void Restore(int page, IReadOnlyList<Stroke> strokes)
    {
        _ack[page] = true;
        foreach (var s in _pages[page].All.ToList()) Remove(s);
        foreach (var s in strokes)
        {
            s.Seq = NextSeq();
            Add(s);
        }
    }

    public void Apply(InkChange c, bool forward)
    {
        foreach (var s in forward ? c.Removed : c.Added) Remove(s);
        foreach (var s in forward ? c.Added : c.Removed) Add(s);
    }

    public List<Stroke> Query(int page, Box box)
    {
        var res = new List<Stroke>();
        var p = _pages[page];
        if (p.All.Count == 0 || box.IsEmpty) return res;
        int stamp = ++_stamp;
        int cx0 = (int)MathF.Floor(box.MinX / Cell), cx1 = (int)MathF.Floor(box.MaxX / Cell);
        int cy0 = (int)MathF.Floor(box.MinY / Cell), cy1 = (int)MathF.Floor(box.MaxY / Cell);
        long cells = (long)(cx1 - cx0 + 1) * (cy1 - cy0 + 1);
        if (cells > p.All.Count + 16)
        {
            foreach (var s in p.All) if (s.Bounds.Intersects(box)) res.Add(s);
            return res;
        }
        for (int cy = cy0; cy <= cy1; cy++)
        for (int cx = cx0; cx <= cx1; cx++)
        {
            if (!p.Cells.TryGetValue(Key(cx, cy), out var list)) continue;
            foreach (var s in list)
            {
                if (s.Stamp == stamp) continue;
                s.Stamp = stamp;
                if (s.Bounds.Intersects(box)) res.Add(s);
            }
        }
        return res;
    }

    public List<Stroke> GroupMembers(int page, HashSet<int> groups)
    {
        var res = new List<Stroke>();
        if (groups.Count == 0) return res;
        foreach (var s in _pages[page].All) if (s.Group != 0 && groups.Contains(s.Group)) res.Add(s);
        return res;
    }

    /// <summary>Strokes per page for every page that must be written on save.</summary>
    public (Dictionary<int, List<Stroke>> strokes, HashSet<int> ack) SnapshotForSave()
    {
        var dict = new Dictionary<int, List<Stroke>>();
        var ack = new HashSet<int>();
        for (int i = 0; i < _pages.Length; i++)
        {
            if (_ack[i]) ack.Add(i);
            if (_pages[i].All.Count > 0 || _ack[i]) dict[i] = new List<Stroke>(_pages[i].All);
        }
        return (dict, ack);
    }

    static long Key(int cx, int cy) => ((long)cx << 32) | (uint)cy;

    sealed class PageInk
    {
        public readonly HashSet<Stroke> All = new();
        public readonly Dictionary<long, List<Stroke>> Cells = new();
        public float MaxR;

        public void Add(Stroke s)
        {
            if (!All.Add(s)) return;
            MaxR = MathF.Max(MaxR, s.MaxR);
            ForCells(s.Bounds, key =>
            {
                if (!Cells.TryGetValue(key, out var list)) Cells[key] = list = new List<Stroke>(4);
                list.Add(s);
            });
        }

        public bool Remove(Stroke s)
        {
            if (!All.Remove(s)) return false;
            ForCells(s.Bounds, key =>
            {
                if (Cells.TryGetValue(key, out var list))
                {
                    list.Remove(s);
                    if (list.Count == 0) Cells.Remove(key);
                }
            });
            return true;
        }

        static void ForCells(Box b, Action<long> f)
        {
            int cx0 = (int)MathF.Floor(b.MinX / Cell), cx1 = (int)MathF.Floor(b.MaxX / Cell);
            int cy0 = (int)MathF.Floor(b.MinY / Cell), cy1 = (int)MathF.Floor(b.MaxY / Cell);
            for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
                f(Key(cx, cy));
        }
    }
}

/// <summary>One undoable edit: strokes removed and strokes added.</summary>
public sealed class InkChange
{
    public readonly List<Stroke> Removed = new();
    public readonly List<Stroke> Added = new();
    public bool IsEmpty => Removed.Count == 0 && Added.Count == 0;
}

/// <summary>Accumulates many small edits (an eraser drag) into a single undo step.</summary>
public sealed class ChangeBuilder
{
    readonly HashSet<Stroke> _added = new();
    readonly List<Stroke> _removed = new();

    public void Removed(Stroke s)
    {
        // A piece created earlier in this same gesture just disappears from the change.
        if (!_added.Remove(s)) _removed.Add(s);
    }

    public void Added(Stroke s) => _added.Add(s);

    public InkChange Build()
    {
        var c = new InkChange();
        c.Removed.AddRange(_removed);
        c.Added.AddRange(_added);
        return c;
    }
}

public sealed class UndoStack
{
    const int Limit = 1000;
    readonly LinkedList<InkChange> _undo = new();
    readonly Stack<InkChange> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(InkChange c)
    {
        _undo.AddLast(c);
        if (_undo.Count > Limit) _undo.RemoveFirst();
        _redo.Clear();
    }

    public InkChange? Undo()
    {
        if (_undo.Last is not { } node) return null;
        _undo.RemoveLast();
        _redo.Push(node.Value);
        return node.Value;
    }

    public InkChange? Redo()
    {
        if (_redo.Count == 0) return null;
        var c = _redo.Pop();
        _undo.AddLast(c);
        return c;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
