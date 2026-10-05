using System.Numerics;
using Pdofile.Ink;
using Pdofile.Pdf;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Pdofile.View;

/// <summary>
/// The PDF canvas: continuous vertical page layout, tiled PDF rendering, cached ink tiles,
/// and all pen/eraser/shape/lasso interaction. Everything is drawn in device pixels.
/// </summary>
public sealed partial class DocumentView : Grid
{
    const float GapPt = 14f;
    const int Tile = PdfEngine.TileSize;
    static readonly Color BgColor = ColorHelper.FromArgb(255, 0xD9, 0xDC, 0xE1);

    readonly CanvasControl _canvas;
    readonly DispatcherQueueTimer _holdTimer;
    PdfEngine? _engine;
    InkStore? _store;
    int _invalidatePending;

    float[] _pageTop = [], _pageLeft = [];
    Vector2[] _size = [];
    float _docW, _docH;

    // Viewport: _px = device pixels per PDF point (quantised to 1/1000 so tiles match exactly);
    // (_offX, _offY) = document point at the top-left pixel.
    float _px = 1, _offX, _offY;
    bool _fitWidth = true;

    public ToolSettings Settings { get; set; } = new();
    public UndoStack History { get; } = new();
    public bool SpaceDown { get; set; }

    public event EventHandler? ViewChanged;
    public event EventHandler? InkEdited;
    public event EventHandler? SelectionChanged;

    public DocumentView()
    {
        _canvas = new CanvasControl { IsTabStop = true, UseSystemFocusVisuals = false, ClearColor = BgColor };
        Children.Add(_canvas);
        _canvas.Draw += OnDraw;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += OnPointerLost;
        _canvas.PointerCaptureLost += OnPointerLost;
        _canvas.PointerWheelChanged += OnWheel;
        _canvas.PointerExited += (_, _) => { _hoverOn = false; Invalidate(); };
        SizeChanged += (_, _) => OnResized();
        _holdTimer = DispatcherQueue.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(50);
        _holdTimer.Tick += (_, _) => OnHoldTick();
    }

    public bool HasDocument => _engine != null;
    public int PageCount => _engine?.PageCount ?? 0;
    public bool HasSelection => _sel != null;

    float Dpi => _canvas.Dpi / 96f;
    int ViewW => Math.Max(1, (int)(_canvas.ActualWidth * Dpi));
    int ViewH => Math.Max(1, (int)(_canvas.ActualHeight * Dpi));
    float BasePx => Dpi * 96f / 72f;
    float MarginPx => 16 * Dpi;
    int Level => (int)MathF.Round(_px * 1000);

    public float ZoomPercent => _px / BasePx * 100f;

    public void FocusCanvas() => _canvas.Focus(FocusState.Programmatic);

