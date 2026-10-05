using Pdofile.Ink;
using Microsoft.Graphics.Canvas;

namespace Pdofile.View;

/// <summary>Draws ink in page points; the drawing session's transform maps to pixels.</summary>
public static class InkGeometry
{
    /// <summary>
    /// Fast path used for all on-screen ink: discs at each sample joined by line segments of the
    /// average width. Ink is always opaque within its layer, so overlaps need no union and these
    /// primitives batch on the GPU, roughly 10x cheaper than filling the capsule-union geometry.
    /// </summary>
    public static void Draw(CanvasDrawingSession ds, ReadOnlySpan<InkPoint> pts, Windows.UI.Color color)
    {
        if (pts.Length == 0) return;
        for (int i = 0; i + 1 < pts.Length; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            ds.DrawLine(a.X, a.Y, b.X, b.Y, color, MathF.Max(0.1f, a.R + b.R));
        }
        foreach (var p in pts) ds.FillCircle(p.X, p.Y, MathF.Max(0.05f, p.R), color);
    }

    public static void Draw(CanvasDrawingSession ds, Stroke s, Windows.UI.Color color) => Draw(ds, s.RenderPoints, color);
}
