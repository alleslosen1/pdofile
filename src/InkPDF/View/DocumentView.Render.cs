using System.Numerics;
using System.Runtime.InteropServices;
using InkPDF.Ink;
using InkPDF.Pdf;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;

namespace InkPDF.View;

// Rendering. Each page is drawn as 512 px tiles: a PDF tile (rendered once by PDFium on the
// worker thread), a highlighter ink tile (multiplied onto the PDF tile so text stays dark,
// the same blend the saved PDF uses) and a pen ink tile. Ink tiles are only redrawn in the dirty rectangle of an edit, which is
// what keeps erasing fast no matter how much ink or how heavy the PDF is.
public sealed partial class DocumentView
{
    const int MaxPdfTiles = 260;
    const int MaxInkTiles = 160;

    sealed class InkTile : IDisposable
    {
        public TileKey Key;
        public int X0, Y0, W, H;
        public CanvasRenderTarget? Pen, Hl;
        public bool Full = true;
        public Box Dirty = Box.Empty;
        public long Used;

        public void Dispose()
        {
            Pen?.Dispose();
            Hl?.Dispose();
            Pen = Hl = null;
        }
    }

    readonly Dictionary<TileKey, InkTile> _inkTiles = new();
    readonly Dictionary<int, List<InkTile>> _inkByPage = new();
    readonly Dictionary<TileKey, long> _tileUsed = new();
    int _fallbackLevel;
    long _frame;
    CanvasRenderTarget? _wetLayer;
    bool _wetLayerClear;
    float _tileDpi;

    const double InkBudgetMs = 10;
    readonly System.Diagnostics.Stopwatch _frameClock = new();
    bool _inkPending;
    int _fullRendersThisFrame;
    int _inkFallbackLevel;

    internal bool InkPending => _inkPending;

    /// <summary>Render target of exactly w×h device pixels at the canvas DPI (so it blits 1:1).</summary>
    CanvasRenderTarget CreateTarget(CanvasDevice dev, int w, int h) =>
        new(dev, (w + 0.25f) * 96f / _tileDpi, (h + 0.25f) * 96f / _tileDpi, _tileDpi);

    void ResetTiles()
    {
        foreach (var t in _inkTiles.Values) t.Dispose();
        _inkTiles.Clear();
        _inkByPage.Clear();
        _tileUsed.Clear();
        _fallbackLevel = 0;
        _inkFallbackLevel = 0;
        _engine?.ClearTiles();
        _wetLayer?.Dispose();
        _wetLayer = null;
    }

    /// <summary>Whole-bitmap source rectangle. With CanvasUnits.Pixels, source rectangles are in pixels.</summary>
    static Rect PxRect(CanvasBitmap b) => new(0, 0, b.SizeInPixels.Width, b.SizeInPixels.Height);

    static Color C(uint rgb, byte a = 255) => ColorHelper.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    void OnStoreChanged(int page, Box box)
    {
        if (_inkByPage.TryGetValue(page, out var list))
        {
            foreach (var t in list)
            {
                float s = t.Key.Level / 1000f;
                var b = new Box(box.MinX * s - t.X0 - 2, box.MinY * s - t.Y0 - 2, box.MaxX * s - t.X0 + 2, box.MaxY * s - t.Y0 + 2);
                if (b.MaxX < 0 || b.MaxY < 0 || b.MinX > t.W || b.MinY > t.H) continue;
                t.Dirty.Include(b);
            }
        }
        Invalidate();
    }