    public void Load(PdfEngine engine, InkStore store)
    {
        Unload();
        _engine = engine;
        _store = store;
        engine.Device = CanvasDevice.GetSharedDevice();
        engine.TileReady += OnTileReady;
        engine.InkImported += OnInkImported;
        store.Changed += OnStoreChanged;

        int n = engine.PageCount;
        _size = engine.PageSizes;
        _pageTop = new float[n];
        _pageLeft = new float[n];
        _docW = _size.Max(s => s.X);
        float y = 0;
        for (int i = 0; i < n; i++)
        {
            _pageTop[i] = y;
            _pageLeft[i] = (_docW - _size[i].X) / 2;
            y += _size[i].Y + GapPt;
        }
        _docH = y - GapPt;

        _fitWidth = true;
        _offY = -MarginPx;
        FitWidth();
        _offY = -MarginPx / _px;
        Clamp();
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Unload()
    {
        _holdTimer.Stop();
        _g = Gesture.None;
        _wet = null;
        _wetHl = null;
        _preview = null;
        _lasso = null;
        _sel = null;
        _erase = null;
        if (_engine != null)
        {
            _engine.TileReady -= OnTileReady;
            _engine.InkImported -= OnInkImported;
        }
        if (_store != null) _store.Changed -= OnStoreChanged;
        foreach (var t in _inkTiles.Values) t.Dispose();
        _inkTiles.Clear();
        _inkByPage.Clear();
        _tileUsed.Clear();
        _fallbackLevel = 0;
        _inkFallbackLevel = 0;
        _tileDpi = 0;   // next frame re-applies the DPI to the new engine
        _engine = null;
        _store = null;
        History.Clear();
        Invalidate();
    }

    void OnTileReady()
    {
        if (Interlocked.Exchange(ref _invalidatePending, 1) == 1) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _invalidatePending, 0);
            Invalidate();
        });
    }

    void OnInkImported(int page, List<Stroke> strokes)
    {
        var engine = _engine;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_engine != engine || _store == null) return;
            _store.ReceiveImport(page, strokes);
        });
    }

    void Invalidate() => _canvas.Invalidate();

    void RaiseView() => ViewChanged?.Invoke(this, EventArgs.Empty);

    // ---- coordinates -------------------------------------------------------------------

    Vector2 ToPx(Windows.Foundation.Point p) => new((float)p.X * Dpi, (float)p.Y * Dpi);

    Vector2 PageOrigin(int i) => new(MathF.Round((_pageLeft[i] - _offX) * _px), MathF.Round((_pageTop[i] - _offY) * _px));
    int PageW(int i) => Math.Max(1, (int)MathF.Round(_size[i].X * _px));
    int PageH(int i) => Math.Max(1, (int)MathF.Round(_size[i].Y * _px));
    Matrix3x2 PageMatrix(int i) => Matrix3x2.CreateScale(_px) * Matrix3x2.CreateTranslation(PageOrigin(i));
    Vector2 ToPage(int i, Vector2 screen) => (screen - PageOrigin(i)) / _px;

    int NearestPage(Vector2 screen)
    {
        float y = screen.Y / _px + _offY;
        int lo = 0, hi = _pageTop.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_pageTop[mid] <= y) lo = mid; else hi = mid - 1;
        }
        if (lo + 1 < _pageTop.Length && y > _pageTop[lo] + _size[lo].Y && y - (_pageTop[lo] + _size[lo].Y) > _pageTop[lo + 1] - y)
            lo++;
        return lo;
    }

    (int first, int last) VisiblePages()
    {
        float top = _offY, bottom = _offY + ViewH / _px;
        int first = NearestPage(Vector2.Zero);
        while (first > 0 && _pageTop[first - 1] + _size[first - 1].Y >= top) first--;
        int last = first;
        while (last + 1 < _pageTop.Length && _pageTop[last + 1] <= bottom) last++;
        return (first, last);
    }

    public int CurrentPage => _engine == null ? 0 : NearestPage(new Vector2(ViewW / 2f, ViewH / 3f));

    // ---- zoom & scroll -----------------------------------------------------------------

    void Clamp()
    {
        if (_engine == null) return;
        float vw = ViewW / _px, vh = ViewH / _px, m = MarginPx / _px;
        if (_docW + 2 * m <= vw) _offX = -(vw - _docW) / 2;
        else _offX = Math.Clamp(_offX, -m, _docW + m - vw);
        if (_docH + 2 * m <= vh) _offY = -m;
        else _offY = Math.Clamp(_offY, -m, _docH + m - vh);
    }

    void SetPx(float px, Vector2 anchor)
    {
        px = Math.Clamp(px, BasePx * 0.1f, BasePx * 8f);
        px = MathF.Round(px * 1000) / 1000;
        var doc = anchor / _px + new Vector2(_offX, _offY);
        _px = px;
        _offX = doc.X - anchor.X / _px;
        _offY = doc.Y - anchor.Y / _px;
        Clamp();
        Invalidate();
        RaiseView();
    }

    public void ZoomBy(float factor)
    {
        _fitWidth = false;
        SetPx(_px * factor, new Vector2(ViewW / 2f, ViewH / 2f));
    }

    public void ZoomAt(float factor, Vector2 anchor)
    {
        _fitWidth = false;
        SetPx(_px * factor, anchor);
    }

    public void SetZoomPercent(float percent)
    {
        _fitWidth = false;
        SetPx(BasePx * percent / 100f, new Vector2(ViewW / 2f, ViewH / 2f));
    }

    public void FitWidth()
    {
        if (_engine == null) return;
        _fitWidth = true;
        SetPx((ViewW - 2 * MarginPx) / _docW, new Vector2(ViewW / 2f, 0));
    }

    void OnResized()
    {
        if (_engine == null) return;
        if (_fitWidth) FitWidth();
        else Clamp();
        if (_ruler.Placed)
        {
            _ruler.Center = Vector2.Clamp(_ruler.Center, Vector2.Zero, new Vector2(ViewW, ViewH));
        }
        Invalidate();
    }

    public void GoToPage(int index)
    {
        if (_engine == null) return;
        index = Math.Clamp(index, 0, PageCount - 1);
        _offY = _pageTop[index] - MarginPx / _px;
        Clamp();
        Invalidate();
        RaiseView();
    }

    public void ScrollByScreens(float screens)
    {
        if (_engine == null) return;
        _offY += screens * ViewH * 0.9f / _px;
        Clamp();
        Invalidate();
        RaiseView();
    }

    public void ScrollToEnd(bool end)
    {
        if (_engine == null) return;
        _offY = end ? float.MaxValue / 4 : float.MinValue / 4;
        Clamp();
        Invalidate();
        RaiseView();
    }

    // ---- edits ---------------------------------------------------------------------------

    void Commit(IEnumerable<Stroke>? added = null, IEnumerable<Stroke>? removed = null)
    {
        var c = new InkChange();
        if (removed != null) c.Removed.AddRange(removed);
        if (added != null) c.Added.AddRange(added);
        if (c.IsEmpty || _store == null) return;
        _store.Apply(c, true);
        History.Push(c);
        InkEdited?.Invoke(this, EventArgs.Empty);
    }

    List<Stroke> CommitShapes(List<Stroke> shapes)
    {
        int g = shapes.Count > 1 ? Stroke.NewGroup() : 0;
        var list = shapes.Select(s => s.Clone(group: g, seq: _store!.NextSeq())).ToList();
        Commit(list);
        return list;
    }

    public void Undo()
    {
        if (_store == null || _g != Gesture.None) return;
        ClearSelection();
        if (History.Undo() is { } c)
        {
            _store.Apply(c, false);
            InkEdited?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Redo()
    {
        if (_store == null || _g != Gesture.None) return;
        ClearSelection();
        if (History.Redo() is { } c)
        {
            _store.Apply(c, true);
            InkEdited?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ToolChanged()
    {
        if (Settings.Tool != Tool.Lasso) ClearSelection();
        UpdateCursor(_hover);
        Invalidate();
    }

    public void RefreshOverlay() => Invalidate();

    // ---- self-test hooks -------------------------------------------------------------------

    internal InkStore? Store => _store;
    internal double LastDrawMs;
    TaskCompletionSource? _frameWaiter;

    internal Task NextFrame()
    {
        _frameWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Invalidate();
        return _frameWaiter.Task;
    }

    void FrameDone()
    {
        var w = _frameWaiter;
        _frameWaiter = null;
        w?.TrySetResult();
    }

    internal void TestCommit(List<Stroke> strokes) => Commit(strokes);

    /// <summary>Erases a page-space segment the same way a pointer drag does; returns the change size.</summary>
    internal (int removed, int added) TestErase(int page, Vector2 a, Vector2 b, float radiusPt, ChangeBuilder cb)
    {
        Eraser.Erase(_store!, page, a, b, radiusPt, false, cb);
        var c = cb.Build();
        return (c.Removed.Count, c.Added.Count);
    }

    internal void TestPushChange(InkChange c) => History.Push(c);
}
