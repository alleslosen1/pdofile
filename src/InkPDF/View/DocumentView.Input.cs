using System.Numerics;
using InkPDF.Ink;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace InkPDF.View;

public sealed partial class DocumentView
{
    enum Gesture { None, Pen, Erase, Lasso, Shape, Pan, RulerMove, RulerRotate, RulerLine, Transform, Scrollbar }
    enum TransformMode { None, Move, Scale, Rotate }

    sealed class WetInk
    {
        public int Page;
        public StrokeKind Kind;
        public uint Color;
        public required StrokeBuilder Builder;
        public int Frozen;
        public bool Hidden;
    }

    sealed class Selection
    {
        public int Page;
        public List<Stroke> Strokes = new();
        public Box Bounds;
        public Matrix3x2 Transform = Matrix3x2.Identity;
    }

    Gesture _g;
    uint _pid;
    ulong _lastTs;
    Vector2 _gLast;
    int _gPage;

    WetInk? _wet;
    /// <summary>The highlighter stroke being drawn, rendered through the ink tiles like finished ink.</summary>
    Stroke? _wetHl;
    Vector2 _holdAnchor;
    long _holdSince;
    ShapeResult? _snap;

    ChangeBuilder? _erase;
    Vector2 _eraseLast;

    List<Vector2>? _lasso;
    List<Stroke>? _preview;
    Vector2 _shapeStart;

    readonly Ruler _ruler = new();
    float _rulerGrab;
    Vector2 _rulerOffset;
    int _rulerSide;
    Vector2 _rulerLineStart;

    Selection? _sel;
    TransformMode _tm;
    Vector2 _tStart, _tAnchor;
    List<Stroke>? _clipboard;
    int _pasteCount;

    float _scrollGrab;

    Vector2 _hover;
    bool _hoverOn, _hoverEraser;

    static bool IsKeyDown(VirtualKey k) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    // ---- pointer dispatch ------------------------------------------------------------------

