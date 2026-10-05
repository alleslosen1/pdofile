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

Requires 64-bit Windows 10 (1809) or Windows 11. No .NET or other runtime needed.

**Using a drawing tablet?** Turn on **Windows Ink** in your tablet driver's settings (Wacom, XP-Pen, Huion)
so pen pressure reaches the app.

## Using it

Open a PDF with the button, **Ctrl+O**, or by dropping a file on the window.

| Action | How |
|---|---|
| Erase | Eraser tool (**E**), the pen's eraser end, the pen's side button, or the right mouse button |
| Pan / zoom | Mouse wheel, **Space**+drag, middle mouse button; **Ctrl**+wheel to zoom, **Ctrl+0** fit width |
| Tools | **P** pen · **H** highlighter · **E** eraser (**Shift+E** whole strokes) · **L** lasso · **S** shapes · **R** ruler · **V** pan |
| Colours | **1–4** default colours, **5** custom colour (palette button opens the colour wheel) |
| Size | **[** and **]** |
| Edit | **Ctrl+Z / Ctrl+Y** undo/redo · **Ctrl+C / X / V / D** copy, cut, paste, duplicate · **Ctrl+A** select all · **Delete** |
| Save | **Ctrl+S** (overwrites the file) · **Ctrl+Shift+S** save as |

Crashes are logged to `%LOCALAPPDATA%\pdofile\crash.log`.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
dotnet build src/Pdofile/Pdofile.csproj -c Release
# single-file exe:
dotnet publish src/Pdofile/Pdofile.csproj -c Release -p:PublishSingleFile=true
```

Built with WinUI 3 (Windows App SDK), Win2D and PDFium. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
