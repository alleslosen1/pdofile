# pdofile

A fast, lightweight PDF reader for writing on PDFs with a pen — built for teaching maths:
write and fix equations, draw graphs and shapes, then hand the annotated PDF to students.

- **Clean ink** — pressure-sensitive, smoothed strokes (drawing tablets and pen displays via Windows Ink).
- **Fast erasing** — the eraser cuts through just what it touches (or removes whole strokes) without
  re-rendering the PDF, so it stays instant even on pages full of ink.
- **Shapes** — line, arrow, rectangle, ellipse and coordinate axes (with grid). Hold the pen still at the
  end of a stroke to straighten it into a line, circle or polygon; hold **Shift** to draw a straight line.
- **Lasso** — select ink to move, resize, rotate, recolour, copy or delete.
- **Ruler** — drag it anywhere, rotate it, and draw along its edge for straight lines.
- **Standard PDFs** — ink is saved as normal PDF ink annotations, so it shows up in Acrobat, Edge,
  Chrome and other readers. *Export flattened copy* bakes the ink into the pages for sharing.
- **Safe** — unsaved ink is journalled in the background and offered back after a crash.

## Download

Get the latest build from [Releases](../../releases):

| File | What it is |
|---|---|
| `pdofile.exe` | Single self-contained executable. Download and run — nothing to install. First launch takes a few seconds while it unpacks. |
| `pdofile-portable-win-x64.zip` | Portable folder. Unzip anywhere (e.g. a USB stick) and run `pdofile.exe`. Starts faster than the single exe. |
| Source code | Attached automatically by GitHub. |

Requires 64-bit Windows 10 (1809) or Windows 11. No .NET or other runtime needed. The app isn't
code-signed yet, so SmartScreen may warn on first run: **More info → Run anyway**.

**Using a drawing tablet?** Turn on **Windows Ink** in your tablet driver's settings (Wacom, XP-Pen, Huion)
so pen pressure reaches the app.

## Using it

Open a PDF with the button, **Ctrl+O**, or by dropping a file on the window.

| Action | How |
|---|---|
| Erase | Eraser tool (**E**), the pen's eraser end, the pen's side button, or the right mouse button |
| Pan / zoom | Mouse wheel, **Space**+drag, middle mouse button; **Ctrl**+wheel to zoom, **Ctrl+0** fit width |
| Tools | **P** pen · **H** highlighter · **E** eraser (**Shift+E** whole strokes) · **L** lasso · **S** shapes · **R** ruler · **V** pan |
| Straight line | Hold **Shift** while drawing (snaps to 0° / 45° / 90° within 3°) |
| Colours | **1–4** black, dark blue, red, green · **5** custom colour (palette button opens the colour wheel) |
| Size | **[** and **]** |
| Edit | **Ctrl+Z / Ctrl+Y** undo/redo · **Ctrl+C / X / V / D** copy, cut, paste, duplicate · **Ctrl+A** select all · **Delete** |
| Save | **Ctrl+S** (overwrites the file) · **Ctrl+Shift+S** save as |

Crashes are logged to `%LOCALAPPDATA%\pdofile\crash.log`.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
dotnet build src/Pdofile/Pdofile.csproj -c Release
# single-file exe:
dotnet publish src/Pdofile/Pdofile.csproj -c Release -p:PublishSingleFile=true `
  -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true
# self-test (dense ink, erase timing, save/reload round trip):
pdofile.exe --selftest in.pdf out.pdf log.txt
```

---

# Project notes

How pdofile came about: the design, what was deliberately in and out of scope, and how it progressed.

## 1. Design

**Problem.** Mainstream PDF readers are poor at handwriting. Acrobat's ink is mediocre, and readers such
as PDFGear lag when erasing: they store ink as PDF annotations and re-render the whole page (PDF + every
annotation) on each eraser movement, so the cost grows with the amount of ink on the page.

**Core idea: keep the PDF and the ink apart.**

```
Input ─► Tool (pen / highlighter / eraser / lasso / shapes / ruler)
                    │ edits
                    ▼
          Ink model (per page) ◄──► spatial grid (48 pt cells)
                    │                    │ dirty rectangles
          Undo stack (one step per gesture)
                    ▼                    ▼
 Screen:  [PDF tiles] × [highlighter tiles, multiply] + [pen tiles] + [live stroke / overlays]
                    │
 File:    standard PDF Ink annotations (on save) + recovery journal (in the background)
```

- **PDF rendering** — PDFium runs on one dedicated worker thread and renders pages into 512 px tiles,
  cached and drawn 1:1 at the screen's DPI. A small whole-page preview (and the previous zoom level,
  stretched) fills in while tiles load. Ink never causes a PDF re-render.
- **Ink model** — strokes are immutable lists of points (x, y, radius) in page points, indexed per page
  by a uniform grid, so erasing and redrawing only touch strokes near the edit.
- **Ink rendering** — ink is rasterised into its own cached tiles; an edit redraws only its dirty
  rectangle. Strokes are drawn as discs joined by segments (batched GPU primitives). Highlighters live
  in separate tiles that are *multiplied* onto the PDF tile, so text underneath stays dark.
