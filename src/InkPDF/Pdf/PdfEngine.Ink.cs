using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using InkPDF.Ink;
using static InkPDF.Pdf.Native;

namespace InkPDF.Pdf;

// Reading existing ink out of the PDF and writing ink back as standard Ink annotations.
public sealed unsafe partial class PdfEngine
{
    /// <summary>Annotation key holding our full-fidelity stroke data (pressure, shape flags).</summary>
    const string DataKey = "InkPDF";

    PageAffine?[] _affine = [];
    bool[] _imported = [];
    List<Stroke>?[] _parsed = [];

    PageAffine Affine(int i, IntPtr page)
    {
        if (_affine[i] is { } a) return a;
        var sz = PageSizes[i];
        int sx = Math.Max(1, (int)MathF.Round(sz.X * 64)), sy = Math.Max(1, (int)MathF.Round(sz.Y * 64));
        FPDF_DeviceToPage(page, 0, 0, sx, sy, 0, 0, 0, out double ox, out double oy);
        FPDF_DeviceToPage(page, 0, 0, sx, sy, 0, sx, 0, out double ax, out double ay);
        FPDF_DeviceToPage(page, 0, 0, sx, sy, 0, 0, sy, out double bx, out double by);
        var o = new Vector2((float)ox, (float)oy);
        var u = (new Vector2((float)ax, (float)ay) - o) / sz.X;
        var v = (new Vector2((float)bx, (float)by) - o) / sz.Y;
        return _affine[i] = new PageAffine(o, u, v);
    }

    /// <summary>
    /// Reads every Ink annotation on the page into strokes and removes them from the in-memory
    /// document, so PDFium renders only the page while our renderer draws the ink.
    /// They are written back on save.
    /// </summary>
    List<Stroke> ImportInk(int i, IntPtr page)
    {
        _imported[i] = true;
        var aff = Affine(i, page);
        var result = new List<Stroke>();
        var remove = new List<int>();
        var groups = new Dictionary<int, int>();
        int n = FPDFPage_GetAnnotCount(page);
        for (int k = 0; k < n; k++)
        {
            IntPtr a = FPDFPage_GetAnnot(page, k);
            if (a == IntPtr.Zero) continue;
            try
            {
                if (FPDFAnnot_GetSubtype(a) != FPDF_ANNOT_INK) continue;
                ParseInk(a, i, aff, result, groups);
                remove.Add(k);
            }
            finally
            {
                FPDFPage_CloseAnnot(a);
            }
        }
        for (int k = remove.Count - 1; k >= 0; k--) FPDFPage_RemoveAnnot(page, remove[k]);
        _parsed[i] = result;
        InkImported?.Invoke(i, result);
        return result;
    }

