namespace InkPDF.Ink;

/// <summary>
/// Compact binary form of a stroke (delta + zigzag varints, 1/64 pt resolution).
/// Stored base64-encoded in each saved annotation so ink reloads with full fidelity,
/// and used for the crash-recovery journal.
/// </summary>
public static class InkCodec
{
    const float Q = 64f;
    const byte Version = 1;

    public static void Write(List<byte> o, Stroke s)
    {
        o.Add(Version);
        o.Add((byte)s.Kind);
        o.Add((byte)(s.Smooth ? 1 : 0));
        WriteVar(o, s.Color);
        WriteVar(o, (uint)s.Group);
        WriteVar(o, (uint)s.Points.Length);
        int px = 0, py = 0, pr = 0;
        foreach (var p in s.Points)
        {
            int x = (int)MathF.Round(p.X * Q), y = (int)MathF.Round(p.Y * Q), r = (int)MathF.Round(p.R * Q);
            WriteZig(o, x - px);
            WriteZig(o, y - py);
            WriteZig(o, r - pr);
            px = x; py = y; pr = r;
        }
    }

    public static Stroke Read(ReadOnlySpan<byte> d, ref int pos, int page)
    {
        if (pos >= d.Length || d[pos++] != Version) throw new FormatException("Unknown ink version.");
        var kind = (StrokeKind)d[pos++];
        bool smooth = d[pos++] != 0;
        uint color = ReadVar(d, ref pos);
        int group = (int)ReadVar(d, ref pos);
        int count = (int)ReadVar(d, ref pos);
        if (count <= 0 || count > 1_000_000) throw new FormatException("Bad point count.");
        var pts = new InkPoint[count];
        int px = 0, py = 0, pr = 0;
        for (int i = 0; i < count; i++)
        {
            px += ReadZig(d, ref pos);
            py += ReadZig(d, ref pos);
            pr += ReadZig(d, ref pos);
            pts[i] = new InkPoint(px / Q, py / Q, MathF.Max(0.05f, pr / Q));
        }
        return new Stroke { Page = page, Points = pts, Kind = kind, Smooth = smooth, Color = color & 0xFFFFFF, Group = group };
    }

    public static string ToBase64(Stroke s)
    {
        var o = new List<byte>(s.Points.Length * 5 + 16);
        Write(o, s);
        return Convert.ToBase64String(o.ToArray());
    }

    public static Stroke? FromBase64(string str, int page)
    {
        try
        {
            var bytes = Convert.FromBase64String(str);
            int pos = 0;
            return Read(bytes, ref pos, page);
        }
        catch
        {
            return null;
        }
    }

    public static void WriteVar(List<byte> o, uint v)
    {
        while (v >= 0x80)
        {
            o.Add((byte)(v | 0x80));
            v >>= 7;
        }
        o.Add((byte)v);
    }

    public static uint ReadVar(ReadOnlySpan<byte> d, ref int pos)
    {
        uint v = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            if (pos >= d.Length) throw new FormatException("Truncated ink data.");
            byte b = d[pos++];
            v |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
        }
        throw new FormatException("Bad varint.");
    }

    static void WriteZig(List<byte> o, int v) => WriteVar(o, (uint)((v << 1) ^ (v >> 31)));

    static int ReadZig(ReadOnlySpan<byte> d, ref int pos)
    {
        uint u = ReadVar(d, ref pos);
        return (int)(u >> 1) ^ -(int)(u & 1);
    }
}
