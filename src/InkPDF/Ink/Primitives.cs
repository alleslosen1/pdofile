using System.Numerics;

namespace InkPDF.Ink;

/// <summary>A sample on a stroke centreline in page points (origin top-left, y down). R is the half-width.</summary>
public struct InkPoint
{
    public float X, Y, R;

    public InkPoint(float x, float y, float r)
    {
        X = x; Y = y; R = r;
    }

    public readonly Vector2 P => new(X, Y);
}

/// <summary>Axis-aligned bounding box.</summary>
public struct Box
{
    public float MinX, MinY, MaxX, MaxY;

    public Box(float minX, float minY, float maxX, float maxY)
    {
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    public static Box Empty => new(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);

    public static Box Around(Vector2 a, Vector2 b) =>
        new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));

    public readonly bool IsEmpty => MaxX < MinX || MaxY < MinY;
    public readonly float Width => MaxX - MinX;
    public readonly float Height => MaxY - MinY;
    public readonly Vector2 Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);

    public void Include(float x, float y, float r = 0)
    {
        MinX = MathF.Min(MinX, x - r);
        MinY = MathF.Min(MinY, y - r);
        MaxX = MathF.Max(MaxX, x + r);
        MaxY = MathF.Max(MaxY, y + r);
    }

    public void Include(in Box b)
    {
        if (b.IsEmpty) return;
        MinX = MathF.Min(MinX, b.MinX);
        MinY = MathF.Min(MinY, b.MinY);
        MaxX = MathF.Max(MaxX, b.MaxX);
        MaxY = MathF.Max(MaxY, b.MaxY);
    }

    public readonly Box Inflate(float d) => IsEmpty ? this : new(MinX - d, MinY - d, MaxX + d, MaxY + d);

    public readonly bool Intersects(in Box o) =>
        MinX <= o.MaxX && o.MinX <= MaxX && MinY <= o.MaxY && o.MinY <= MaxY;

    public readonly bool Contains(Vector2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

    public readonly Box Transform(Matrix3x2 m)
    {
        if (IsEmpty) return this;
        var b = Empty;
        foreach (var c in new[] { new Vector2(MinX, MinY), new Vector2(MaxX, MinY), new Vector2(MaxX, MaxY), new Vector2(MinX, MaxY) })
        {
            var t = Vector2.Transform(c, m);
            b.Include(t.X, t.Y);
        }
        return b;
    }
}