    void ParseInk(IntPtr a, int page, PageAffine aff, List<Stroke> output, Dictionary<int, int> groups)
    {
        if (FPDFAnnot_HasKey(a, DataKey) != 0 && InkCodec.FromBase64(GetString(a, DataKey), page) is { } own)
        {
            int g = 0;
            if (own.Group != 0 && !groups.TryGetValue(own.Group, out g)) groups[own.Group] = g = Stroke.NewGroup();
            output.Add(own.Clone(group: g));
            return;
        }

        uint paths = FPDFAnnot_GetInkListCount(a);
        if (paths == 0) return;

        // Ink from other apps: colour/width come from /C and /Border, or from the appearance stream.
        bool haveColor = FPDFAnnot_GetColor(a, 0, out uint rr, out uint gg, out uint bb, out uint aa) != 0;
        float width = 1;
        if (FPDFAnnot_GetBorder(a, out _, out _, out float bw) != 0 && bw > 0) width = bw;
        int objs = FPDFAnnot_GetObjectCount(a);
        for (int k = 0; k < objs; k++)
        {
            IntPtr o = FPDFAnnot_GetObject(a, k);
            if (o == IntPtr.Zero || FPDFPageObj_GetType(o) != FPDF_PAGEOBJ_PATH) continue;
            if (!haveColor && FPDFPageObj_GetStrokeColor(o, out rr, out gg, out bb, out aa) != 0) haveColor = true;
            if (FPDFPageObj_GetStrokeWidth(o, out float ow) != 0 && ow > 0) width = ow;
            break;
        }
        if (!haveColor) { rr = gg = bb = 0; aa = 255; }
        float ca = FPDFAnnot_GetNumberValue(a, "CA", out float caV) != 0 ? caV : 1f;
        float alpha = MathF.Min(ca, aa / 255f);
        bool hl = alpha < 0.9f;
        if (hl)
        {
            // Our renderer draws highlighters opaque with a multiply-like blend: pre-mix with white.
            rr = (uint)(255 - (255 - rr) * alpha);
            gg = (uint)(255 - (255 - gg) * alpha);
            bb = (uint)(255 - (255 - bb) * alpha);
        }
        float r = MathF.Max(0.2f, width / 2 / aff.Scale);
        int group = paths > 1 ? Stroke.NewGroup() : 0;

        for (uint p = 0; p < paths; p++)
        {
            uint cnt = FPDFAnnot_GetInkListPath(a, p, null, 0);
            if (cnt == 0) continue;
            var buf = new FS_POINTF[cnt];
            fixed (FS_POINTF* bp = buf) FPDFAnnot_GetInkListPath(a, p, bp, cnt);
            var pts = new InkPoint[cnt];
            for (int j = 0; j < cnt; j++)
            {
                var q = aff.FromPdf(new Vector2(buf[j].X, buf[j].Y));
                pts[j] = new InkPoint(q.X, q.Y, r);
            }
            output.Add(new Stroke
            {
                Page = page,
                Points = StrokeMath.Simplify(pts, 0.05f),
                Kind = hl ? StrokeKind.Highlighter : StrokeKind.Pen,
                Color = (rr << 16) | (gg << 8) | bb,
                Smooth = true,
                Group = group,
            });
        }
    }

    static string GetString(IntPtr a, string key)
    {
        uint len = FPDFAnnot_GetStringValue(a, key, null, 0);
        if (len <= 2) return "";
        var buf = new byte[len];
        fixed (byte* b = buf) FPDFAnnot_GetStringValue(a, key, (ushort*)b, len);
        return Encoding.Unicode.GetString(buf, 0, (int)len - 2);
    }

    /// <summary>
    /// Produces the complete PDF with ink. <paramref name="ui"/> holds the editor's strokes per page;
    /// <paramref name="ack"/> are pages whose imported ink the editor already holds.
    /// </summary>
    public Task<byte[]> BuildPdfAsync(Dictionary<int, List<Stroke>> ui, HashSet<int> ack) => Invoke(() => BuildPdf(ui, ack));

    byte[] BuildPdf(Dictionary<int, List<Stroke>> ui, HashSet<int> ack)
    {
        var pages = new SortedSet<int>(ui.Keys);
        for (int i = 0; i < PageCount; i++) if (_imported[i]) pages.Add(i);
        var added = new List<(int page, int first)>();
        _holdPages = true;
        try
        {
            foreach (int i in pages)
            {
                IntPtr page = GetPage(i);
                if (page == IntPtr.Zero) continue;
                var list = new List<Stroke>();
                if (ui.TryGetValue(i, out var u)) list.AddRange(u);
                // Imported here but not yet delivered to the editor: keep it.
                if (!ack.Contains(i) && _parsed[i] is { } parsed) list.AddRange(parsed);
                if (list.Count == 0) continue;
                list.Sort((x, y) => x.Kind != y.Kind
                    ? (x.Kind == StrokeKind.Highlighter ? -1 : 1)
                    : x.Seq.CompareTo(y.Seq));
                var aff = Affine(i, page);
                int first = FPDFPage_GetAnnotCount(page);
                added.Add((i, first));
                foreach (var s in list) WriteStroke(page, aff, s);
            }
            var ms = new MemoryStream();
            SaveDoc(_doc, ms);
            return ms.ToArray();
        }
        finally
        {
            // Take the annotations out again: in memory, ink lives in the editor, not the PDF.
            foreach (var (i, first) in added)
            {
                IntPtr page = GetPage(i);
                for (int k = FPDFPage_GetAnnotCount(page) - 1; k >= first; k--) FPDFPage_RemoveAnnot(page, k);
            }
            _holdPages = false;
            TrimPages();
        }
    }

