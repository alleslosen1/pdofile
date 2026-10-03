using System.Numerics;

namespace InkPDF.Ink;

/// <summary>One-euro filter: removes hand jitter at low speed without adding lag at high speed.</summary>
public sealed class OneEuro2
{
    readonly float _minCutoff, _beta;
    const float DCutoff = 1f;
    Vector2 _x, _dx;
    bool _init;

    public OneEuro2(float minCutoff, float beta)
    {
        _minCutoff = minCutoff;
        _beta = beta;
    }

    static float Alpha(float cutoff, float dt)
    {
        float tau = 1f / (2 * MathF.PI * cutoff);
        return 1f / (1f + tau / dt);
    }

    public Vector2 Filter(Vector2 x, float dt)
    {
        if (!_init)
        {
            _init = true;
            _x = x;
            return x;
        }
        var dx = (x - _x) / dt;
        _dx += Alpha(DCutoff, dt) * (dx - _dx);
        float cutoff = _minCutoff + _beta * _dx.Length();
        _x += Alpha(cutoff, dt) * (x - _x);
        return _x;
    }
}

/// <summary>Turns raw pointer samples into stroke control points (smoothing + pressure).</summary>
public sealed class StrokeBuilder
{
    readonly float _baseR;
    readonly bool _pressure;
    readonly OneEuro2? _filter;
    double _lastT = -1;
    float _p = -1;

    public List<InkPoint> Control { get; } = new();
    public float Length { get; private set; }

    /// <param name="smoothing">0 = off, 1 = light, 2 = medium, 3 = strong.</param>
    public StrokeBuilder(float baseRadius, bool usePressure, int smoothing)
    {
        _baseR = baseRadius;
        _pressure = usePressure;
        _filter = smoothing switch
        {
            1 => new OneEuro2(4f, 0.08f),
            2 => new OneEuro2(2f, 0.05f),
            3 => new OneEuro2(1f, 0.03f),
            _ => null,
        };
    }

    public void Add(Vector2 pos, float pressure, ulong timestampUs)
    {
        double t = timestampUs / 1e6;
        float dt = _lastT < 0 ? 1 / 240f : (float)(t - _lastT);
        if (dt <= 0) dt = 1 / 1000f;
        _lastT = t;

        var q = _filter?.Filter(pos, dt) ?? pos;

        float r = _baseR;
        if (_pressure)
        {
            float p = Math.Clamp(pressure, 0f, 1f);
            _p = _p < 0 ? p : _p + 0.35f * (p - _p);
            // Normal writing pressure (~0.55) gives the nominal width.
            float f = 0.3f + 0.7f * MathF.Pow(_p / 0.55f, 0.7f);
            r = _baseR * Math.Clamp(f, 0.3f, 1.25f);
        }

        if (Control.Count > 0)
        {
            var last = Control[^1];
            float d = Vector2.Distance(last.P, q);
            if (d < MathF.Max(0.12f, 0.2f * r)) return;
            Length += d;
        }
        Control.Add(new InkPoint(q.X, q.Y, r));
    }

    public float AverageRadius
    {
        get
        {
            if (Control.Count == 0) return _baseR;
            float s = 0;
            foreach (var p in Control) s += p.R;
            return s / Control.Count;
        }
    }

    public InkPoint[] Finish() => Control.ToArray();
}
