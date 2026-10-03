using InkPDF.Ink;
using InkPDF.Pdf;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using WindowActivatedEventArgs = Microsoft.UI.Xaml.WindowActivatedEventArgs;

namespace InkPDF;

public sealed partial class MainWindow : Window
{
    // Black, dark blue, red, green; anything else comes from the colour wheel.
    static readonly uint[] PenColors = [0x000000, 0x1F3A93, 0xD32F2F, 0x2E7D32];
    static readonly uint[] HlColors = [0xFFF176, 0xA5F5A5, 0xFFB0DA, 0xA0DDFF];
    static readonly Tool[] ShapeCycle = [Tool.Line, Tool.Arrow, Tool.Rect, Tool.Ellipse, Tool.Axes];

    readonly ToolSettings _settings = ToolSettings.Load();
    readonly DispatcherQueueTimer _recoveryTimer;
    readonly IntPtr _hwnd;
    readonly (ToggleButton btn, Tool tool)[] _toolButtons;

    PdfEngine? _engine;
    InkStore? _store;
    string? _path;
    int _savedVersion;
    bool _busy, _closeConfirmed, _syncing, _dialogOpen, _started;
    Tool _lastShape = Tool.Line;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "InkPDF";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 950));
        AppWindow.Closing += OnClosing;

        _toolButtons =
        [
            (PenBtn, Tool.Pen), (HlBtn, Tool.Highlighter), (EraserBtn, Tool.Eraser), (LassoBtn, Tool.Lasso), (HandBtn, Tool.Hand),
            (LineBtn, Tool.Line), (ArrowBtn, Tool.Arrow), (RectBtn, Tool.Rect), (EllipseBtn, Tool.Ellipse), (AxesBtn, Tool.Axes),
        ];
        if (ToolSettings.IsShapeTool(_settings.Tool)) _lastShape = _settings.Tool;

        DocView.Settings = _settings;
        DocView.ViewChanged += (_, _) => UpdateStatus();
        DocView.InkEdited += (_, _) =>
        {
            UpdateTitle();
            _recoveryTimer!.Stop();
            _recoveryTimer.Start();
        };

        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        Root.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnKeyUp), true);

        _recoveryTimer = DispatcherQueue.CreateTimer();
        _recoveryTimer.Interval = TimeSpan.FromSeconds(3);
        _recoveryTimer.IsRepeating = false;
        _recoveryTimer.Tick += (_, _) => WriteRecovery();

        Activated += OnActivated;
        SyncToolbar();
        UpdateStatus();
    }

    bool IsDirty => _store != null && _store.Version != _savedVersion;

    async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Win32.DisablePenFeedback(_hwnd);
        if (_started) return;
        _started = true;
        DocView.FocusCanvas();
        var cmd = Environment.GetCommandLineArgs();
        if (cmd.Length >= 5 && cmd[1] == "--selftest")
            await RunSelfTest(cmd[2], cmd[3], cmd[4], cmd.Contains("--keep"));
        else if (cmd.Length > 1 && File.Exists(cmd[1])) await OpenPath(cmd[1]);
    }

    // ---- documents -------------------------------------------------------------------------

    async void Open_Click(object sender, RoutedEventArgs e) => await OpenDialog();

    async Task OpenDialog()
    {
        var p = Win32.PickPdf(_hwnd, false, _path, "Open PDF");
        if (p != null) await OpenPath(p);
    }

    async Task OpenPath(string path)
    {
        if (_busy || !await ConfirmUnsaved()) return;
        _busy = true;
        try
        {
            PdfEngine engine;
            try
            {
                engine = await PdfEngine.OpenAsync(path);
            }
            catch (Exception ex)
            {
                await ShowMessage("Can't open this PDF", ex.Message);
                return;
            }
            CloseDocument();
            var store = new InkStore(engine.PageCount);
            _engine = engine;
            _store = store;
            _path = path;
            _savedVersion = store.Version;
            DocView.Load(engine, store);
            EmptyHint.Visibility = Visibility.Collapsed;
            UpdateTitle();
            UpdateStatus();

            var rec = Recovery.TryRead(path, engine.PageCount);
            if (rec is { Count: > 0 })
            {
                if (await Ask("Recover unsaved ink?",
                        "InkPDF closed before your last changes to this file were saved. Restore them?", "Restore", "Discard"))
                {
                    var groups = new Dictionary<int, int>();
                    foreach (var (page, strokes) in rec)
                    {
                        store.Restore(page, strokes.Select(s =>
                        {
                            int g = 0;
                            if (s.Group != 0 && !groups.TryGetValue(s.Group, out g)) groups[s.Group] = g = Stroke.NewGroup();
                            return s.Clone(group: g);
                        }).ToList());
                    }
                    engine.PreImport(rec.Keys);
                    _savedVersion = -1;
                    UpdateTitle();
                }
                else Recovery.Delete(path);
            }
            DocView.FocusCanvas();
        }
        finally
        {
            _busy = false;
        }
    }

    void CloseDocument()
    {
        _recoveryTimer.Stop();
        DocView.Unload();
        _engine?.Dispose();
        _engine = null;
        _store = null;
        _path = null;
        EmptyHint.Visibility = Visibility.Visible;
        UpdateTitle();
        UpdateStatus();
    }

    /// <summary>Returns false if the user cancelled.</summary>
    async Task<bool> ConfirmUnsaved()
    {
        if (!IsDirty || _path == null) return true;
        var result = await ThreeWay("Save changes?", $"Save your ink in \"{Path.GetFileName(_path)}\"?");
        if (result == ContentDialogResult.Primary) return await Save(null);
        if (result == ContentDialogResult.Secondary)
        {
            Recovery.Delete(_path);
            _savedVersion = _store?.Version ?? 0;
            return true;
        }
        return false;
    }

    async void Save_Click(object sender, RoutedEventArgs e) => await Save(null);
    async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAs();

    async Task SaveAs()
    {
        if (_path == null) return;
        var p = Win32.PickPdf(_hwnd, true, _path, "Save PDF as");
        if (p != null) await Save(p);
    }

    async Task<bool> Save(string? target)
    {
        if (_engine == null || _store == null || _path == null || _busy) return false;
        _busy = true;
        string path = target ?? _path;
        try
        {
            var (strokes, ack) = _store.SnapshotForSave();
            int version = _store.Version;
            var bytes = await _engine.BuildPdfAsync(strokes, ack);
            await Task.Run(() => FileUtil.WriteAtomic(path, bytes));
            Recovery.Delete(_path);
            _path = path;
            _savedVersion = version;
            UpdateTitle();
            return true;
        }
        catch (Exception ex)
        {
            string hint = ex is IOException or UnauthorizedAccessException
                ? "\n\nIf the file is open in another program, close it there and try again, or use Save as."
                : "";
            await ShowMessage("Couldn't save", ex.Message + hint);
            return false;
        }
        finally
        {
            _busy = false;
        }
    }

    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_engine == null || _store == null || _path == null || _busy) return;
        string suggested = Path.Combine(Path.GetDirectoryName(_path) ?? "", Path.GetFileNameWithoutExtension(_path) + " (annotated).pdf");
        var p = Win32.PickPdf(_hwnd, true, suggested, "Export flattened copy");
        if (p == null) return;
        if (string.Equals(Path.GetFullPath(p), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessage("Choose another name", "The flattened copy can't replace the file you're editing.");
            return;
        }
        _busy = true;
        try
        {
            var (strokes, ack) = _store.SnapshotForSave();
            var bytes = await _engine.BuildPdfAsync(strokes, ack);
            await _engine.ExportFlattenedAsync(bytes, p);
            await ShowMessage("Exported", $"Saved a flattened copy to:\n{p}");
        }
        catch (Exception ex)
        {
            await ShowMessage("Couldn't export", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    void WriteRecovery()
    {
        if (!IsDirty || _store == null || _path == null) return;
        var data = Recovery.Snapshot(_store);
        var path = _path;
        _ = Task.Run(() => Recovery.Write(path, data));
    }

    async void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        _settings.Save();
        if (_closeConfirmed || !IsDirty)
        {
            CloseDocument();
            return;
        }
        e.Cancel = true;
        if (await ConfirmUnsaved())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var pdf = items.OfType<StorageFile>().FirstOrDefault(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf != null) await OpenPath(pdf.Path);
    }

    // ---- dialogs -----------------------------------------------------------------------

    async Task<ContentDialogResult> ShowDialog(ContentDialog d)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        try
        {
            d.XamlRoot = Content.XamlRoot;
            return await d.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    Task<ContentDialogResult> ShowMessage(string title, string text) =>
        ShowDialog(new ContentDialog { Title = title, Content = text, CloseButtonText = "OK" });

    async Task<bool> Ask(string title, string text, string yes, string no) =>
        await ShowDialog(new ContentDialog
        {
            Title = title, Content = text, PrimaryButtonText = yes, CloseButtonText = no, DefaultButton = ContentDialogButton.Primary,
        }) == ContentDialogResult.Primary;

    Task<ContentDialogResult> ThreeWay(string title, string text) =>
        ShowDialog(new ContentDialog
        {
            Title = title, Content = text, PrimaryButtonText = "Save", SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        });

    // ---- toolbar -----------------------------------------------------------------------

    void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && Enum.TryParse<Tool>(t, out var tool)) SetTool(tool);
    }

    void SetTool(Tool t)
    {
        _settings.Tool = t;
        if (ToolSettings.IsShapeTool(t)) _lastShape = t;
        DocView.ToolChanged();
        SyncToolbar();
    }

    void Whole_Click(object sender, RoutedEventArgs e)
    {
        _settings.EraserWholeStroke = !_settings.EraserWholeStroke;
        SyncToolbar();
    }

    void Ruler_Click(object sender, RoutedEventArgs e) => ToggleRuler();

    void ToggleRuler()
    {
        _settings.RulerVisible = !_settings.RulerVisible;
        DocView.RefreshOverlay();
        SyncToolbar();
    }

    void SyncToolbar()
    {
        _syncing = true;
        foreach (var (btn, tool) in _toolButtons) btn.IsChecked = _settings.Tool == tool;
        WholeBtn.IsChecked = _settings.EraserWholeStroke;
        WholeBtn.Visibility = _settings.Tool == Tool.Eraser ? Visibility.Visible : Visibility.Collapsed;
        RulerBtn.IsChecked = _settings.RulerVisible;

        switch (_settings.Tool)
        {
            case Tool.Highlighter:
                SetSlider(4, 30, 1, _settings.HlWidth);
                break;
            case Tool.Eraser:
                SetSlider(3, 60, 1, _settings.EraserRadius);
                break;
            default:
                SetSlider(0.4, 8, 0.1, _settings.PenWidth);
                break;
        }
        UpdateSizeLabel();
        BuildSwatches();

        SmoothBox.SelectedIndex = Math.Clamp(_settings.Smoothing, 0, 3);
        PressureSwitch.IsOn = _settings.Pressure;
        HoldSwitch.IsOn = _settings.HoldToShape;
        AxesUnitsBox.Value = _settings.AxesUnits;
        AxesGridSwitch.IsOn = _settings.AxesGrid;
        AxesQuadSwitch.IsOn = _settings.AxesFirstQuadrant;
        _syncing = false;
    }

    void SetSlider(double min, double max, double step, double value)
    {
        SizeSlider.Minimum = min;
        SizeSlider.Maximum = max;
        SizeSlider.StepFrequency = step;
        SizeSlider.Value = value;
    }

    void UpdateSizeLabel()
    {
        (SizeLabel.Text, SizeHeader.Text) = _settings.Tool switch
        {
            Tool.Highlighter => ($"{_settings.HlWidth:0} pt", "Highlighter width"),
            Tool.Eraser => ($"⌀ {_settings.EraserRadius * 2:0}", "Eraser size"),
            _ => ($"{_settings.PenWidth:0.0} pt", "Pen / shape width"),
        };
    }

    void Size_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        float v = (float)e.NewValue;
        switch (_settings.Tool)
        {
            case Tool.Highlighter: _settings.HlWidth = v; break;
            case Tool.Eraser: _settings.EraserRadius = v; break;
            default: _settings.PenWidth = v; break;
        }
        UpdateSizeLabel();
        DocView.RefreshOverlay();
    }

    void StepSize(int dir)
    {
        SizeSlider.Value = Math.Clamp(SizeSlider.Value + dir * (_settings.Tool is Tool.Highlighter or Tool.Eraser ? 2 : 0.2), SizeSlider.Minimum, SizeSlider.Maximum);
    }

    static Windows.UI.Color ToColor(uint c) => ColorHelper.FromArgb(255, (byte)(c >> 16), (byte)(c >> 8), (byte)c);
    static uint FromColor(Windows.UI.Color c) => ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    void BuildSwatches()
    {
        Swatches.Children.Clear();
        bool hl = _settings.Tool == Tool.Highlighter;
        var colors = hl ? HlColors : PenColors;
        uint current = hl ? _settings.HlColor : _settings.PenColor;
        // A colour picked before (e.g. from an older palette) lives in the custom slot.
        if (!colors.Contains(current))
        {
            if (hl) _settings.HlCustomColor = current;
            else _settings.PenCustomColor = current;
        }
        uint custom = hl ? _settings.HlCustomColor : _settings.PenCustomColor;

        for (int i = 0; i < colors.Length; i++)
            Swatches.Children.Add(Swatch(colors[i], current, $"Colour ({i + 1})"));
        Swatches.Children.Add(Swatch(custom, current, "Custom colour (5)"));

        var picker = new ColorPicker
        {
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            Color = ToColor(custom),
        };
        var flyout = new Flyout { Content = picker };
        flyout.Closed += (_, _) =>
        {
            uint chosen = FromColor(picker.Color);
            if (chosen == custom) return;
            if (hl) _settings.HlCustomColor = chosen;
            else _settings.PenCustomColor = chosen;
            PickColor(chosen);
        };
        var wheel = new Button
        {
            Padding = new Thickness(5),
            MinWidth = 0,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            AllowFocusOnInteraction = false,
            IsTabStop = false,
            Content = new FontIcon { Glyph = "", FontSize = 16 },
            Flyout = flyout,
        };
        ToolTipService.SetToolTip(wheel, "More colours…");
        Swatches.Children.Add(wheel);
    }

    Button Swatch(uint c, uint current, string tip)
    {
        var b = new Button
        {
            Padding = new Thickness(3),
            MinWidth = 0,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            AllowFocusOnInteraction = false,
            IsTabStop = false,
            Content = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(ToColor(c)),
                BorderThickness = new Thickness(c == current ? 3 : 1),
                BorderBrush = new SolidColorBrush(c == current ? ColorHelper.FromArgb(255, 0x1A, 0x73, 0xE8) : ColorHelper.FromArgb(80, 0, 0, 0)),
            },
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => PickColor(c);
        return b;
    }

    void PickColor(uint c)
    {
        if (_settings.Tool == Tool.Highlighter) _settings.HlColor = c;
        else
        {
            _settings.PenColor = c;
            if (DocView.HasSelection) DocView.RecolorSelection(c);
            else if (_settings.Tool is Tool.Eraser or Tool.Hand or Tool.Lasso) SetTool(Tool.Pen);
        }
        SyncToolbar();
        DocView.RefreshOverlay();
    }

    void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _settings.Smoothing = Math.Max(0, SmoothBox.SelectedIndex);
        _settings.Pressure = PressureSwitch.IsOn;
        _settings.HoldToShape = HoldSwitch.IsOn;
        _settings.AxesGrid = AxesGridSwitch.IsOn;
        _settings.AxesFirstQuadrant = AxesQuadSwitch.IsOn;
    }

    void AxesUnits_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing || double.IsNaN(args.NewValue)) return;
        _settings.AxesUnits = (int)Math.Clamp(args.NewValue, 1, 40);
    }

    void Undo_Click(object sender, RoutedEventArgs e) => DocView.Undo();
    void Redo_Click(object sender, RoutedEventArgs e) => DocView.Redo();
    void ZoomIn_Click(object sender, RoutedEventArgs e) => DocView.ZoomBy(1.2f);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => DocView.ZoomBy(1 / 1.2f);
    void Fit_Click(object sender, RoutedEventArgs e) => DocView.FitWidth();

    void UpdateStatus()
    {
        ZoomPill.Visibility = DocView.HasDocument ? Visibility.Visible : Visibility.Collapsed;
        if (!DocView.HasDocument)
        {
            PageLabel.Text = "";
            ZoomLabel.Text = "";
            return;
        }
        PageLabel.Text = $"Page {DocView.CurrentPage + 1} / {DocView.PageCount}";
        ZoomLabel.Text = $"{DocView.ZoomPercent:0}%";
    }

    void UpdateTitle()
    {
        Title = _path == null ? "InkPDF" : $"{(IsDirty ? "● " : "")}{Path.GetFileName(_path)} — InkPDF";
    }

    // ---- keyboard ------------------------------------------------------------------------

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox || _dialogOpen) return;
        bool ctrl = Down(VirtualKey.Control), shift = Down(VirtualKey.Shift);
        var k = e.Key;
        if (k == VirtualKey.Space && !ctrl)
        {
            DocView.SpaceDown = true;
            e.Handled = true;
            return;
        }
        e.Handled = true;
        if (ctrl)
        {
            switch (k)
            {
                case VirtualKey.O: await OpenDialog(); break;
                case VirtualKey.S: if (shift) await SaveAs(); else await Save(null); break;
                case VirtualKey.Z: if (shift) DocView.Redo(); else DocView.Undo(); break;
                case VirtualKey.Y: DocView.Redo(); break;
                case VirtualKey.C: DocView.CopySelection(); break;
                case VirtualKey.X: DocView.CutSelection(); break;
                case VirtualKey.V: DocView.Paste(); break;
                case VirtualKey.D: DocView.DuplicateSelection(); break;
                case VirtualKey.A:
                    if (_settings.Tool != Tool.Lasso) SetTool(Tool.Lasso);
                    DocView.SelectAllOnPage();
                    break;
                case VirtualKey.Number0: DocView.FitWidth(); break;
                case VirtualKey.Number1: DocView.SetZoomPercent(100); break;
                case VirtualKey.Add or (VirtualKey)187: DocView.ZoomBy(1.2f); break;
                case VirtualKey.Subtract or (VirtualKey)189: DocView.ZoomBy(1 / 1.2f); break;
                default: e.Handled = false; break;
            }
            return;
        }
        switch (k)
        {
            case VirtualKey.P: SetTool(Tool.Pen); break;
            case VirtualKey.H: SetTool(Tool.Highlighter); break;
            case VirtualKey.E:
                if (shift) Whole_Click(this, new RoutedEventArgs());
                else SetTool(Tool.Eraser);
                break;
            case VirtualKey.L: SetTool(Tool.Lasso); break;
            case VirtualKey.V: SetTool(Tool.Hand); break;
            case VirtualKey.S:
                SetTool(ToolSettings.IsShapeTool(_settings.Tool)
                    ? ShapeCycle[(Array.IndexOf(ShapeCycle, _settings.Tool) + 1) % ShapeCycle.Length]
                    : _lastShape);
                break;
            case VirtualKey.R: ToggleRuler(); break;
            case >= VirtualKey.Number1 and <= VirtualKey.Number5:
            {
                int i = k - VirtualKey.Number1;
                bool hl = _settings.Tool == Tool.Highlighter;
                var colors = hl ? HlColors : PenColors;
                PickColor(i < colors.Length ? colors[i] : hl ? _settings.HlCustomColor : _settings.PenCustomColor);
                break;
            }
            case VirtualKey.Delete:
            case VirtualKey.Back: DocView.DeleteSelection(); break;
            case VirtualKey.Escape: DocView.ClearSelection(); break;
            case VirtualKey.PageDown: DocView.ScrollByScreens(1); break;
            case VirtualKey.PageUp: DocView.ScrollByScreens(-1); break;
            case VirtualKey.Down: DocView.ScrollByScreens(0.15f); break;
            case VirtualKey.Up: DocView.ScrollByScreens(-0.15f); break;
            case VirtualKey.Home: DocView.ScrollToEnd(false); break;
            case VirtualKey.End: DocView.ScrollToEnd(true); break;
            case (VirtualKey)219: StepSize(-1); break;   // [
            case (VirtualKey)221: StepSize(1); break;    // ]
            default: e.Handled = false; break;
        }
    }

    void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space) DocView.SpaceDown = false;
    }
}