    public Task ExportFlattenedAsync(byte[] pdf, string path) => Invoke(() =>
    {
        var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        IntPtr doc = FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)pdf.Length, null);
        try
        {
            if (doc == IntPtr.Zero) throw new IOException("Couldn't re-open the document to flatten it.");
            int n = FPDF_GetPageCount(doc);
            for (int i = 0; i < n; i++)
            {
                IntPtr p = FPDF_LoadPage(doc, i);
                if (p == IntPtr.Zero) continue;
                FPDFPage_Flatten(p, 0);
                FPDF_ClosePage(p);
            }
            var ms = new MemoryStream();
            SaveDoc(doc, ms);
            FileUtil.WriteAtomic(path, ms.ToArray());
        }
        finally
        {
            if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
            pin.Free();
        }
        return 0;
    });

    static void WriteStroke(IntPtr page, PageAffine aff, Stroke s)
    {
        IntPtr annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_INK);
        if (annot == IntPtr.Zero) return;
        try
        {
            var pdf = new FS_POINTF[s.Points.Length];
            for (int i = 0; i < pdf.Length; i++)
            {
                var q = aff.ToPdf(s.Points[i].X, s.Points[i].Y);
                pdf[i] = new FS_POINTF { X = q.X, Y = q.Y };
            }
            fixed (FS_POINTF* pp = pdf) FPDFAnnot_AddInkStroke(annot, pp, (nuint)pdf.Length);

            var bounds = s.Bounds.Inflate(1).Transform(Matrix3x2.Identity);
            var box = Box.Empty;
            foreach (var c in new[] { aff.ToPdf(bounds.MinX, bounds.MinY), aff.ToPdf(bounds.MaxX, bounds.MinY), aff.ToPdf(bounds.MaxX, bounds.MaxY), aff.ToPdf(bounds.MinX, bounds.MaxY) })
                box.Include(c.X, c.Y);
            var rect = new FS_RECTF { Left = box.MinX, Top = box.MaxY, Right = box.MaxX, Bottom = box.MinY };
            FPDFAnnot_SetRect(annot, ref rect);

            uint r = (s.Color >> 16) & 255, g = (s.Color >> 8) & 255, b = s.Color & 255;
            FPDFAnnot_SetColor(annot, 0, r, g, b, 255);   // must precede the appearance stream
            float avgR = 0;
            foreach (var p in s.Points) avgR += p.R;
            avgR /= s.Points.Length;
            FPDFAnnot_SetBorder(annot, 0, 0, 2 * avgR * aff.Scale);
            FPDFAnnot_SetFlags(annot, FPDF_ANNOT_FLAG_PRINT);
            FPDFAnnot_SetStringValue(annot, DataKey, InkCodec.ToBase64(s));

            if (s.Kind != StrokeKind.Highlighter || !AppendHighlighter(annot, s, aff))
                FPDFAnnot_SetAP(annot, 0, BuildAppearance(s, aff));
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>Highlighter as one stroked path with multiply blending, so text underneath stays dark.</summary>
    static bool AppendHighlighter(IntPtr annot, Stroke s, PageAffine aff)
    {
        var P = s.Points;
        var q0 = aff.ToPdf(P[0].X, P[0].Y);
        IntPtr path = FPDFPageObj_CreateNewPath(q0.X, q0.Y);
        if (path == IntPtr.Zero) return false;
        if (P.Length == 1) FPDFPath_LineTo(path, q0.X + 0.01f, q0.Y);
        for (int i = 0; i < P.Length - 1; i++)
        {
            if (s.Smooth)
            {
                StrokeMath.Bezier(P, i, out _, out var b1, out var b2, out var b3);
                var c1 = aff.ToPdf(b1.X, b1.Y);
                var c2 = aff.ToPdf(b2.X, b2.Y);
                var c3 = aff.ToPdf(b3.X, b3.Y);
                FPDFPath_BezierTo(path, c1.X, c1.Y, c2.X, c2.Y, c3.X, c3.Y);
            }
            else
            {
                var c = aff.ToPdf(P[i + 1].X, P[i + 1].Y);
                FPDFPath_LineTo(path, c.X, c.Y);
            }
        }
        FPDFPath_SetDrawMode(path, 0, 1);
        FPDFPageObj_SetStrokeColor(path, (s.Color >> 16) & 255, (s.Color >> 8) & 255, s.Color & 255, 255);
        FPDFPageObj_SetStrokeWidth(path, 2 * s.MaxR * aff.Scale);
        FPDFPageObj_SetLineCap(path, 1);
        FPDFPageObj_SetLineJoin(path, 1);
        FPDFPageObj_SetBlendMode(path, "Multiply");
        if (FPDFAnnot_AppendObject(annot, path) != 0) return true;
        FPDFPageObj_Destroy(path);
        return false;
    }

    /// <summary>
    /// Pen appearance: the spline as Bezier segments, each stroked with its own width
    /// (round caps/joins), which reproduces the pressure-varying look in any viewer.
    /// </summary>
    static string BuildAppearance(Stroke s, PageAffine aff)
    {
        var ci = CultureInfo.InvariantCulture;
        string F(float v) => v.ToString("0.###", ci);
        var sb = new StringBuilder(s.Points.Length * 48);
        sb.Append("q 1 J 1 j ")
          .Append(F(((s.Color >> 16) & 255) / 255f)).Append(' ')
          .Append(F(((s.Color >> 8) & 255) / 255f)).Append(' ')
          .Append(F((s.Color & 255) / 255f)).Append(" RG\n");
        float sc = aff.Scale;
        var P = s.Points;
        if (P.Length == 1)
        {
            var q = aff.ToPdf(P[0].X, P[0].Y);
            sb.Append(F(2 * P[0].R * sc)).Append(" w ").Append(F(q.X)).Append(' ').Append(F(q.Y)).Append(" m ")
              .Append(F(q.X)).Append(' ').Append(F(q.Y)).Append(" l S\n");
        }
        else
        {
            float curW = -1;
            bool open = false;
            for (int i = 0; i < P.Length - 1; i++)
            {
                float w = (P[i].R + P[i + 1].R) * sc;
                if (!open || MathF.Abs(w - curW) > 0.03f * curW + 0.01f)
                {
                    if (open) sb.Append("S\n");
                    curW = w;
                    var st = aff.ToPdf(P[i].X, P[i].Y);
                    sb.Append(F(w)).Append(" w ").Append(F(st.X)).Append(' ').Append(F(st.Y)).Append(" m ");
                    open = true;
                }
                if (s.Smooth)
                {
                    StrokeMath.Bezier(P, i, out _, out var b1, out var b2, out var b3);
                    var c1 = aff.ToPdf(b1.X, b1.Y);
                    var c2 = aff.ToPdf(b2.X, b2.Y);
                    var c3 = aff.ToPdf(b3.X, b3.Y);
                    sb.Append(F(c1.X)).Append(' ').Append(F(c1.Y)).Append(' ')
                      .Append(F(c2.X)).Append(' ').Append(F(c2.Y)).Append(' ')
                      .Append(F(c3.X)).Append(' ').Append(F(c3.Y)).Append(" c ");
                }
                else
                {
                    var c = aff.ToPdf(P[i + 1].X, P[i + 1].Y);
                    sb.Append(F(c.X)).Append(' ').Append(F(c.Y)).Append(" l ");
                }
            }
            sb.Append("S\n");
        }
        sb.Append('Q');
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileWrite
    {
        public int Version;
        public IntPtr WriteBlock;
        public IntPtr Handle;   // ours: GCHandle to the target stream
    }

    [UnmanagedCallersOnly]
    static int WriteBlock(FileWrite* self, byte* data, uint size)
    {
        try
        {
            var s = (Stream)GCHandle.FromIntPtr(self->Handle).Target!;
            s.Write(new ReadOnlySpan<byte>(data, (int)size));
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    static void SaveDoc(IntPtr doc, Stream s)
    {
        var h = GCHandle.Alloc(s);
        try
        {
            var fw = new FileWrite
            {
                Version = 1,
                WriteBlock = (IntPtr)(delegate* unmanaged<FileWrite*, byte*, uint, int>)&WriteBlock,
                Handle = GCHandle.ToIntPtr(h),
            };
            if (FPDF_SaveAsCopy(doc, &fw, FPDF_NO_INCREMENTAL) == 0)
                throw new IOException("PDFium failed to write the document.");
        }
        finally
        {
            h.Free();
        }
    }
}

public static class FileUtil
{
    /// <summary>Writes to a temp file next to the target, then swaps it in.</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        string tmp = path + ".inkpdf-tmp";
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
