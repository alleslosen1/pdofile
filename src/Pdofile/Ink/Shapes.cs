using System.Numerics;

namespace Pdofile.Ink;

public enum ShapeKind { Line, Arrow, Rect, Ellipse, Axes, Polygon, Polyline }

/// <summary>A shape recognised from a freehand stroke. For Ellipse, Points = [centre, radii].</summary>
public sealed record ShapeResult(ShapeKind Kind, Vector2[] Points, float Radius);

/// <summary>Builds shapes as plain (non-smoothed) strokes, so erasing and lasso treat them like ink.</summary>
public static class Shapes
{
    public static Stroke Make(int page, IEnumerable<Vector2> pts, float r, uint color, StrokeKind kind = StrokeKind.Pen) => new()
    {
        Page = page,
        Points = pts.Select(p => new InkPoint(p.X, p.Y, r)).ToArray(),
        Kind = kind,
        Color = color,
        Smooth = false,
    };

    public static List<Stroke> Line(int page, Vector2 a, Vector2 b, float r, uint color, StrokeKind kind = StrokeKind.Pen) =>
        [Make(page, [a, b], r, color, kind)];

    public static List<Stroke> Arrow(int page, Vector2 a, Vector2 b, float r, uint color)
    {
        var list = Line(page, a, b, r, color);
        float len = Vector2.Distance(a, b);
        if (len < 0.5f) return list;
        list.Add(Make(page, ArrowHead(a, b, MathF.Min(len * 0.35f, 5f + 3f * r)), r, color));
        return list;
    }

    static Vector2[] ArrowHead(Vector2 from, Vector2 tip, float size)
    {
        var dir = Vector2.Normalize(tip - from);
        const float ang = 0.45f;
        var l = Rotate(dir, ang) * size;
        var rr = Rotate(dir, -ang) * size;
        return [tip - l, tip, tip - rr];
    }

    static Vector2 Rotate(Vector2 v, float a) => new(v.X * MathF.Cos(a) - v.Y * MathF.Sin(a), v.X * MathF.Sin(a) + v.Y * MathF.Cos(a));

    public static List<Stroke> Rect(int page, Box b, float r, uint color) =>
        [Make(page, [new(b.MinX, b.MinY), new(b.MaxX, b.MinY), new(b.MaxX, b.MaxY), new(b.MinX, b.MaxY), new(b.MinX, b.MinY)], r, color)];