- **Input & smoothing** — every pen sample (including coalesced ones) is used; a one-euro filter removes
  jitter without lag; a centripetal Catmull-Rom spline gives smooth curves; pressure maps to width with
  a natural taper. The live stroke bakes finished segments into a layer, so only its tail is redrawn.
- **Eraser** — the eraser path is tested against densely sampled stroke points from nearby grid cells;
  touched runs are cut out and the remaining pieces become new strokes (one undo step per drag).
  Whole-stroke mode removes complete strokes (and whole shapes).
- **Shapes** — generated as ordinary non-smoothed strokes, grouped so lasso moves them together.
  Hold-to-straighten recognises lines, circles/ellipses, triangles, rectangles and short polylines.
- **Saving** — each stroke becomes a standard `/Ink` annotation with an appearance stream (pen: Bézier
  segments with per-segment width; highlighter: one path with multiply blending), plus a compact
  private copy of the full stroke data (delta-encoded, 1/64 pt) so pdofile reloads it exactly. Existing
  ink from other apps is imported lazily per page and becomes editable. Files are written to a temp
  file and swapped in atomically.
- **Safety** — unsaved ink is journalled to `%LOCALAPPDATA%\pdofile\Recovery` a few seconds after
  each change and offered back on the next open.

**Stack.** C# / .NET 10, WinUI 3 (Windows App SDK 2.x — WinUI + Foundation components only), Win2D,
PDFium (bblanchon/pdfium-binaries). Windows-native was chosen for the best pen latency and tablet support.

## 2. Scope

Target user: a maths teacher using a **drawing tablet** on **Windows**, teaching in class, online and in
recordings.

**In scope (v0.1)** — PDF viewing (no passwords, no cloud), pen and highlighter with pressure and smoothing,
partial and whole-stroke eraser (pen eraser tip / side button / right mouse), shapes and coordinate axes,
hold-to-straighten and Shift lines, lasso transform and recolour, ruler, undo/redo, colours with a
colour wheel, save / save as / flattened export, crash recovery, keyboard shortcuts.

**Out of scope** — editing PDF text, forms, digital signatures, OCR, password-protected files,
cloud sync, handwriting-to-LaTeX, macOS / Linux / iPad.

**Changed from the original spec**
- Own pointer pipeline + Win2D instead of Windows InkPresenter (needed for partial erasing and full
  control of the stroke model).
- Save rewrites the whole file (to a temp file, then swapped in) rather than an incremental append —
  simpler and safe; fast enough so far.
- Recognised shapes are saved as ink strokes, not Line/Circle annotations.
- Inserting blank/grid pages and a laser pointer were moved to the roadmap.

## 3. Progression

| Stage | What happened |
|---|---|
| Feasibility & spec | Decided a focused pen-first reader (not a full Acrobat clone) is realistic; wrote the design above. |
| Toolchain spike | Confirmed WinUI 3 + Win2D + PDFium build and run from the command line, self-contained. |
| MVP | Ink model, PDFium engine, tiled view, tools, toolbar, saving, recovery. Added a `--selftest` mode that fills a page with 4,800 strokes, drags the eraser across it while timing frames, then saves, reopens and checks every stroke survived. |
| Bugs caught by testing | Toolbar overflowed (→ icon toolbar, pop-out size slider, floating page/zoom panel). Any highlighter crashed the app (Win2D rejects the "min" blend for images → GPU multiply blend effect). At 125% display scaling tiles were stretched (→ tiles at the canvas DPI, pixel source rectangles). |
| Performance pass | Stroke rendering switched from geometry unions to batched discs + segments, and full tile renders are spread across frames. On the 4,800-stroke page: first draw 1.1 s → 0.3 s, zoom step 0.94 s → 0.21 s, erase frame ≈ 1 ms. |
| Feedback round | Shift-to-draw-straight-lines; four default colours (black, dark blue, red, green) plus a colour wheel. |
| Release 0.1.0 | Renamed to pdofile; dropped ~45 MB of unused AI runtimes from the Windows App SDK; published a 70.6 MB single exe and a 68.9 MB portable zip. |

**Measured performance** (Intel i5-12450HX, synthetic page with 4,800 strokes):

| Measure | Result |
|---|---|
| Eraser step (model update) | ~0.15 ms |
| Frame while erasing (dirty-tile redraw) | ~0.9 ms average, ~2 ms worst |
| Idle frame | ~0.15 ms |
| Page first fully drawn | ~0.3 s, spread over several frames (UI stays responsive) |
| Zoom step re-render | ~0.2 s |

## 4. Roadmap

- Verify on real tablet hardware (pressure curve, eraser tip, side buttons).
- Smaller saved files for very ink-heavy pages (≈ 7 MB for 4,800 strokes today).
- Insert blank / grid pages; extend margins for working space.
- Laser pointer and presentation mode.
- Installer and code signing; choose a licence.

Built with WinUI 3 (Windows App SDK), Win2D and PDFium. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