    void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == null || _store == null) return;
        _canvas.Focus(FocusState.Pointer);
        e.Handled = true;
        if (_g != Gesture.None) return;   // ignore a second pointer while one is active

        var pt = e.GetCurrentPoint(_canvas);
        var pos = ToPx(pt.Position);
        var props = pt.Properties;
        var dev = e.Pointer.PointerDeviceType;
        _pid = e.Pointer.PointerId;
        _lastTs = pt.Timestamp;
        _gLast = pos;
        _hover = pos;
        _canvas.CapturePointer(e.Pointer);

        if (ScrollbarVisible && pos.X > ViewW - 16 * Dpi && dev != PointerDeviceType.Touch)
        {
            BeginScrollbar(pos);
            return;
        }
        if (Settings.RulerVisible && dev != PointerDeviceType.Touch)
        {
            if (_ruler.HitKnob(pos))
            {
                _g = Gesture.RulerRotate;
                _rulerGrab = AngleOf(pos - _ruler.Center) - _ruler.Angle;
                return;
            }
            if (_ruler.HitBody(pos))
            {
                _g = Gesture.RulerMove;
                _rulerOffset = _ruler.Center - pos;
                return;
            }
        }
        if (props.IsMiddleButtonPressed || dev == PointerDeviceType.Touch || Settings.Tool == Tool.Hand || SpaceDown)
        {
            _g = Gesture.Pan;
            return;
        }
        if (props.IsEraser || props.IsRightButtonPressed || props.IsBarrelButtonPressed)
        {
            BeginErase(pos);
            return;
        }
        if (_sel != null)
        {
            var mode = HitSelection(pos);
            if (mode != TransformMode.None)
            {
                BeginTransform(pos, mode);
                return;
            }
            ClearSelection();
        }
        switch (Settings.Tool)
        {
            case Tool.Pen:
            case Tool.Highlighter:
                if (Settings.RulerVisible)
                {
                    int side = _ruler.EdgeSide(pos, 24 * Dpi);
                    if (side != 0)
                    {
                        BeginRulerLine(pos, side);
                        return;
                    }
                }
                BeginPen(pos, pt, dev);
                break;
            case Tool.Eraser:
                BeginErase(pos);
                break;
            case Tool.Lasso:
                BeginLasso(pos);
                break;
            default:
                BeginShape(pos);
                break;
        }
        Invalidate();
    }

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == null) return;
        var cur = e.GetCurrentPoint(_canvas);
        var pos = ToPx(cur.Position);
        _hover = pos;
        _hoverOn = e.Pointer.PointerDeviceType != PointerDeviceType.Touch;
        _hoverEraser = cur.Properties.IsEraser || cur.Properties.IsInverted;

        if (_g == Gesture.None || e.Pointer.PointerId != _pid)
        {
            UpdateCursor(pos);
            if (_hoverOn) Invalidate();
            return;
        }
        e.Handled = true;

        var pts = e.GetIntermediatePoints(_canvas).OrderBy(p => p.Timestamp).ToList();
        foreach (var p in pts)
        {
            if (p.Timestamp <= _lastTs) continue;
            _lastTs = p.Timestamp;
            Move(ToPx(p.Position), p);
        }
        Invalidate();
    }

    void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_g == Gesture.None || e.Pointer.PointerId != _pid) return;
        e.Handled = true;
        var pt = e.GetCurrentPoint(_canvas);
        // The release sample often carries zero pressure; don't feed it to the pen.
        if (_g != Gesture.Pen) Move(ToPx(pt.Position), pt);
        EndGesture();
        _canvas.ReleasePointerCapture(e.Pointer);
    }

    void OnPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_g == Gesture.None || e.Pointer.PointerId != _pid) return;
        EndGesture();
    }

    void Move(Vector2 pos, PointerPoint p)
    {
        switch (_g)
        {
            case Gesture.Pen: MovePen(pos, p); break;
            case Gesture.Erase: EraseTo(pos); break;
            case Gesture.Lasso: MoveLasso(pos); break;
            case Gesture.Shape: MoveShape(pos); break;
            case Gesture.RulerLine: MoveRulerLine(pos); break;
            case Gesture.Transform: MoveTransform(pos); break;
            case Gesture.Scrollbar: MoveScrollbar(pos); break;
            case Gesture.Pan:
                var d = pos - _gLast;
                _offX -= d.X / _px;
                _offY -= d.Y / _px;
                Clamp();
                RaiseView();
                break;
            case Gesture.RulerMove:
                _ruler.Center = pos + _rulerOffset;
                break;
            case Gesture.RulerRotate:
                float a = AngleOf(pos - _ruler.Center) - _rulerGrab;
                float deg = a * 180 / MathF.PI;
                float snapped = MathF.Round(deg / 15) * 15;
                if (MathF.Abs(deg - snapped) < 1.5f) a = snapped * MathF.PI / 180;
                _ruler.Angle = a;
                break;
        }
        _gLast = pos;
    }

    void EndGesture()
    {
        var g = _g;
        _g = Gesture.None;
        switch (g)
        {
            case Gesture.Pen: EndPen(); break;
            case Gesture.Erase: EndErase(); break;
            case Gesture.Lasso: EndLasso(); break;
            case Gesture.Shape:
            case Gesture.RulerLine:
                if (_preview != null && _preview.Count > 0 && _preview[0].Bounds.Width + _preview[0].Bounds.Height > 3 / _px * 4)
                    CommitShapes(_preview);
                _preview = null;
                break;
            case Gesture.Transform: EndTransform(); break;
        }
        _holdTimer.Stop();
        UpdateCursor(_hover);
        Invalidate();
    }

    static float AngleOf(Vector2 v) => MathF.Atan2(v.Y, v.X);

    // ---- pen ---------------------------------------------------------------------------

    void BeginPen(Vector2 pos, PointerPoint pt, PointerDeviceType dev)
    {
        bool hl = Settings.Tool == Tool.Highlighter;
        _gPage = NearestPage(pos);
        float baseR = (hl ? Settings.HlWidth : Settings.PenWidth) / 2;
        var b = new StrokeBuilder(baseR, !hl && Settings.Pressure && dev == PointerDeviceType.Pen, Settings.Smoothing);
        _wet = new WetInk
        {
            Page = _gPage,
            Kind = hl ? StrokeKind.Highlighter : StrokeKind.Pen,
            Color = hl ? Settings.HlColor : Settings.PenColor,
            Builder = b,
        };
        _wetLayerClear = true;
        b.Add(ToPage(_gPage, pos), pt.Properties.Pressure, pt.Timestamp);
        UpdateWetHighlight();
        _g = Gesture.Pen;
        _snap = null;
        _holdAnchor = pos;
        _holdSince = Environment.TickCount64;
        if (Settings.HoldToShape) _holdTimer.Start();
    }

    void MovePen(Vector2 pos, PointerPoint p)
    {
        if (Vector2.Distance(pos, _holdAnchor) > 4 * Dpi)
        {
            _holdAnchor = pos;
            _holdSince = Environment.TickCount64;
        }
        var loc = ToPage(_gPage, pos);
        if (_snap == null && _wet != null && IsKeyDown(VirtualKey.Shift))
        {
            // Shift held: straight line from where the stroke started, at the nominal width.
            float r = (_wet.Kind == StrokeKind.Highlighter ? Settings.HlWidth : Settings.PenWidth) / 2;
            _snap = new ShapeResult(ShapeKind.Line, [_wet.Builder.Control[0].P, loc], r);
            _wet.Hidden = true;
            UpdateWetHighlight();
        }
        if (_snap != null)
        {
            // After snapping to a line, the free end follows the pen until it lifts
            // (snapping to horizontal / vertical / 45° when within 3°).
            if (_snap.Kind == ShapeKind.Line)
            {
                _snap = _snap with { Points = [_snap.Points[0], Shapes.SnapAngle(_snap.Points[0], loc, false)] };
                _preview = Shapes.FromResult(_gPage, _snap, _wet!.Color, _wet.Kind);
            }
            return;
        }
        _wet!.Builder.Add(loc, p.Properties.Pressure, p.Timestamp);
        UpdateWetHighlight();
    }

    void UpdateWetHighlight()
    {
        var w = _wet;
        var old = _wetHl;
        _wetHl = w is { Kind: StrokeKind.Highlighter, Hidden: false } && w.Builder.Control.Count > 0
            ? new Stroke
            {
                Page = w.Page, Points = w.Builder.Control.ToArray(), Kind = StrokeKind.Highlighter,
                Color = w.Color, Smooth = true, Seq = long.MaxValue,
            }
            : null;
        if (old != null) OnStoreChanged(old.Page, old.Bounds);
        if (_wetHl != null) OnStoreChanged(_wetHl.Page, _wetHl.Bounds);
    }

    void OnHoldTick()
    {
        if (_g != Gesture.Pen || _snap != null || _wet == null)
        {
            _holdTimer.Stop();
            return;
        }
        if (Environment.TickCount64 - _holdSince < 600) return;
        _holdTimer.Stop();
        if (_wet.Builder.Length < 10) return;
        var shape = ShapeRecognizer.Recognize(_wet.Builder.Control);
        if (shape == null) return;
        if (_wet.Kind == StrokeKind.Highlighter && shape.Kind != ShapeKind.Line) return;
        _snap = shape;
        _wet.Hidden = true;
        UpdateWetHighlight();
        _preview = Shapes.FromResult(_wet.Page, shape, _wet.Color, _wet.Kind);
        Invalidate();
    }

    void EndPen()
    {
        _holdTimer.Stop();
        var w = _wet;
        _wet = null;
        UpdateWetHighlight();
        if (w == null) return;
        if (_snap != null && _preview != null)
        {
            CommitShapes(_preview);
            _preview = null;
            _snap = null;
            return;
        }
        var pts = w.Builder.Finish();
        if (pts.Length == 0) return;
        Commit([new Stroke
        {
            Page = w.Page,
            Points = pts,
            Kind = w.Kind,
            Color = w.Color,
            Smooth = true,
            Seq = _store!.NextSeq(),
        }]);
    }

    // ---- eraser ------------------------------------------------------------------------

    void BeginErase(Vector2 pos)
    {
        _g = Gesture.Erase;
        _erase = new ChangeBuilder();
        _gPage = NearestPage(pos);
        _eraseLast = ToPage(_gPage, pos);
        EraseTo(pos);
    }

    void EraseTo(Vector2 pos)
    {
        int page = NearestPage(pos);
        var loc = ToPage(page, pos);
        if (page != _gPage)
        {
            _gPage = page;
            _eraseLast = loc;
        }
        float r = Settings.EraserRadius * Dpi / _px;
        Eraser.Erase(_store!, page, _eraseLast, loc, r, Settings.EraserWholeStroke, _erase!);
        _eraseLast = loc;
    }

    void EndErase()
    {
        var c = _erase?.Build();
        _erase = null;
        if (c == null || c.IsEmpty) return;
        History.Push(c);
        InkEdited?.Invoke(this, EventArgs.Empty);
    }

    // ---- shapes & ruler ----------------------------------------------------------------

    void BeginShape(Vector2 pos)
    {
        _g = Gesture.Shape;
        _gPage = NearestPage(pos);
        _shapeStart = ToPage(_gPage, pos);
        _preview = null;
    }

    void MoveShape(Vector2 pos)
    {
        var p = ToPage(_gPage, pos);
        _preview = Shapes.FromDrag(Settings.Tool, _gPage, _shapeStart, p, IsKeyDown(VirtualKey.Shift), Settings, Settings.PenWidth / 2, Settings.PenColor);
    }

    void BeginRulerLine(Vector2 pos, int side)
    {
        _g = Gesture.RulerLine;
        _rulerSide = side;
        _gPage = NearestPage(pos);
        _rulerLineStart = pos;
        MoveRulerLine(pos);
    }

    void MoveRulerLine(Vector2 pos)
    {
        bool hl = Settings.Tool == Tool.Highlighter;
        float r = (hl ? Settings.HlWidth : Settings.PenWidth) / 2;
        var a = ToPage(_gPage, _ruler.ProjectToEdge(_rulerLineStart, _rulerSide, r * _px));
        var b = ToPage(_gPage, _ruler.ProjectToEdge(pos, _rulerSide, r * _px));
        _preview = Shapes.Line(_gPage, a, b, r, hl ? Settings.HlColor : Settings.PenColor, hl ? StrokeKind.Highlighter : StrokeKind.Pen);
    }

    // ---- lasso & selection -----------------------------------------------------------

    void BeginLasso(Vector2 pos)
    {
        _g = Gesture.Lasso;
        _gPage = NearestPage(pos);
        _lasso = [ToPage(_gPage, pos)];
    }

    void MoveLasso(Vector2 pos)
    {
        var p = ToPage(_gPage, pos);
        if (Vector2.Distance(p, _lasso![^1]) * _px > 2) _lasso.Add(p);
    }

    void EndLasso()
    {
        var poly = _lasso;
        _lasso = null;
        if (poly == null || poly.Count < 3) return;
        var box = Box.Empty;
        foreach (var p in poly) box.Include(p.X, p.Y);
        var picked = new List<Stroke>();
        foreach (var s in _store!.Query(_gPage, box))
        {
            var pts = s.RenderPoints;
            int inside = 0;
            foreach (var p in pts) if (StrokeMath.PointInPolygon(p.P, poly)) inside++;
            if (inside * 2 >= pts.Length) picked.Add(s);
        }
        SetSelection(_gPage, picked);
    }

    void SetSelection(int page, List<Stroke> strokes)
    {
        ClearSelection();
        if (strokes.Count == 0 || _store == null) return;
        var groups = strokes.Where(s => s.Group != 0).Select(s => s.Group).ToHashSet();
        var all = strokes.ToHashSet();
        foreach (var s in _store.GroupMembers(page, groups)) all.Add(s);
        _sel = new Selection { Page = page, Strokes = all.OrderBy(s => s.Seq).ToList() };
        MarkSelected(_sel.Strokes, true);
        RecomputeSelectionBounds();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    void MarkSelected(IEnumerable<Stroke> strokes, bool selected)
    {
        foreach (var s in strokes)
        {
            s.Selected = selected;
            OnStoreChanged(s.Page, s.Bounds);
        }
    }

    void RecomputeSelectionBounds()
    {
        var b = Box.Empty;
        foreach (var s in _sel!.Strokes) b.Include(s.Bounds);
        _sel.Bounds = b;
    }

    public void ClearSelection()
    {
        if (_sel == null) return;
        MarkSelected(_sel.Strokes, false);
        _sel = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    Vector2[] SelectionCorners()
    {
        var b = _sel!.Bounds.Inflate(4 / _px);
        var m = _sel.Transform * PageMatrix(_sel.Page);
        return
        [
            Vector2.Transform(new Vector2(b.MinX, b.MinY), m),
            Vector2.Transform(new Vector2(b.MaxX, b.MinY), m),
            Vector2.Transform(new Vector2(b.MaxX, b.MaxY), m),
            Vector2.Transform(new Vector2(b.MinX, b.MaxY), m),
        ];
    }

    Vector2 RotateKnob(Vector2[] c) => (c[0] + c[1]) / 2 - new Vector2(0, 26 * Dpi);

    int _scaleCorner;

    TransformMode HitSelection(Vector2 pos)
    {
        var c = SelectionCorners();
        float tol = 10 * Dpi;
        if (Vector2.Distance(pos, RotateKnob(c)) <= tol) return TransformMode.Rotate;
        for (int i = 0; i < 4; i++)
        {
            if (Vector2.Distance(pos, c[i]) <= tol)
            {
                _scaleCorner = i;
                return TransformMode.Scale;
            }
        }
        var minX = c.Min(p => p.X); var maxX = c.Max(p => p.X);
        var minY = c.Min(p => p.Y); var maxY = c.Max(p => p.Y);
        return pos.X >= minX && pos.X <= maxX && pos.Y >= minY && pos.Y <= maxY ? TransformMode.Move : TransformMode.None;
    }

    void BeginTransform(Vector2 pos, TransformMode mode)
    {
        _g = Gesture.Transform;
        _tm = mode;
        _sel!.Transform = Matrix3x2.Identity;
        _tStart = ToPage(_sel.Page, pos);
        var b = _sel.Bounds;
        Vector2[] corners = [new(b.MinX, b.MinY), new(b.MaxX, b.MinY), new(b.MaxX, b.MaxY), new(b.MinX, b.MaxY)];
        _tAnchor = mode == TransformMode.Scale ? corners[(_scaleCorner + 2) % 4] : b.Center;
    }

    void MoveTransform(Vector2 pos)
    {
        var p = ToPage(_sel!.Page, pos);
        switch (_tm)
        {
            case TransformMode.Move:
                _sel.Transform = Matrix3x2.CreateTranslation(p - _tStart);
                break;
            case TransformMode.Scale:
            {
                float d0 = Vector2.Distance(_tStart, _tAnchor);
                float f = d0 < 1e-3f ? 1 : Math.Clamp(Vector2.Distance(p, _tAnchor) / d0, 0.05f, 50f);
                _sel.Transform = Matrix3x2.CreateScale(f, _tAnchor);
                break;
            }
            case TransformMode.Rotate:
            {
                float a = AngleOf(p - _tAnchor) - AngleOf(_tStart - _tAnchor);
                if (IsKeyDown(VirtualKey.Shift)) a = MathF.Round(a / (MathF.PI / 12)) * (MathF.PI / 12);
                _sel.Transform = Matrix3x2.CreateRotation(a, _tAnchor);
                break;
            }
        }
    }

    void EndTransform()
    {
        var sel = _sel;
        if (sel == null) return;
        var t = sel.Transform;
        sel.Transform = Matrix3x2.Identity;
        if (t.IsIdentity) return;
        ReplaceSelection(sel.Strokes.Select(s => s.Transformed(t)).ToList());
    }

    /// <summary>Swaps the selected strokes for edited copies (one undo step) and keeps them selected.</summary>
    void ReplaceSelection(List<Stroke> replacement)
    {
        var sel = _sel!;
        foreach (var s in replacement) s.Selected = true;
        var old = sel.Strokes;
        Commit(replacement, old);
        foreach (var s in old) s.Selected = false;
        sel.Strokes = replacement;
        RecomputeSelectionBounds();
        Invalidate();
    }

    public void DeleteSelection()
    {
        if (_sel == null) return;
        var old = _sel.Strokes;
        _sel = null;
        foreach (var s in old) s.Selected = false;
        Commit(removed: old);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void RecolorSelection(uint color)
    {
        if (_sel == null) return;
        ReplaceSelection(_sel.Strokes.Select(s => s.Kind == StrokeKind.Pen && s.Color != 0xBDBDBD ? s.Clone(color: color) : s.Clone()).ToList());
    }

    public void CopySelection()
    {
        if (_sel == null) return;
        _clipboard = _sel.Strokes.ToList();
        _pasteCount = 0;
    }

    public void CutSelection()
    {
        CopySelection();
        DeleteSelection();
    }

    public void Paste()
    {
        if (_clipboard == null || _clipboard.Count == 0 || _store == null) return;
        int page = CurrentPage;
        _pasteCount++;
        var offset = page == _clipboard[0].Page ? new Vector2(12, 12) * _pasteCount : Vector2.Zero;
        PlaceCopies(page, _clipboard, offset);
    }

    public void DuplicateSelection()
    {
        if (_sel == null) return;
        PlaceCopies(_sel.Page, _sel.Strokes, new Vector2(12, 12));
    }

    void PlaceCopies(int page, List<Stroke> source, Vector2 offset)
    {
        var groupMap = new Dictionary<int, int>();
        var m = Matrix3x2.CreateTranslation(offset);
        var copies = source.OrderBy(s => s.Seq).Select(s =>
        {
            int g = 0;
            if (s.Group != 0 && !groupMap.TryGetValue(s.Group, out g)) groupMap[s.Group] = g = Stroke.NewGroup();
            return s.Transformed(m).Clone(page: page, group: g, seq: _store!.NextSeq());
        }).ToList();
        ClearSelection();
        Commit(copies);
        if (Settings.Tool == Tool.Lasso) SetSelection(page, copies);
        Invalidate();
    }

    public void SelectAllOnPage()
    {
        if (_store == null) return;
        int page = CurrentPage;
        SetSelection(page, _store.PageStrokes(page).ToList());
        Invalidate();
    }

    // ---- scrollbar, wheel, cursor --------------------------------------------------------

    void BeginScrollbar(Vector2 pos)
    {
        _g = Gesture.Scrollbar;
        var thumb = ScrollThumb();
        if (pos.Y >= thumb.Y && pos.Y <= thumb.Y + thumb.Height) _scrollGrab = pos.Y - (float)thumb.Y;
        else
        {
            _scrollGrab = (float)thumb.Height / 2;
            MoveScrollbar(pos);
        }
    }

    void MoveScrollbar(Vector2 pos)
    {
        var thumb = ScrollThumb();
        float pad = 4 * Dpi, track = ViewH - 2 * pad;
        float m = MarginPx / _px, total = _docH + 2 * m, vh = ViewH / _px;
        float frac = Math.Clamp((pos.Y - _scrollGrab - pad) / MathF.Max(1, track - (float)thumb.Height), 0, 1);
        _offY = frac * (total - vh) - m;
        Clamp();
        RaiseView();
    }

    void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_engine == null) return;
        e.Handled = true;
        var pt = e.GetCurrentPoint(_canvas);
        var pos = ToPx(pt.Position);
        int delta = pt.Properties.MouseWheelDelta;
        bool shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            ZoomAt(MathF.Pow(1.2f, delta / 120f), pos);
            return;
        }
        if (Settings.RulerVisible && _ruler.HitBody(pos))
        {
            _ruler.Angle += delta / 120f * (shift ? 15 : 1) * MathF.PI / 180;
            Invalidate();
            return;
        }
        float step = delta / 120f * 110 * Dpi / _px;
        if (pt.Properties.IsHorizontalMouseWheel) _offX += step;
        else if (shift) _offX -= step;
        else _offY -= step;
        Clamp();
        Invalidate();
        RaiseView();
    }

    InputCursor? _cursor;
    readonly Dictionary<InputSystemCursorShape, InputCursor> _cursors = new();

    void UpdateCursor(Vector2 pos)
    {
        var shape = InputSystemCursorShape.Cross;
        if (_engine == null) shape = InputSystemCursorShape.Arrow;
        else if (Settings.RulerVisible && _ruler.HitBody(pos)) shape = InputSystemCursorShape.SizeAll;
        else if (_sel != null && HitSelection(pos) != TransformMode.None) shape = InputSystemCursorShape.SizeAll;
        else if (Settings.Tool == Tool.Hand || SpaceDown || _g == Gesture.Pan) shape = InputSystemCursorShape.Hand;
        else if (Settings.Tool == Tool.Lasso) shape = InputSystemCursorShape.Arrow;
        if (!_cursors.TryGetValue(shape, out var c)) _cursors[shape] = c = InputSystemCursor.Create(shape);
        if (c != _cursor)
        {
            _cursor = c;
            ProtectedCursor = c;
        }
    }
}
