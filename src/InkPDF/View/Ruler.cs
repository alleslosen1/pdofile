using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Foundation;

namespace InkPDF.View;

/// <summary>On-screen ruler (screen pixels). Pen strokes started along its edges are drawn straight.</summary>
sealed class Ruler
{
    public Vector2 Center;
    public float Angle;
    public bool Placed;
    public float Length = 900;
    public float Thickness = 72;

    Vector2 Dir => new(MathF.Cos(Angle), MathF.Sin(Angle));
    Vector2 Nrm => new(-MathF.Sin(Angle), MathF.Cos(Angle));

    public void Place(Vector2 center, float dpi)
    {
        Center = center;
        Length = 900 * dpi;
        Thickness = 72 * dpi;
        Placed = true;
    }

    Vector2 ToLocal(Vector2 p)
    {
        var d = p - Center;
        return new Vector2(Vector2.Dot(d, Dir), Vector2.Dot(d, Nrm));
    }

    Vector2 FromLocal(float x, float y) => Center + Dir * x + Nrm * y;

    public bool HitBody(Vector2 p)
    {
        var l = ToLocal(p);
        return MathF.Abs(l.X) <= Length / 2 && MathF.Abs(l.Y) <= Thickness / 2;
    }

    Vector2 KnobLocal => new(Length / 2 - Thickness * 0.55f, 0);

    public bool HitKnob(Vector2 p) => Vector2.Distance(ToLocal(p), KnobLocal) <= Thickness * 0.38f;

    /// <summary>-1 / +1 when p is just outside the top / bottom edge, else 0.</summary>
    public int EdgeSide(Vector2 p, float snap)
    {
        var l = ToLocal(p);
        if (MathF.Abs(l.X) > Length / 2) return 0;
        if (l.Y < -Thickness / 2 && l.Y > -Thickness / 2 - snap) return -1;
        if (l.Y > Thickness / 2 && l.Y < Thickness / 2 + snap) return 1;
        return 0;
    }

    public Vector2 ProjectToEdge(Vector2 p, int side, float offset)
    {
        var l = ToLocal(p);
        return FromLocal(Math.Clamp(l.X, -Length / 2, Length / 2), side * (Thickness / 2 + offset));
    }

    public float AngleDegrees
    {
        get
        {
            float d = Angle * 180 / MathF.PI % 180;
            return d < 0 ? d + 180 : d;
        }
    }

    CanvasTextFormat? _small, _big;

    public void Draw(CanvasDrawingSession ds, float pxPerPt, float dpi)
    {
        _small ??= new CanvasTextFormat { FontSize = 10 * dpi, HorizontalAlignment = CanvasHorizontalAlignment.Center };
        _big ??= new CanvasTextFormat { FontSize = 14 * dpi, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
        var old = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(Angle) * Matrix3x2.CreateTranslation(Center) * old;
        float L = Length, T = Thickness;
        var body = new Rect(-L / 2, -T / 2, L, T);
        ds.FillRoundedRectangle(body, 4 * dpi, 4 * dpi, ColorHelper.FromArgb(200, 245, 247, 250));
        ds.DrawRoundedRectangle(body, 4 * dpi, 4 * dpi, ColorHelper.FromArgb(255, 120, 130, 145), dpi);

        // Millimetre ticks at the page's scale, along both long edges.
        float mm = pxPerPt * 72f / 25.4f;
        var tickColor = ColorHelper.FromArgb(255, 60, 66, 76);
        if (mm >= 0.5f)
        {
            int n = (int)(L / mm);
            for (int k = 0; k <= n; k++)
            {
                bool cm = k % 10 == 0, half = k % 5 == 0;
                if (!cm && mm < 3) continue;
                if (!cm && !half && mm < 5) continue;
                float x = -L / 2 + k * mm;
                float h = cm ? T * 0.28f : half ? T * 0.2f : T * 0.12f;
                ds.DrawLine(x, -T / 2, x, -T / 2 + h, tickColor, dpi);
                ds.DrawLine(x, T / 2, x, T / 2 - h, tickColor, dpi);
                if (cm && k > 0 && x < L / 2 - T) ds.DrawText((k / 10).ToString(), x, -T / 2 + T * 0.3f, tickColor, _small);
            }
        }

        var knob = KnobLocal;
        ds.FillCircle(knob, T * 0.3f, ColorHelper.FromArgb(255, 225, 230, 238));
        ds.DrawCircle(knob, T * 0.3f, ColorHelper.FromArgb(255, 120, 130, 145), dpi);
        ds.DrawText("⟳", new Rect(knob.X - T * 0.3f, knob.Y - T * 0.3f, T * 0.6f, T * 0.6f), tickColor, _big);

        ds.DrawText($"{AngleDegrees:0}°", new Rect(-60 * dpi, -T * 0.2f, 120 * dpi, T * 0.5f), tickColor, _big);
        ds.Transform = old;
    }
}
