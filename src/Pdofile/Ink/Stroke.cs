using System.Numerics;

namespace Pdofile.Ink;

public enum StrokeKind : byte { Pen = 0, Highlighter = 1 }

/// <summary>
/// An immutable ink stroke on one page. Edits (erase, move, recolour) replace strokes
/// with new instances, so cached geometry never goes stale.
/// </summary>
public sealed class Stroke
{
    static int s_nextGroup;
    public static int NewGroup() => Interlocked.Increment(ref s_nextGroup);

    public required int Page { get; init; }
    /// <summary>Control points. Smooth strokes interpolate them with a centripetal Catmull-Rom spline.</summary>
    public required InkPoint[] Points { get; init; }
    public StrokeKind Kind { get; init; }
    /// <summary>0xRRGGBB.</summary>
    public uint Color { get; init; }
    public bool Smooth { get; init; } = true;
    /// <summary>Strokes sharing a non-zero group (e.g. the parts of an arrow) are selected together.</summary>
    public int Group { get; init; }
    /// <summary>Z-order. Pieces produced by erasing keep their parent's value.</summary>
    public long Seq { get; set; }

    InkPoint[]? _render, _hit;
    Box _bounds = Box.Empty;
    float _maxR = -1;

    /// <summary>Points the renderer draws capsules between.</summary>
    public InkPoint[] RenderPoints => _render ??= StrokeMath.RenderPoints(Points, Smooth);

    /// <summary>Densely spaced points used for erasing and hit-testing.</summary>
    public InkPoint[] HitPoints => _hit ??= StrokeMath.Subdivide(RenderPoints);

    public Box Bounds
    {
        get
        {
            if (_bounds.IsEmpty)
            {
                var b = Box.Empty;
                foreach (var p in RenderPoints) b.Include(p.X, p.Y, p.R);
                _bounds = b;
            }
            return _bounds;
        }
    }

    public float MaxR
    {
        get
        {
            if (_maxR < 0)
            {
                float m = 0;
                foreach (var p in Points) m = MathF.Max(m, p.R);
                _maxR = m;
            }
            return _maxR;
        }
    }

    // Owned by the view (UI thread only).
    internal int Stamp;
    internal bool Selected;

    public Stroke Clone(InkPoint[]? points = null, int? page = null, uint? color = null, int? group = null, long? seq = null) => new()
    {
        Page = page ?? Page,
        Points = points ?? Points,
        Kind = Kind,
        Color = color ?? Color,
        Smooth = Smooth,
        Group = group ?? Group,
        Seq = seq ?? Seq,
    };

    public Stroke Transformed(Matrix3x2 m)
    {
        float s = MathF.Sqrt(MathF.Abs(m.GetDeterminant()));
        var pts = new InkPoint[Points.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            var q = Vector2.Transform(Points[i].P, m);
            pts[i] = new InkPoint(q.X, q.Y, Points[i].R * s);
        }
        return Clone(pts);
    }
}
