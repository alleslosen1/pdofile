using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Pdofile.Ink;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using static Pdofile.Pdf.Native;

namespace Pdofile.Pdf;

public readonly record struct TileKey(int Page, int Level, int X, int Y)
{
    /// <summary>Level value for the small whole-page preview used while tiles load.</summary>
    public const int Preview = -1;
}

public sealed class PdfOpenException(string message) : Exception(message);

/// <summary>Maps page-local points (top-left origin, y down, as displayed) to PDF user space.</summary>
public sealed class PageAffine(Vector2 o, Vector2 u, Vector2 v)
{
    public Vector2 ToPdf(float x, float y) => o + x * u + y * v;

    public Vector2 FromPdf(Vector2 p)
    {
        var d = p - o;
        float det = u.X * v.Y - u.Y * v.X;
        return new Vector2((d.X * v.Y - d.Y * v.X) / det, (u.X * d.Y - u.Y * d.X) / det);
    }

    public float Scale => MathF.Sqrt(MathF.Abs(u.X * v.Y - u.Y * v.X));
}

/// <summary>
/// Owns a PDFium document. PDFium is not thread-safe, so every call runs on one dedicated
/// worker thread: jobs (open, import, save) first, then page tiles the view currently wants.
/// </summary>
public sealed partial class PdfEngine : IDisposable
{
    public const int TileSize = 512;
    const int MaxOpenPages = 12;

    static readonly object s_initLock = new();
    static bool s_initialized;

    readonly object _lock = new();
    readonly Queue<Action> _jobs = new();
    List<TileKey> _wanted = new();
    readonly Thread _thread;
    bool _disposed;

    IntPtr _doc;
    GCHandle _pin;
    readonly Dictionary<int, IntPtr> _open = new();
    readonly LinkedList<int> _lru = new();
    bool _holdPages;

    public string FilePath { get; }
    public int PageCount { get; private set; }
    public Vector2[] PageSizes { get; private set; } = [];

    /// <summary>Rendered tiles. Written by the worker, read and evicted by the UI thread.</summary>
    public ConcurrentDictionary<TileKey, CanvasBitmap> Tiles { get; } = new();

    public CanvasDevice? Device { get; set; }

    /// <summary>DPI given to tile bitmaps; must match the canvas so tiles blit 1:1.</summary>
    public float Dpi { get; set; } = 96;

    public void ClearTiles()
    {
        foreach (var k in Tiles.Keys)
            if (Tiles.TryRemove(k, out var b)) b.Dispose();
    }

    /// <summary>Raised on the worker thread when a page's existing ink has been read (and removed from the in-memory PDF).</summary>
    public event Action<int, List<Stroke>>? InkImported;

    /// <summary>Raised on the worker thread whenever a tile finishes.</summary>
    public event Action? TileReady;

    PdfEngine(string path)
    {
        FilePath = path;
        _thread = new Thread(Run) { IsBackground = true, Name = "PDFium" };
        _thread.Start();
    }