    public static List<Stroke> Ellipse(int page, Vector2 c, float rx, float ry, float r, uint color)
    {
        int n = Math.Clamp((int)((rx + ry) * 0.6f), 32, 160);
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i * 2 * MathF.PI / n;
            pts[i] = new Vector2(c.X + rx * MathF.Cos(t), c.Y + ry * MathF.Sin(t));
        }
        return [Make(page, pts, r, color)];
    }

    public static List<Stroke> Polygon(int page, IReadOnlyList<Vector2> v, bool closed, float r, uint color)
    {
        var pts = new List<Vector2>(v);
        if (closed) pts.Add(v[0]);
        return [Make(page, pts, r, color)];
    }

    public static List<Stroke> Axes(int page, Box box, int units, bool grid, bool firstQuadrant, float r, uint color)
    {
        var list = new List<Stroke>();
        units = Math.Clamp(units, 1, 40);
        float head = 5f + 3f * r;
        Vector2 o = firstQuadrant ? new(box.MinX, box.MaxY) : box.Center;
        float spanX = box.MaxX - o.X - head * 1.5f;
        float spanY = o.Y - box.MinY - head * 1.5f;
        float sp = MathF.Max(1f, MathF.Min(spanX, spanY) / units);
        float tick = 2.5f + r;

        var xs = new List<float>();
        var ys = new List<float>();
        for (int k = 1; k <= units; k++)
        {
            if (o.X + k * sp <= box.MaxX - head) xs.Add(o.X + k * sp);
            if (!firstQuadrant && o.X - k * sp >= box.MinX) xs.Add(o.X - k * sp);
            if (o.Y - k * sp >= box.MinY + head) ys.Add(o.Y - k * sp);
            if (!firstQuadrant && o.Y + k * sp <= box.MaxY) ys.Add(o.Y + k * sp);
        }

        if (grid)
        {
            const uint gridColor = 0xBDBDBD;
            float gr = MathF.Max(0.2f, r * 0.35f);
            float gx0 = firstQuadrant ? o.X : box.MinX, gy1 = firstQuadrant ? o.Y : box.MaxY;
            foreach (var x in xs) list.Add(Make(page, [new(x, box.MinY + head), new(x, gy1)], gr, gridColor));
            foreach (var y in ys) list.Add(Make(page, [new(gx0, y), new(box.MaxX - head, y)], gr, gridColor));
        }

        var xStart = new Vector2(firstQuadrant ? o.X : box.MinX, o.Y);
        var xEnd = new Vector2(box.MaxX, o.Y);
        var yStart = new Vector2(o.X, firstQuadrant ? o.Y : box.MaxY);
        var yEnd = new Vector2(o.X, box.MinY);
        list.Add(Make(page, [xStart, xEnd], r, color));
        list.Add(Make(page, ArrowHead(xStart, xEnd, head), r, color));
        list.Add(Make(page, [yStart, yEnd], r, color));
        list.Add(Make(page, ArrowHead(yStart, yEnd, head), r, color));
        foreach (var x in xs) list.Add(Make(page, [new(x, o.Y - tick), new(x, o.Y + tick)], r, color));
        foreach (var y in ys) list.Add(Make(page, [new(o.X - tick, y), new(o.X + tick, y)], r, color));
        return list;
    }

    /// <summary>Shape for a drag from a to b with the given shape tool.</summary>
    public static List<Stroke> FromDrag(Tool tool, int page, Vector2 a, Vector2 b, bool shift, ToolSettings st, float r, uint color)
    {
        switch (tool)
        {
            case Tool.Line:
            case Tool.Arrow:
            {
                b = SnapAngle(a, b, shift);
                return tool == Tool.Line ? Line(page, a, b, r, color) : Arrow(page, a, b, r, color);
            }
            case Tool.Rect:
                return Rect(page, Box.Around(a, Square(a, b, shift)), r, color);
            case Tool.Ellipse:
            {
                var box = Box.Around(a, Square(a, b, shift));
                return Ellipse(page, box.Center, box.Width / 2, box.Height / 2, r, color);
            }
            case Tool.Axes:
                return Axes(page, Box.Around(a, Square(a, b, shift)), st.AxesUnits, st.AxesGrid, st.AxesFirstQuadrant, r, color);
            default:
                return [];
        }
    }

    public static List<Stroke> FromResult(int page, ShapeResult s, uint color, StrokeKind kind)
    {
        var list = s.Kind switch
        {
            ShapeKind.Line => Line(page, s.Points[0], s.Points[1], s.Radius, color, kind),
            ShapeKind.Ellipse => Ellipse(page, s.Points[0], s.Points[1].X, s.Points[1].Y, s.Radius, color),
            ShapeKind.Polygon => Polygon(page, s.Points, true, s.Radius, color),
            ShapeKind.Polyline => Polygon(page, s.Points, false, s.Radius, color),
            _ => [],
        };
        return kind == StrokeKind.Pen ? list : list.Select(x => new Stroke
        {
            Page = x.Page, Points = x.Points, Kind = kind, Color = x.Color, Smooth = false,
        }).ToList();
    }

    /// <summary>Snaps to 15° steps with Shift, otherwise only when within 3° of a multiple of 45°.</summary>
    public static Vector2 SnapAngle(Vector2 a, Vector2 b, bool shift)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-3f) return b;
        float ang = MathF.Atan2(d.Y, d.X);
        float step = shift ? MathF.PI / 12 : MathF.PI / 4;
        float snapped = MathF.Round(ang / step) * step;
        if (shift || MathF.Abs(snapped - ang) < 3 * MathF.PI / 180)
            return a + new Vector2(MathF.Cos(snapped), MathF.Sin(snapped)) * len;
        return b;
    }

    static Vector2 Square(Vector2 a, Vector2 b, bool shift)
    {
        if (!shift) return b;
        var d = b - a;
        float s = MathF.Max(MathF.Abs(d.X), MathF.Abs(d.Y));
        return a + new Vector2(MathF.CopySign(s, d.X), MathF.CopySign(s, d.Y));
    }
}
