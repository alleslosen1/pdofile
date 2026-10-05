using System.Numerics;

namespace Pdofile.Ink;

/// <summary>Recognises lines, circles/ellipses, triangles, rectangles and short polylines
/// from a freehand stroke (used by hold-to-straighten).</summary>
public static class ShapeRecognizer
{
    public static ShapeResult? Recognize(IReadOnlyList<InkPoint> input)
    {
        if (input.Count < 3) return null;
        var p = input.Select(q => q.P).ToList();
        float r = input.Average(q => q.R);

        float len = 0;
        for (int i = 1; i < p.Count; i++) len += Vector2.Distance(p[i - 1], p[i]);
        if (len < 10) return null;

        Vector2 a = p[0], b = p[^1];
        float chord = Vector2.Distance(a, b);

        // Straight line.
        float maxDev = 0;
        foreach (var q in p) maxDev = MathF.Max(maxDev, MathF.Sqrt(StrokeMath.DistToSegmentSq(q, a, b)));
        if (chord > 0.85f * len && maxDev < MathF.Max(1.2f, 0.05f * chord))
            return new ShapeResult(ShapeKind.Line, [a, b], r);

        var box = Box.Empty;
        foreach (var q in p) box.Include(q.X, q.Y);
        float diag = MathF.Sqrt(box.Width * box.Width + box.Height * box.Height);
        if (diag < 8) return null;

        bool closed = chord < 0.25f * diag;
        if (closed)
        {
            float rx = box.Width / 2, ry = box.Height / 2;
            var c = box.Center;
            float ellipseErr = float.MaxValue;
            if (rx > 2 && ry > 2)
            {
                float scale = MathF.Sqrt((rx * rx + ry * ry) / 2);
                float sum = 0;
                foreach (var q in p)
                {
                    float dx = (q.X - c.X) / rx, dy = (q.Y - c.Y) / ry;
                    sum += MathF.Abs(MathF.Sqrt(dx * dx + dy * dy) - 1) * scale;
                }
                ellipseErr = sum / p.Count;
            }

            var poly = ClosedPolygon(p, 0.07f * diag);
            float polyErr = poly.Count >= 3 ? MeanDistance(p, poly, true) : float.MaxValue;

            float limit = 0.045f * diag;
            if (poly.Count is >= 3 and <= 4 && polyErr < limit && polyErr <= ellipseErr * 1.1f)
            {
                if (poly.Count == 4 && IsAxisAligned(poly))
                    return new ShapeResult(ShapeKind.Polygon,
                        [new(box.MinX, box.MinY), new(box.MaxX, box.MinY), new(box.MaxX, box.MaxY), new(box.MinX, box.MaxY)], r);
                return new ShapeResult(ShapeKind.Polygon, poly.ToArray(), r);
            }
            if (ellipseErr < limit)
            {
                if (MathF.Abs(rx - ry) < 0.12f * MathF.Max(rx, ry))
                    rx = ry = (rx + ry) / 2;
                return new ShapeResult(ShapeKind.Ellipse, [c, new(rx, ry)], r);
            }
            if (poly.Count is >= 5 and <= 6 && polyErr < limit)
                return new ShapeResult(ShapeKind.Polygon, poly.ToArray(), r);
            return null;
        }

        // Open polyline with a few corners (angles, zig-zags).
        var open = Rdp(p, 0.05f * len);
        if (open.Count is >= 3 and <= 5 && MeanDistance(p, open, false) < 0.03f * len)
            return new ShapeResult(ShapeKind.Polyline, open.ToArray(), r);
        return null;
    }

    static bool IsAxisAligned(List<Vector2> poly)
    {
        for (int i = 0; i < poly.Count; i++)
        {
            var d = poly[(i + 1) % poly.Count] - poly[i];
            float ang = MathF.Abs(MathF.Atan2(d.Y, d.X)) % (MathF.PI / 2);
            float off = MathF.Min(ang, MathF.PI / 2 - ang);
            if (off > 10 * MathF.PI / 180) return false;
        }
        return true;
    }

    static float MeanDistance(List<Vector2> pts, List<Vector2> poly, bool closed)
    {
        float sum = 0;
        int segs = closed ? poly.Count : poly.Count - 1;
        foreach (var q in pts)
        {
            float best = float.MaxValue;
            for (int i = 0; i < segs; i++)
                best = MathF.Min(best, StrokeMath.DistToSegmentSq(q, poly[i], poly[(i + 1) % poly.Count]));
            sum += MathF.Sqrt(best);
        }
        return sum / pts.Count;
    }

    static List<Vector2> ClosedPolygon(List<Vector2> p, float eps)
    {
        // Split at the point farthest from the start, simplify both halves, then merge.
        int far = 0;
        float best = -1;
        for (int i = 0; i < p.Count; i++)
        {
            float d = Vector2.DistanceSquared(p[0], p[i]);
            if (d > best) { best = d; far = i; }
        }
        if (far == 0) return [];
        var first = Rdp(p.GetRange(0, far + 1), eps);
        var second = p.GetRange(far, p.Count - far);
        second.Add(p[0]);
        var secondS = Rdp(second, eps);
        var poly = new List<Vector2>(first);
        poly.AddRange(secondS.Skip(1).Take(secondS.Count - 2));

        // Drop vertices that are nearly collinear or too close together.
        bool changed = true;
        while (changed && poly.Count > 3)
        {
            changed = false;
            for (int i = 0; i < poly.Count; i++)
            {
                var prev = poly[(i - 1 + poly.Count) % poly.Count];
                var cur = poly[i];
                var next = poly[(i + 1) % poly.Count];
                var d1 = cur - prev;
                var d2 = next - cur;
                float l1 = d1.Length(), l2 = d2.Length();
                float turn = l1 < 1e-3f || l2 < 1e-3f ? 0 : MathF.Acos(Math.Clamp(Vector2.Dot(d1, d2) / (l1 * l2), -1f, 1f));
                if (turn < 25 * MathF.PI / 180 || l1 < eps * 0.8f)
                {
                    poly.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
        }
        return poly;
    }

    static List<Vector2> Rdp(List<Vector2> p, float eps)
    {
        var keep = new bool[p.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, p.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            float maxD = 0;
            int idx = -1;
            for (int i = s + 1; i < e; i++)
            {
                float d = StrokeMath.DistToSegmentSq(p[i], p[s], p[e]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (idx >= 0 && MathF.Sqrt(maxD) > eps)
            {
                keep[idx] = true;
                stack.Push((s, idx));
                stack.Push((idx, e));
            }
        }
        var res = new List<Vector2>();
        for (int i = 0; i < p.Count; i++) if (keep[i]) res.Add(p[i]);
        return res;
    }
}
