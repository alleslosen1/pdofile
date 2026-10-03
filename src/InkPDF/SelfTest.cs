using System.Diagnostics;
using System.Numerics;
using System.Text;
using InkPDF.Ink;
using InkPDF.Pdf;

namespace InkPDF;

// Hidden test mode:  InkPDF.exe --selftest <in.pdf> <out.pdf> <log.txt> [--keep]
// Fills page 1 with dense synthetic handwriting, drags the eraser across it while timing
// every frame, adds shapes and highlights, saves, reopens and checks the ink round-trips.
public sealed partial class MainWindow
{
    internal async Task RunSelfTest(string input, string output, string logPath, bool keepOpen)
    {
        var log = new StringBuilder();
        void L(string s) { log.AppendLine(s); Debug.WriteLine(s); }
        try
        {
            await OpenPath(input);
            if (_store == null || _engine == null) throw new Exception("open failed");
            await WaitFor(() => _store.IsAcknowledged(0), 5000);
            for (int i = 0; i < 5; i++) await DocView.NextFrame();
            L($"open: pages={_engine.PageCount} page0 imported strokes={_store.PageStrokes(0).Count}");

            // Dense handwriting-like strokes over page 1.
            var rnd = new Random(7);
            var strokes = new List<Stroke>();
            var size = _engine.PageSizes[0];
            for (float y = 40; y < size.Y - 40; y += 9)
            for (float x = 40; x < size.X - 40; x += 9)
            {
                var pts = new InkPoint[24];
                float px = x, py = y, ang = (float)(rnd.NextDouble() * Math.PI * 2);
                for (int k = 0; k < pts.Length; k++)
                {
                    ang += (float)(rnd.NextDouble() - 0.5) * 1.2f;
                    px += MathF.Cos(ang) * 0.6f;
                    py += MathF.Sin(ang) * 0.6f;
                    pts[k] = new InkPoint(px, py, 0.5f + 0.35f * MathF.Sin(k * 0.4f));
                }
                strokes.Add(new Stroke { Page = 0, Points = pts, Color = 0x1E5BD8, Seq = _store.NextSeq() });
            }
            var sw = Stopwatch.StartNew();
            DocView.TestCommit(strokes);
            var (maxF, frames) = await DrainInk();
            L($"added {strokes.Count} strokes; fully drawn after {sw.ElapsedMilliseconds} ms over {frames} frames, worst frame {maxF:0.0} ms");

            sw.Restart();
            DocView.ZoomBy(1.25f);
            (maxF, frames) = await DrainInk();
            L($"zoom step: ink redrawn after {sw.ElapsedMilliseconds} ms over {frames} frames, worst frame {maxF:0.0} ms");
            DocView.ZoomBy(1 / 1.25f);
            await DrainInk();

            // Idle frames (nothing dirty) should be cheap: just blits.
            var idle = new List<double>();
            for (int i = 0; i < 10; i++) { await DocView.NextFrame(); idle.Add(DocView.LastDrawMs); }
            L($"idle frame: avg {idle.Average():0.00} ms, max {idle.Max():0.00} ms");

            // Eraser drag across the middle of the page in small steps, like a pointer would.
            var cb = new ChangeBuilder();
            var eraseMs = new List<double>();
            var frameMs = new List<double>();
            float ey = size.Y / 2, r = 6f;
            Vector2 last = new(30, ey);
            for (int i = 1; i <= 120; i++)
            {
                var cur = new Vector2(30 + (size.X - 60) * i / 120f, ey + 20 * MathF.Sin(i * 0.2f));
                var t = Stopwatch.StartNew();
                Eraser.Erase(_store, 0, last, cur, r, false, cb);
                eraseMs.Add(t.Elapsed.TotalMilliseconds);
                await DocView.NextFrame();
                frameMs.Add(DocView.LastDrawMs);
                last = cur;
            }
            var change = cb.Build();
            DocView.TestPushChange(change);
            L($"erase step: avg {eraseMs.Average():0.000} ms, max {eraseMs.Max():0.000} ms");
            L($"erase frame (dirty-tile redraw): avg {frameMs.Average():0.00} ms, p95 {Pct(frameMs, 0.95):0.00} ms, max {frameMs.Max():0.00} ms");
            L($"erase change: removed {change.Removed.Count}, added pieces {change.Added.Count}");

            // Undo / redo of the whole drag.
            int before = _store.PageStrokes(0).Count;
            DocView.Undo();
            int undone = _store.PageStrokes(0).Count;
            DocView.Redo();
            L($"undo/redo: {before} -> {undone} -> {_store.PageStrokes(0).Count} (expect {strokes.Count} after undo)");

            // Shapes + highlighter on page 2.
            int p2 = Math.Min(1, _engine.PageCount - 1);
            var shapes = new List<Stroke>();
            shapes.AddRange(Shapes.Axes(p2, new Box(100, 100, 400, 400), 5, true, false, 0.8f, 0));
            shapes.AddRange(Shapes.Arrow(p2, new(100, 500), new(300, 450), 0.8f, 0xD32F2F));
            shapes.AddRange(Shapes.Ellipse(p2, new(450, 600), 60, 40, 0.8f, 0x2E7D32));
            int g = Stroke.NewGroup();
            DocView.TestCommit(shapes.Select(s => s.Clone(group: g, seq: _store.NextSeq())).ToList());
            DocView.TestCommit([new Stroke
            {
                Page = p2, Kind = StrokeKind.Highlighter, Color = 0xFFF176, Seq = _store.NextSeq(),
                Points = [new(80, 180, 6), new(200, 182, 6), new(330, 179, 6)],
            }]);
            await DocView.NextFrame();

            var expect = new Dictionary<int, int>();
            for (int i = 0; i < _engine.PageCount; i++) expect[i] = _store.PageStrokes(i).Count;
            sw.Restart();
            bool saved = await Save(output);
            L($"save: ok={saved} {sw.ElapsedMilliseconds} ms, size {new FileInfo(output).Length / 1024} KB");

            // Reopen the saved file in a fresh engine and read the ink back.
            using var e2 = await PdfEngine.OpenAsync(output);
            var got = new Dictionary<int, List<Stroke>>();
            var tcs = new TaskCompletionSource();
            e2.InkImported += (page, list) => { lock (got) { got[page] = list; if (got.Count == e2.PageCount) tcs.TrySetResult(); } };
            e2.PreImport(Enumerable.Range(0, e2.PageCount));
            await Task.WhenAny(tcs.Task, Task.Delay(10000));
            bool ok = true;
            foreach (var (page, n) in expect)
            {
                int m = got.TryGetValue(page, out var l) ? l.Count : -1;
                L($"roundtrip page {page + 1}: saved {n}, reloaded {m}");
                ok &= n == m;
            }
            var hl = got.GetValueOrDefault(p2)?.Count(s => s.Kind == StrokeKind.Highlighter) ?? 0;
            var groups = got.GetValueOrDefault(p2)?.Where(s => s.Group != 0).Select(s => s.Group).Distinct().Count() ?? 0;
            L($"page {p2 + 1}: highlighters {hl} (expect 1), shape groups {groups} (expect 1)");
            ok &= hl == 1 && groups == 1;
            L(ok ? "RESULT: PASS" : "RESULT: FAIL");
        }
        catch (Exception ex)
        {
            L("RESULT: ERROR " + ex);
        }
        if (keepOpen && DocView.PageCount > 1)
        {
            DocView.GoToPage(1);
            for (int i = 0; i < 30 && _engine != null; i++) await DocView.NextFrame();
            await Task.Delay(500);
            await DocView.NextFrame();
        }
        await File.WriteAllTextAsync(logPath, log.ToString());
        if (!keepOpen)
        {
            _savedVersion = _store?.Version ?? 0;
            Close();
        }
    }

    /// <summary>Draws frames until no ink tiles are pending; returns the worst frame time and frame count.</summary>
    async Task<(double maxMs, int frames)> DrainInk()
    {
        double max = 0;
        int n = 0;
        do
        {
            await DocView.NextFrame();
            max = Math.Max(max, DocView.LastDrawMs);
            n++;
        } while (DocView.InkPending && n < 2000);
        return (max, n);
    }

    static double Pct(List<double> v, double p)
    {
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, (int)(p * s.Count))];
    }

    static async Task WaitFor(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(20);
    }
}
