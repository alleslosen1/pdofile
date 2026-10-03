using System.Numerics;
using InkPDF.Ink;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;

namespace InkPDF.View;

/// <summary>
/// Stroke outline = union of tapered capsules between consecutive points (round joins and
/// caps, exact variable width). Built in page points, so it stays sharp at any zoom.
/// </summary>
public static class InkGeometry
{
    public static CanvasGeometry Get(ICanvasResourceCreator rc, Stroke s)
    {
        if (s.RenderCache is CanvasGeometry g) return g;
        g = Build(rc, s.RenderPoints);
        s.RenderCache = g;
        return g;
    }

    public static CanvasGeometry Build(ICanvasResourceCreator rc, ReadOnlySpan<InkPoint> pts)
    {
        using var pb = new CanvasPathBuilder(rc);
        pb.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        if (pts.Length == 1) AddCircle(pb, pts[0].P, MathF.Max(pts[0].R, 0.05f));
        for (int i = 0; i + 1 < pts.Length; i++) AddCapsule(pb, pts[i], pts[i + 1]);
        return CanvasGeometry.CreatePath(pb);
    }

    // Every figure is wound counter-clockwise on screen so the nonzero fill is their union.
    static void AddCircle(CanvasPathBuilder pb, Vector2 c, float r)
    {
        pb.BeginFigure(c.X + r, c.Y);
        pb.AddArc(new Vector2(c.X - r, c.Y), r, r, 0, CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
        pb.AddArc(new Vector2(c.X + r, c.Y), r, r, 0, CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
        pb.EndFigure(CanvasFigureLoop.Closed);
    }

    static void AddCapsule(CanvasPathBuilder pb, InkPoint a, InkPoint b)
    {
        float ra = MathF.Max(a.R, 0.05f), rb = MathF.Max(b.R, 0.05f);
        Vector2 A = a.P, B = b.P, d = B - A;
        float len = d.Length();
        if (len <= MathF.Abs(ra - rb) + 1e-4f)
        {
            if (ra >= rb) AddCircle(pb, A, ra);
            else AddCircle(pb, B, rb);
            return;
        }
        float alpha = MathF.Atan2(d.Y, d.X);
        float phi = MathF.Acos(Math.Clamp((ra - rb) / len, -1f, 1f));
        Vector2 E(float t) => new(MathF.Cos(t), MathF.Sin(t));
        var a1 = A + ra * E(alpha + phi);
        var b1 = B + rb * E(alpha + phi);
        var b2 = B + rb * E(alpha - phi);
        var a2 = A + ra * E(alpha - phi);
        pb.BeginFigure(a1);
        pb.AddLine(b1);
        pb.AddArc(b2, rb, rb, 0, CanvasSweepDirection.CounterClockwise, phi > MathF.PI / 2 ? CanvasArcSize.Large : CanvasArcSize.Small);
        pb.AddLine(a2);
        pb.AddArc(a1, ra, ra, 0, CanvasSweepDirection.CounterClockwise, phi < MathF.PI / 2 ? CanvasArcSize.Large : CanvasArcSize.Small);
        pb.EndFigure(CanvasFigureLoop.Closed);
    }
}
