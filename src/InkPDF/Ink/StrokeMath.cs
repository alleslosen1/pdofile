using System.Numerics;

namespace InkPDF.Ink;

public static class StrokeMath
{
    /// <summary>
    /// Cubic Bezier for segment i (P[i] to P[i+1]) of a centripetal Catmull-Rom spline.
    /// End segments reflect the missing neighbour.
    /// </summary>
    public static void Bezier(ReadOnlySpan<InkPoint> p, int i, out Vector2 b0, out Vector2 b1, out Vector2 b2, out Vector2 b3)
    {
        int n = p.Length;
        Vector2 p1 = p[i].P, p2 = p[i + 1].P;
        Vector2 p0 = i > 0 ? p[i - 1].P : p1 + (p1 - p2);
        Vector2 p3 = i + 2 < n ? p[i + 2].P : p2 + (p2 - p1);
        b0 = p1;
        b3 = p2;
        float d12 = MathF.Sqrt(Vector2.Distance(p1, p2));
        if (d12 < 1e-4f)
        {
            b1 = p1; b2 = p2;
            return;
        }
        float d01 = MathF.Sqrt(Vector2.Distance(p0, p1));
        float d23 = MathF.Sqrt(Vector2.Distance(p2, p3));
        if (d01 < 1e-4f) d01 = d12;
        if (d23 < 1e-4f) d23 = d12;
        Vector2 m1 = ((p1 - p0) / d01 - (p2 - p0) / (d01 + d12) + (p2 - p1) / d12) * d12;
        Vector2 m2 = ((p2 - p1) / d12 - (p3 - p1) / (d12 + d23) + (p3 - p2) / d23) * d12;
        b1 = p1 + m1 / 3f;
        b2 = p2 - m2 / 3f;
    }

    static Vector2 Eval(Vector2 b0, Vector2 b1, Vector2 b2, Vector2 b3, float t)
    {
        float u = 1 - t;
        return u * u * u * b0 + 3 * u * u * t * b1 + 3 * u * t * t * b2 + t * t * t * b3;
    }

    /// <summary>Appends samples for segment i, excluding its start point.</summary>
    public static void AppendSegment(List<InkPoint> output, ReadOnlySpan<InkPoint> p, int i, bool smooth)
    {
        var a = p[i];
        var b = p[i + 1];
        if (!smooth)
        {
            output.Add(b);
            return;
        }
        Bezier(p, i, out var b0, out var b1, out var b2, out var b3);
        float len = (b1 - b0).Length() + (b2 - b1).Length() + (b3 - b2).Length();
        int n = Math.Clamp((int)MathF.Ceiling(len / 1.0f), 1, 32);
        for (int k = 1; k <= n; k++)
        {
            float t = k / (float)n;
            var q = Eval(b0, b1, b2, b3, t);
            output.Add(new InkPoint(q.X, q.Y, a.R + (b.R - a.R) * t));
        }
    }

    public static InkPoint[] RenderPoints(InkPoint[] p, bool smooth)
    {
        if (p.Length <= 2 || !smooth) return p;
        var list = new List<InkPoint>(p.Length * 2) { p[0] };
        for (int i = 0; i < p.Length - 1; i++) AppendSegment(list, p, i, true);
        return list.ToArray();
    }

    /// <summary>Linear subdivision so neighbouring samples' discs always overlap.</summary>
    public static InkPoint[] Subdivide(InkPoint[] p)
    {
        if (p.Length < 2) return p;
        var list = new List<InkPoint>(p.Length * 2) { p[0] };
        for (int i = 0; i < p.Length - 1; i++)
        {
            var a = p[i];
            var b = p[i + 1];
            float len = Vector2.Distance(a.P, b.P);
            float step = MathF.Max(0.15f, 0.5f * MathF.Min(a.R, b.R));
            int n = Math.Clamp((int)MathF.Ceiling(len / step), 1, 4096);
            for (int k = 1; k <= n; k++)
            {
                float t = k / (float)n;
                list.Add(new InkPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.R + (b.R - a.R) * t));
            }
        }
        return list.ToArray();
    }

    /// <summary>Ramer-Douglas-Peucker on position and radius.</summary>
    public static InkPoint[] Simplify(ReadOnlySpan<InkPoint> p, float eps)
    {
        int n = p.Length;
        if (n <= 2) return p.ToArray();
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            if (e <= s + 1) continue;
            Vector2 a = p[s].P, ab = p[e].P - a;
            float len2 = ab.LengthSquared();
            float maxD = -1;
            int idx = -1;
            for (int i = s + 1; i < e; i++)
            {
                Vector2 ap = p[i].P - a;
                float t = len2 > 1e-12f ? Math.Clamp(Vector2.Dot(ap, ab) / len2, 0f, 1f) : 0f;
                float d = (ap - ab * t).Length();
                float rd = MathF.Abs(p[i].R - (p[s].R + (p[e].R - p[s].R) * t));
                float m = MathF.Max(d, rd);
                if (m > maxD) { maxD = m; idx = i; }
            }
            if (maxD > eps)
            {
                keep[idx] = true;
                stack.Push((s, idx));
                stack.Push((idx, e));
            }
        }
        var result = new List<InkPoint>();
        for (int i = 0; i < n; i++) if (keep[i]) result.Add(p[i]);
        return result.ToArray();
    }

    public static float DistToSegmentSq(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 > 1e-12f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return Vector2.DistanceSquared(p, a + ab * t);
    }

    public static bool PointInPolygon(Vector2 p, IReadOnlyList<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