    void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            DrawFrame(sender, args.DrawingSession);
        }
        finally
        {
            LastDrawMs = sw.Elapsed.TotalMilliseconds;
            FrameDone();
        }
    }

    void DrawFrame(CanvasControl sender, CanvasDrawingSession ds)
    {
        ds.Units = CanvasUnits.Pixels;
        ds.Clear(BgColor);
        if (_engine == null || _store == null) return;
        _frame++;
        _frameClock.Restart();
        _inkPending = false;
        _fullRendersThisFrame = 0;
        var dev = sender.Device;
        if (sender.Dpi != _tileDpi)
        {
            // Tiles must match the screen DPI to be drawn pixel-for-pixel.
            ResetTiles();
            _tileDpi = sender.Dpi;
            _engine.Dpi = _tileDpi;
        }
        int level = Level;
        var wanted = new List<(TileKey key, float prio)>();
        var (first, last) = VisiblePages();
        bool all = true;
        for (int i = first; i <= last; i++) all &= DrawPage(ds, dev, i, level, wanted);
        if (all) _fallbackLevel = level;
        if (_inkPending) Invalidate();   // keep going next frame
        else _inkFallbackLevel = level;
        wanted.Sort((a, b) => a.prio.CompareTo(b.prio));
        _engine.SetWanted(wanted.Select(w => w.key).ToList());

        DrawSelection(ds, dev);
        DrawWet(ds, dev);
        DrawPreview(ds, dev);
        DrawLasso(ds, dev);
        if (Settings.RulerVisible)
        {
            if (!_ruler.Placed) _ruler.Place(new Vector2(ViewW / 2f, ViewH / 2f), Dpi);
            _ruler.Draw(ds, _px, Dpi);
        }
        DrawCursor(ds);
        DrawScrollbar(ds);
        Evict();
    }

    bool DrawPage(CanvasDrawingSession ds, CanvasDevice dev, int i, int level, List<(TileKey, float)> wanted)
    {
        var o = PageOrigin(i);
        int W = PageW(i), H = PageH(i);
        var pageRect = new Rect(o.X, o.Y, W, H);
        ds.FillRectangle(new Rect(o.X - 1, o.Y - 1, W + 2, H + 3), ColorHelper.FromArgb(60, 0, 0, 0));
        ds.FillRectangle(pageRect, Colors.White);

        int vx0 = Math.Max(0, (int)-o.X), vy0 = Math.Max(0, (int)-o.Y);
        int vx1 = Math.Min(W, ViewW - (int)o.X), vy1 = Math.Min(H, ViewH - (int)o.Y);
        if (vx1 <= vx0 || vy1 <= vy0) return true;
        int tx0 = vx0 / Tile, tx1 = (vx1 - 1) / Tile, ty0 = vy0 / Tile, ty1 = (vy1 - 1) / Tile;
        var tiles = _engine!.Tiles;
        var center = new Vector2(ViewW / 2f, ViewH / 2f);

        bool all = true;
        for (int ty = ty0; ty <= ty1; ty++)
        for (int tx = tx0; tx <= tx1; tx++)
        {
            var k = new TileKey(i, level, tx, ty);
            if (tiles.ContainsKey(k)) continue;
            all = false;
            wanted.Add((k, Vector2.Distance(o + new Vector2((tx + 0.5f) * Tile, (ty + 0.5f) * Tile), center)));
        }

        if (!all)
        {
            var pk = new TileKey(i, TileKey.Preview, 0, 0);
            if (tiles.TryGetValue(pk, out var pv))
            {
                _tileUsed[pk] = _frame;
                ds.DrawImage(pv, pageRect, PxRect(pv), 1, CanvasImageInterpolation.Linear);
            }
            else wanted.Add((pk, -1));
            if (_fallbackLevel != 0 && _fallbackLevel != level) DrawFallback(ds, i, o, vx0, vy0, vx1, vy1);
        }

        bool hasInk = _store!.HasStrokes(i) || _inkByPage.ContainsKey(i) || _wetHl?.Page == i;
        for (int ty = ty0; ty <= ty1; ty++)
        for (int tx = tx0; tx <= tx1; tx++)
        {
            var k = new TileKey(i, level, tx, ty);
            var it = hasInk ? GetInkTile(dev, i, level, tx, ty, W, H) : null;
            var pending = it is { Full: true } ? it : null;
            if (pending != null) it = null;
            float x = o.X + tx * Tile, y = o.Y + ty * Tile;
            if (tiles.TryGetValue(k, out var bmp))
            {
                _tileUsed[k] = _frame;
                if (it?.Hl != null) DrawMultiplied(ds, bmp, it.Hl, x, y);
                else
                {
                    var sz = bmp.SizeInPixels;
                    ds.DrawImage(bmp, new Rect(x, y, sz.Width, sz.Height), PxRect(bmp), 1, CanvasImageInterpolation.NearestNeighbor);
                }
            }
            else if (it?.Hl != null)
            {
                // PDF tile still rendering: show the highlight translucently over the preview.
                ds.DrawImage(it.Hl, new Rect(x, y, it.W, it.H), PxRect(it.Hl), 0.55f, CanvasImageInterpolation.NearestNeighbor);
            }
            if (it?.Pen != null)
                ds.DrawImage(it.Pen, new Rect(x, y, it.W, it.H), PxRect(it.Pen), 1, CanvasImageInterpolation.NearestNeighbor);
            if (pending != null) DrawInkFallback(ds, i, o, pending);
        }
        return all;
    }

    /// <summary>Until an ink tile is rendered at the new zoom, stretch the last complete level's ink.</summary>
    void DrawInkFallback(CanvasDrawingSession ds, int page, Vector2 o, InkTile cur)
    {
        if (_inkFallbackLevel == 0 || _inkFallbackLevel == cur.Key.Level) return;
        float ratio = _px / (_inkFallbackLevel / 1000f);
        int fx0 = (int)(cur.X0 / ratio) / Tile, fx1 = (int)((cur.X0 + cur.W - 1) / ratio) / Tile;
        int fy0 = (int)(cur.Y0 / ratio) / Tile, fy1 = (int)((cur.Y0 + cur.H - 1) / ratio) / Tile;
        using (ds.CreateLayer(1f, new Rect(o.X + cur.X0, o.Y + cur.Y0, cur.W, cur.H)))
        {
            for (int ty = fy0; ty <= fy1; ty++)
            for (int tx = fx0; tx <= fx1; tx++)
            {
                if (!_inkTiles.TryGetValue(new TileKey(page, _inkFallbackLevel, tx, ty), out var ft) || ft.Full) continue;
                ft.Used = _frame;
                var dst = new Rect(o.X + ft.X0 * ratio, o.Y + ft.Y0 * ratio, ft.W * ratio, ft.H * ratio);
                if (ft.Hl != null) ds.DrawImage(ft.Hl, dst, PxRect(ft.Hl), 0.55f, CanvasImageInterpolation.Linear);
                if (ft.Pen != null) ds.DrawImage(ft.Pen, dst, PxRect(ft.Pen), 1, CanvasImageInterpolation.Linear);
            }
        }
    }

    /// <summary>While new-zoom tiles render, stretch the last complete zoom level's tiles.</summary>
    void DrawFallback(CanvasDrawingSession ds, int page, Vector2 o, int vx0, int vy0, int vx1, int vy1)
    {
        float fs = _fallbackLevel / 1000f, ratio = _px / fs;
        int fx0 = (int)(vx0 / ratio) / Tile, fx1 = (int)((vx1 - 1) / ratio) / Tile;
        int fy0 = (int)(vy0 / ratio) / Tile, fy1 = (int)((vy1 - 1) / ratio) / Tile;
        for (int ty = fy0; ty <= fy1; ty++)
        for (int tx = fx0; tx <= fx1; tx++)
        {
            var k = new TileKey(page, _fallbackLevel, tx, ty);
            if (!_engine!.Tiles.TryGetValue(k, out var bmp)) continue;
            _tileUsed[k] = _frame;
            var sz = bmp.SizeInPixels;
            ds.DrawImage(bmp, new Rect(o.X + tx * Tile * ratio, o.Y + ty * Tile * ratio, sz.Width * ratio, sz.Height * ratio),
                PxRect(bmp), 1, CanvasImageInterpolation.Linear);
        }
    }

    InkTile GetInkTile(CanvasDevice dev, int page, int level, int tx, int ty, int pageW, int pageH)
    {
        var key = new TileKey(page, level, tx, ty);
        if (!_inkTiles.TryGetValue(key, out var t))
        {
            t = new InkTile
            {
                Key = key,
                X0 = tx * Tile,
                Y0 = ty * Tile,
                W = Math.Max(1, Math.Min(Tile, pageW - tx * Tile)),
                H = Math.Max(1, Math.Min(Tile, pageH - ty * Tile)),
            };
            _inkTiles[key] = t;
            if (!_inkByPage.TryGetValue(page, out var list)) _inkByPage[page] = list = new List<InkTile>();
            list.Add(t);
        }
        t.Used = _frame;
        if (t.Full)
        {
            // Full tile renders are the expensive ones (every stroke in the tile). Spread them over
            // frames so a dense page or a zoom step never stalls the UI; edits stay immediate.
            if (_fullRendersThisFrame > 0 && _frameClock.Elapsed.TotalMilliseconds > InkBudgetMs)
            {
                _inkPending = true;
                return t;
            }
            _fullRendersThisFrame++;
            RenderInkTile(dev, t);
        }
        else if (!t.Dirty.IsEmpty) RenderInkTile(dev, t);
        return t;
    }

    void RenderInkTile(CanvasDevice dev, InkTile t)
    {
        float s = t.Key.Level / 1000f;
        Rect region;
        if (t.Full) region = new Rect(0, 0, t.W, t.H);
        else
        {
            int rx0 = Math.Max(0, (int)MathF.Floor(t.Dirty.MinX)), ry0 = Math.Max(0, (int)MathF.Floor(t.Dirty.MinY));
            int rx1 = Math.Min(t.W, (int)MathF.Ceiling(t.Dirty.MaxX)), ry1 = Math.Min(t.H, (int)MathF.Ceiling(t.Dirty.MaxY));
            t.Dirty = Box.Empty;
            if (rx1 <= rx0 || ry1 <= ry0) return;
            region = new Rect(rx0, ry0, rx1 - rx0, ry1 - ry0);
        }

        var ptBox = new Box((t.X0 + (float)region.X) / s, (t.Y0 + (float)region.Y) / s,
                            (t.X0 + (float)region.Right) / s, (t.Y0 + (float)region.Bottom) / s);
        var strokes = _store!.Query(t.Key.Page, ptBox);
        strokes.RemoveAll(x => x.Selected);
        if (_wetHl is { } wet && wet.Page == t.Key.Page && wet.Bounds.Intersects(ptBox)) strokes.Add(wet);
        bool hasPen = strokes.Any(x => x.Kind == StrokeKind.Pen);
        bool hasHl = strokes.Any(x => x.Kind == StrokeKind.Highlighter);

        // A layer appearing for the first time must be drawn for the whole tile.
        if (!t.Full && ((hasPen && t.Pen == null) || (hasHl && t.Hl == null)))
        {
            t.Full = true;
            RenderInkTile(dev, t);
            return;
        }
        bool full = t.Full;
        t.Full = false;
        t.Dirty = Box.Empty;
        if (full)
        {
            if (hasPen) t.Pen ??= CreateTarget(dev, t.W, t.H);
            if (hasHl) t.Hl ??= CreateTarget(dev, t.W, t.H);
        }
        strokes.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        var m = Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(-t.X0, -t.Y0);
        if (t.Pen != null) DrawInkLayer(dev, t.Pen, region, full, Colors.Transparent, strokes, StrokeKind.Pen, m);
        if (t.Hl != null) DrawInkLayer(dev, t.Hl, region, full, Colors.Transparent, strokes, StrokeKind.Highlighter, m);
    }

    static void DrawInkLayer(CanvasDevice dev, CanvasRenderTarget target, Rect region, bool full, Color clear,
        List<Stroke> strokes, StrokeKind kind, Matrix3x2 m)
    {
        using var ds = target.CreateDrawingSession();
        ds.Units = CanvasUnits.Pixels;
        if (full)
        {
            ds.Clear(clear);
            ds.Transform = m;
            foreach (var s in strokes) if (s.Kind == kind) InkGeometry.Draw(ds, s, C(s.Color));
            return;
        }
        using (ds.CreateLayer(1f, region))
        {
            ds.Blend = CanvasBlend.Copy;
            ds.FillRectangle(region, clear);
            ds.Blend = CanvasBlend.SourceOver;
            ds.Transform = m;
            foreach (var s in strokes) if (s.Kind == kind) InkGeometry.Draw(ds, s, C(s.Color));
        }
    }

    /// <summary>PDF tile with the highlighter tile multiplied on top (a GPU blend effect).</summary>
    static void DrawMultiplied(CanvasDrawingSession ds, CanvasBitmap page, CanvasBitmap highlight, float x, float y)
    {
        using var blend = new BlendEffect { Mode = BlendEffectMode.Multiply, Background = page, Foreground = highlight };
        ds.DrawImage(blend, x, y);
    }

    void Evict()
    {
        var tiles = _engine!.Tiles;
        if (tiles.Count > MaxPdfTiles)
        {
            var victims = new List<(TileKey k, long used)>();
            foreach (var k in tiles.Keys)
            {
                if (!_tileUsed.TryGetValue(k, out long used))
                {
                    _tileUsed[k] = _frame;   // just arrived; give it a chance to be drawn
                    continue;
                }
                if (used < _frame) victims.Add((k, used));
            }
            foreach (var (k, _) in victims.OrderBy(v => v.used).Take(tiles.Count - MaxPdfTiles))
            {
                if (tiles.TryRemove(k, out var b)) b.Dispose();
                _tileUsed.Remove(k);
            }
        }
        if (_inkTiles.Count > MaxInkTiles)
        {
            foreach (var t in _inkTiles.Values.Where(t => t.Used < _frame).OrderBy(t => t.Used).Take(_inkTiles.Count - MaxInkTiles).ToList())
            {
                _inkTiles.Remove(t.Key);
                if (_inkByPage.TryGetValue(t.Key.Page, out var list))
                {
                    list.Remove(t);
                    if (list.Count == 0) _inkByPage.Remove(t.Key.Page);
                }
                t.Dispose();
            }
        }
    }

    // ---- overlays ----------------------------------------------------------------------

    CanvasRenderTarget ScreenTarget(CanvasDevice dev, ref CanvasRenderTarget? target)
    {
        if (target == null || target.SizeInPixels.Width != ViewW || target.SizeInPixels.Height != ViewH)
        {
            target?.Dispose();
            target = CreateTarget(dev, ViewW, ViewH);
        }
        return target;
    }

    void DrawWet(CanvasDrawingSession ds, CanvasDevice dev)
    {
        var w = _wet;
        if (w == null || w.Hidden) return;
        var ctrl = CollectionsMarshal.AsSpan(w.Builder.Control);
        int n = ctrl.Length;
        if (n == 0) return;
        var m = PageMatrix(w.Page);
        var full = new Rect(0, 0, ViewW, ViewH);

        // Highlighters in progress are drawn through the ink tiles (see _wetHl).
        if (w.Kind == StrokeKind.Highlighter) return;

        // Pen: finished segments are baked into a screen layer in batches; only the tail
        // (whose spline still depends on upcoming points) is rebuilt each frame.
        var layer = ScreenTarget(dev, ref _wetLayer);
        if (_wetLayerClear)
        {
            using var lds = layer.CreateDrawingSession();
            lds.Units = CanvasUnits.Pixels;
            lds.Clear(Colors.Transparent);
            _wetLayerClear = false;
        }
        int finalEnd = n - 2;
        if (finalEnd - w.Frozen >= 12)
        {
            var pts = new List<InkPoint> { ctrl[w.Frozen] };
            for (int i = w.Frozen; i < finalEnd; i++) StrokeMath.AppendSegment(pts, ctrl, i, true);
            using (var lds = layer.CreateDrawingSession())
            {
                lds.Units = CanvasUnits.Pixels;
                lds.Transform = m;
                InkGeometry.Draw(lds, CollectionsMarshal.AsSpan(pts), C(w.Color));
            }
            w.Frozen = finalEnd;
        }
        ds.DrawImage(layer, full, PxRect(layer), 1, CanvasImageInterpolation.NearestNeighbor);

        var tail = new List<InkPoint> { ctrl[w.Frozen] };
        for (int i = w.Frozen; i < n - 1; i++) StrokeMath.AppendSegment(tail, ctrl, i, true);
        ds.Transform = m;
        InkGeometry.Draw(ds, CollectionsMarshal.AsSpan(tail), C(w.Color));
        ds.Transform = Matrix3x2.Identity;
    }

    void DrawStrokes(CanvasDrawingSession ds, CanvasDevice dev, IEnumerable<Stroke> strokes, Matrix3x2 m, bool cached)
    {
        var list = strokes as IList<Stroke> ?? strokes.ToList();
        if (list.Count == 0) return;
        if (list.Any(s => s.Kind == StrokeKind.Highlighter))
        {
            using (ds.CreateLayer(0.65f))
            {
                ds.Transform = m;
                foreach (var s in list) if (s.Kind == StrokeKind.Highlighter) Fill(s);
                ds.Transform = Matrix3x2.Identity;
            }
        }
        ds.Transform = m;
        foreach (var s in list) if (s.Kind == StrokeKind.Pen) Fill(s);
        ds.Transform = Matrix3x2.Identity;

        void Fill(Stroke s) => InkGeometry.Draw(ds, s, C(s.Color));
    }

    void DrawPreview(CanvasDrawingSession ds, CanvasDevice dev)
    {
        if (_preview == null || _preview.Count == 0) return;
        DrawStrokes(ds, dev, _preview, PageMatrix(_preview[0].Page), cached: false);
    }

    static readonly CanvasStrokeStyle Dashed = new() { DashStyle = CanvasDashStyle.Dash };
    static readonly Color Accent = ColorHelper.FromArgb(255, 0x1A, 0x73, 0xE8);

    void DrawLasso(CanvasDrawingSession ds, CanvasDevice dev)
    {
        if (_lasso == null || _lasso.Count < 2) return;
        var m = PageMatrix(_gPage);
        using var pb = new CanvasPathBuilder(dev);
        pb.BeginFigure(Vector2.Transform(_lasso[0], m));
        for (int i = 1; i < _lasso.Count; i++) pb.AddLine(Vector2.Transform(_lasso[i], m));
        pb.EndFigure(CanvasFigureLoop.Open);
        using var g = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(g, Colors.White, 3 * Dpi);
        ds.DrawGeometry(g, Accent, 1.5f * Dpi, Dashed);
    }

    void DrawSelection(CanvasDrawingSession ds, CanvasDevice dev)
    {
        if (_sel == null) return;
        var m = _sel.Transform * PageMatrix(_sel.Page);
        DrawStrokes(ds, dev, _sel.Strokes, m, cached: true);

        var c = SelectionCorners();
        using (var g = CanvasGeometry.CreatePolygon(dev, c))
        {
            ds.DrawGeometry(g, Colors.White, 3 * Dpi);
            ds.DrawGeometry(g, Accent, 1.5f * Dpi, Dashed);
        }
        if (_g == Gesture.Transform) return;
        float h = 5 * Dpi;
        foreach (var p in c)
        {
            ds.FillRectangle(p.X - h, p.Y - h, 2 * h, 2 * h, Colors.White);
            ds.DrawRectangle(p.X - h, p.Y - h, 2 * h, 2 * h, Accent, 1.5f * Dpi);
        }
        var knob = RotateKnob(c);
        ds.DrawLine((c[0] + c[1]) / 2, knob, Accent, 1.5f * Dpi);
        ds.FillCircle(knob, 6 * Dpi, Colors.White);
        ds.DrawCircle(knob, 6 * Dpi, Accent, 1.5f * Dpi);
    }

    void DrawCursor(CanvasDrawingSession ds)
    {
        if (!_hoverOn || _g is Gesture.Pan or Gesture.Transform or Gesture.RulerMove or Gesture.RulerRotate or Gesture.Lasso) return;
        bool eraser = _g == Gesture.Erase || (_g == Gesture.None && (Settings.Tool == Tool.Eraser || _hoverEraser));
        if (eraser)
        {
            float r = Settings.EraserRadius * Dpi;
            ds.FillCircle(_hover, r, ColorHelper.FromArgb(40, 0, 0, 0));
            ds.DrawCircle(_hover, r, ColorHelper.FromArgb(200, 255, 255, 255), 2.5f * Dpi);
            ds.DrawCircle(_hover, r, ColorHelper.FromArgb(220, 60, 60, 60), Dpi);
            return;
        }
        if (_g != Gesture.None) return;
        if (Settings.Tool is Tool.Pen or Tool.Highlighter)
        {
            bool hl = Settings.Tool == Tool.Highlighter;
            float r = MathF.Max(1.5f * Dpi, (hl ? Settings.HlWidth : Settings.PenWidth) / 2 * _px);
            ds.FillCircle(_hover, r, C(hl ? Settings.HlColor : Settings.PenColor, hl ? (byte)160 : (byte)255));
            ds.DrawCircle(_hover, r + Dpi, ColorHelper.FromArgb(120, 255, 255, 255), Dpi);
        }
    }

    // ---- scrollbar -----------------------------------------------------------------------

    bool ScrollbarVisible => _engine != null && (_docH + 2 * MarginPx / _px) * _px > ViewH;

    Rect ScrollThumb()
    {
        float m = MarginPx / _px;
        float total = _docH + 2 * m, vh = ViewH / _px;
        float pad = 4 * Dpi, track = ViewH - 2 * pad;
        float th = MathF.Max(32 * Dpi, track * vh / total);
        float frac = Math.Clamp((_offY + m) / MathF.Max(1e-3f, total - vh), 0f, 1f);
        return new Rect(ViewW - 10 * Dpi, pad + frac * (track - th), 6 * Dpi, th);
    }

    void DrawScrollbar(CanvasDrawingSession ds)
    {
        if (!ScrollbarVisible) return;
        var r = ScrollThumb();
        ds.FillRoundedRectangle(r, 3 * Dpi, 3 * Dpi, ColorHelper.FromArgb(_g == Gesture.Scrollbar ? (byte)170 : (byte)110, 60, 64, 72));
    }
}