    public static async Task<PdfEngine> OpenAsync(string path)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        var e = new PdfEngine(path);
        try
        {
            await e.Invoke(() => { e.Load(bytes); return 0; });
            return e;
        }
        catch
        {
            e.Dispose();
            throw;
        }
    }

    void Load(byte[] bytes)
    {
        lock (s_initLock)
        {
            if (!s_initialized)
            {
                FPDF_InitLibrary();
                s_initialized = true;
            }
        }
        // The whole file is kept in memory so the original can be overwritten on save.
        _pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        _doc = FPDF_LoadMemDocument64(_pin.AddrOfPinnedObject(), (nuint)bytes.Length, null);
        if (_doc == IntPtr.Zero)
        {
            uint err = FPDF_GetLastError();
            throw new PdfOpenException(err switch
            {
                4 => "This PDF is password-protected. Password-protected files aren't supported.",
                3 => "The file isn't a valid PDF, or it is damaged.",
                2 => "The file couldn't be read.",
                6 => "This PDF uses an unsupported security handler.",
                _ => $"The PDF couldn't be opened (PDFium error {err}).",
            });
        }
        PageCount = FPDF_GetPageCount(_doc);
        if (PageCount <= 0) throw new PdfOpenException("This PDF has no pages.");
        var sizes = new Vector2[PageCount];
        for (int i = 0; i < PageCount; i++)
            sizes[i] = FPDF_GetPageSizeByIndexF(_doc, i, out var sz) != 0 && sz.Width > 0 && sz.Height > 0
                ? new Vector2(sz.Width, sz.Height)
                : new Vector2(612, 792);
        PageSizes = sizes;
        _affine = new PageAffine?[PageCount];
        _imported = new bool[PageCount];
        _parsed = new List<Stroke>?[PageCount];
    }

    public Task<T> Invoke<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    void Enqueue(Action a)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _jobs.Enqueue(a);
            Monitor.Pulse(_lock);
        }
    }

    /// <summary>Replaces the tile wish-list (highest priority first). The list is taken over by the engine.</summary>
    public void SetWanted(List<TileKey> keys)
    {
        lock (_lock)
        {
            _wanted = keys;
            Monitor.Pulse(_lock);
        }
    }

    /// <summary>Makes sure these pages' original ink has been read (used after crash recovery).</summary>
    public void PreImport(IEnumerable<int> pages)
    {
        var list = pages.ToList();
        Enqueue(() => { foreach (int i in list) GetPage(i); });
    }

    void Run()
    {
        while (true)
        {
            Action? job = null;
            TileKey tile = default;
            lock (_lock)
            {
                while (!_disposed && _jobs.Count == 0 && _wanted.Count == 0) Monitor.Wait(_lock);
                if (_disposed) break;
                if (_jobs.Count > 0) job = _jobs.Dequeue();
                else
                {
                    tile = _wanted[0];
                    _wanted.RemoveAt(0);
                }
            }
            try
            {
                if (job != null) job();
                else if (_doc != IntPtr.Zero) RenderTile(tile);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        foreach (var p in _open.Values) FPDF_ClosePage(p);
        _open.Clear();
        if (_doc != IntPtr.Zero) FPDF_CloseDocument(_doc);
        _doc = IntPtr.Zero;
        if (_pin.IsAllocated) _pin.Free();
    }

    public float PreviewScale(int page)
    {
        var s = PageSizes[page];
        return 480f / MathF.Max(s.X, s.Y);
    }

    unsafe void RenderTile(TileKey k)
    {
        if (Tiles.ContainsKey(k) || Device == null || k.Page < 0 || k.Page >= PageCount) return;
        var size = PageSizes[k.Page];
        bool preview = k.Level == TileKey.Preview;
        float s = preview ? PreviewScale(k.Page) : k.Level / 1000f;
        int pw = Math.Max(1, (int)MathF.Round(size.X * s)), ph = Math.Max(1, (int)MathF.Round(size.Y * s));
        int x0 = preview ? 0 : k.X * TileSize, y0 = preview ? 0 : k.Y * TileSize;
        int w = preview ? pw : Math.Min(TileSize, pw - x0), h = preview ? ph : Math.Min(TileSize, ph - y0);
        if (w <= 0 || h <= 0) return;

        IntPtr page = GetPage(k.Page);
        if (page == IntPtr.Zero) return;

        var buf = new byte[w * h * 4];
        fixed (byte* p = buf)
        {
            IntPtr bmp = FPDFBitmap_CreateEx(w, h, FPDFBitmap_BGRA, (IntPtr)p, w * 4);
            FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
            FPDF_RenderPageBitmap(bmp, page, -x0, -y0, pw, ph, 0, FPDF_ANNOT);
            FPDFBitmap_Destroy(bmp);
        }
        var cb = CanvasBitmap.CreateFromBytes(Device, buf, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized, Dpi);
        if (!Tiles.TryAdd(k, cb)) cb.Dispose();
        TileReady?.Invoke();
    }

    IntPtr GetPage(int i)
    {
        if (_open.TryGetValue(i, out var p))
        {
            _lru.Remove(i);
            _lru.AddFirst(i);
            return p;
        }
        p = FPDF_LoadPage(_doc, i);
        if (p == IntPtr.Zero) return p;
        _open[i] = p;
        _lru.AddFirst(i);
        TrimPages();
        if (!_imported[i]) ImportInk(i, p);
        return p;
    }

    void TrimPages()
    {
        if (_holdPages) return;
        while (_lru.Count > MaxOpenPages)
        {
            int old = _lru.Last!.Value;
            _lru.RemoveLast();
            FPDF_ClosePage(_open[old]);
            _open.Remove(old);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Monitor.Pulse(_lock);
        }
        _thread.Join(5000);
        foreach (var b in Tiles.Values) b.Dispose();
        Tiles.Clear();
    }
}
