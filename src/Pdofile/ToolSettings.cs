using System.Text.Json;

namespace Pdofile;

public enum Tool { Pen, Highlighter, Eraser, Lasso, Hand, Line, Arrow, Rect, Ellipse, Axes }

public sealed class ToolSettings
{
    public Tool Tool { get; set; } = Tool.Pen;
    public uint PenColor { get; set; } = 0x000000;
    public float PenWidth { get; set; } = 1.6f;
    public uint HlColor { get; set; } = 0xFFF176;
    /// <summary>Last colour chosen from the colour wheel, shown as an extra swatch.</summary>
    public uint PenCustomColor { get; set; } = 0x7B1FA2;
    public uint HlCustomColor { get; set; } = 0xFFCC80;
    public float HlWidth { get; set; } = 12f;
    /// <summary>Eraser radius in screen DIPs, so it feels the same at any zoom.</summary>
    public float EraserRadius { get; set; } = 10f;
    public bool EraserWholeStroke { get; set; }
    public int Smoothing { get; set; } = 2;
    public bool Pressure { get; set; } = true;
    public bool HoldToShape { get; set; } = true;
    public int AxesUnits { get; set; } = 5;
    public bool AxesGrid { get; set; }
    public bool AxesFirstQuadrant { get; set; }
    public bool RulerVisible { get; set; }

    public static bool IsShapeTool(Tool t) => t is Tool.Line or Tool.Arrow or Tool.Rect or Tool.Ellipse or Tool.Axes;

    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string FilePath => Path.Combine(AppData, "pdofile", "settings.json");
    // Settings written before the app was renamed.
    static string OldFilePath => Path.Combine(AppData, "InkPDF", "settings.json");

    public static ToolSettings Load()
    {
        try
        {
            string path = File.Exists(FilePath) ? FilePath : OldFilePath;
                return JsonSerializer.Deserialize<ToolSettings>(File.ReadAllText(path)) ?? new ToolSettings();
        }
        catch
        {
            // Corrupt settings fall back to defaults.
        }
        return new ToolSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Settings are a convenience; never fail because of them.
        }
    }
}
