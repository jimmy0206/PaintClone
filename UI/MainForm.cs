using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using PaintClone.Core;
using PaintClone.Documents;
using PaintClone.Rendering;
using PaintClone.Tools;

namespace PaintClone.UI;

public sealed class MainForm : Form
{
    private readonly PaintDocument _doc = new();
    private readonly CanvasControl _canvas;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ColorPanel _colorPanel = new();
    private readonly ToolStrip _toolStrip = new();
    private readonly MenuStrip _menu = new();
    private readonly Panel _status = new() { Dock = DockStyle.Bottom, Padding = new Padding(6, 2, 24, 2) };
    private readonly Label _hintLabel = new() { AutoSize = false, Dock = DockStyle.Left, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _posLabel = new() { AutoSize = true, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0) };
    private readonly Label _sizeLabel = new() { AutoSize = true, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0) };
    private readonly Label _zoomLabel = new()
    {
        AutoSize = true,
        Dock = DockStyle.Right,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 8, 0),
        ForeColor = SystemColors.HotTrack,
        Cursor = Cursors.Hand,
    };
    private readonly System.Windows.Forms.Timer _autoSaveTimer = new();
    private readonly ToolTip _toolTip = new();

    private ToolStripButton _undoBtn = null!, _redoBtn = null!;
    private ToolStripDropDownButton _sizeDrop = null!, _styleDrop = null!, _optionsDrop = null!;
    private readonly Dictionary<string, ToolStripButton> _toolButtons = new();
    private readonly List<Tool> _tools = new();

    // 圖層面板
    private readonly Panel _layerPanel = new();
    private readonly ListView _layerList = new();
    private Panel? _layerButtonsPanel;
    private readonly List<Button> _layerButtons = new();
    private readonly Splitter _layerSplitter = new()
    {
        Dock = DockStyle.Right,
        Width = 5,
        MinSize = 150,
    };
    private ContextMenuStrip _zoomMenu = null!;

    private readonly PencilTool _pencil = new();
    private readonly BrushTool _brush = new();
    private readonly AirbrushTool _airbrush = new();
    private readonly EraserTool _eraser = new();
    private readonly FillTool _fill = new();
    private readonly ColorPickerTool _picker = new();
    private readonly MagicWandTool _wand = new();
    private readonly LineTool _line = new();
    private readonly CurveTool _curve = new();
    private readonly RectangleTool _rect = new();
    private readonly RoundedRectangleTool _roundRect = new();
    private readonly EllipseTool _ellipse = new();
    private readonly CalloutTool _callout = new();
    private readonly PolygonTool _polygon = new();
    private readonly TextTool _text = new();
    private readonly SelectTool _select = new();
    private readonly LassoTool _lasso = new();

    private const string AppTitle = "小畫家 Plus";

    public MainForm()
    {
        Text = AppTitle;
        // 以 96dpi 為設計基準，依裝置 DPI 遞迴縮放全部控制項（高 DPI 下不再跑版）
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        DoubleBuffered = true;   // 拖曳調整視窗大小的過程整幅離屏合成，消除 dock 重排閃爍/殘留
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1000;   // 實際大小於 OnShown 以實體像素還原（存檔值不再經 DPI 二次縮放）
        Height = 700;
        WindowState = _settings.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        BackColor = SystemColors.Control;
        KeyPreview = true;
        AllowDrop = true;

        _canvas = new CanvasControl(_doc) { Dock = DockStyle.Fill, AllowDrop = true };
        _canvas.ShowGrid = _settings.ShowGrid;
        _canvas.Options.AntialiasShapes = _settings.AntialiasShapes;
        _canvas.Options.FloodTolerance = _settings.FloodTolerance;
        _canvas.Options.FloodUseColor2 = _settings.FloodUseColor2;

        BuildTools();
        BuildMenu();
        BuildToolStrip();
        BuildColorPanel();
        BuildLayerPanel();
        BuildStatus();
        BuildZoomMenu();

        // Dock 由後加入者先排：splitter 要排在圖層面板左緣，必須比面板先加入
        Controls.Add(_canvas);
        Controls.Add(_layerSplitter);
        Controls.Add(_layerPanel);
        Controls.Add(_colorPanel);
        Controls.Add(_toolStrip);
        Controls.Add(_menu);
        Controls.Add(_status);
        _layerSplitter.Visible = _layerPanel.Visible;
        MainMenuStrip = _menu;

        // 事件接線
        _canvas.ToolHint += hint => _hintLabel.Text = hint;
        _canvas.PointerMoved += pos => _posLabel.Text = pos is { } p ? $"位置：{p.X},{p.Y}" : "";
        _canvas.ZoomChanged += (_, _) => UpdateStatusSize();
        _doc.Changed += (_, _) => UpdateUndoButtons();
        _doc.StructureChanged += (_, _) => { RefreshLayerList(); UpdateStatusSize(); UpdateTitle(); UpdateUndoButtons(); };
        _canvas.ColorChanged += (which, c) => { _colorPanel.SetFromExternal(which, c); _colorPanel.PushRecent(c); };
        _canvas.ToolChanged += (_, _) =>
        {
            foreach (var (name, btn) in _toolButtons)
                btn.Checked = _canvas.ActiveTool.Name == name;
            UpdateSizeDropCaption();
        };
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _canvas.DragEnter += OnDragEnter;
        _canvas.DragDrop += OnDragDrop;
        _canvas.Selection.Changed += (_, _) => UpdateStatusSize();

        _colorPanel.LoadPersisted(_settings.RecentColorsHex);
        _canvas.ActiveTool = _pencil;
        UpdateStatusSize();
        UpdateTitle();

        // 自動存檔（崩潰回覆）
        _autoSaveTimer.Interval = 20_000;
        _autoSaveTimer.Tick += (_, _) => DoAutoSave();
        _autoSaveTimer.Start();

        Load += (_, _) => { CheckCrashRecovery(); CheckStartupFile(); };
    }

    public void LoadOnStartup(string path) => _startupFile = path;
    private string? _startupFile;

    private void CheckStartupFile()
    {
        if (_startupFile is not null && File.Exists(_startupFile))
            OpenFile(_startupFile);
    }

    // ------------------------------------------------------------ 工具

    private void BuildTools()
    {
        _tools.AddRange(new Tool[]
        {
            _select, _lasso, _wand, _pencil, _brush, _airbrush, _eraser, _fill, _picker,
            _line, _curve, _rect, _roundRect, _ellipse, _callout, _polygon, _text,
        });
    }

    private void BuildToolStrip()
    {
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.ImageScalingSize = new Size(22, 22);
        _toolStrip.Padding = new Padding(4, 2, 4, 2);
        // Flow 佈局：寬度不足時自動換行（取代溢出箭頭），高度由 RelayoutToolStrip 動態調整
        _toolStrip.LayoutStyle = ToolStripLayoutStyle.Flow;
        _toolStrip.AutoSize = false;
        _toolStrip.Height = 34;

        ToolStripButton Icon(string kind, Action click, string tip)
        {
            var b = new ToolStripButton(IconFactory.Make(kind)) { ToolTipText = tip, AutoToolTip = false, DisplayStyle = ToolStripItemDisplayStyle.Image };
            b.AccessibleName = kind; // 供 ApplyHiDpiOnce 依原樣式高解析重繪
            b.Click += (_, _) => click();
            return b;
        }

        _toolStrip.Items.Add(Icon("new", NewImage, "新增 (Ctrl+N)"));
        _toolStrip.Items.Add(Icon("open", () => OpenWithDialog(), "開啟 (Ctrl+O)"));
        _toolStrip.Items.Add(Icon("save", () => Save(), "儲存 (Ctrl+S)"));
        _toolStrip.Items.Add(new ToolStripSeparator());
        _undoBtn = Icon("undo", DoUndo, "復原 (Ctrl+Z)");
        _redoBtn = Icon("redo", DoRedo, "重做 (Ctrl+Y)");
        _toolStrip.Items.Add(_undoBtn);
        _toolStrip.Items.Add(_redoBtn);
        _toolStrip.Items.Add(new ToolStripSeparator());

        foreach (var (tool, kind) in new (Tool, string)[]
        {
            (_select, "select"), (_lasso, "lasso"), (_wand, "wand"),
            (_pencil, "pencil"), (_brush, "brush"), (_airbrush, "airbrush"), (_eraser, "eraser"),
            (_fill, "fill"), (_picker, "picker"),
            (_line, "line"), (_curve, "curve"),
            (_rect, "rect"), (_roundRect, "roundrect"), (_ellipse, "ellipse"), (_callout, "callout"), (_polygon, "polygon"),
            (_text, "text"),
        })
        {
            var btn = new ToolStripButton(IconFactory.Make(kind))
            {
                Tag = tool,
                AccessibleName = kind,
                ToolTipText = tool.Name,
                AutoToolTip = false,
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                CheckOnClick = false,
            };
            btn.Click += (_, _) => _canvas.ActiveTool = tool;
            _toolButtons[tool.Name] = btn;
            _toolStrip.Items.Add(btn);
        }

        _sizeDrop = new ToolStripDropDownButton("大小") { ShowDropDownArrow = true };
        _sizeDrop.DropDownOpening += (_, _) => RebuildSizeMenu();
        _styleDrop = new ToolStripDropDownButton("樣式") { ShowDropDownArrow = true };
        RebuildStyleMenuStatic();
        _optionsDrop = new ToolStripDropDownButton("選項") { ShowDropDownArrow = true };
        RebuildOptionsMenuStatic();
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_sizeDrop);
        _toolStrip.Items.Add(_styleDrop);
        _toolStrip.Items.Add(_optionsDrop);

        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(Icon("zoomin", () => _canvas.ZoomIn(), "放大 (Ctrl++)"));
        _toolStrip.Items.Add(Icon("zoomout", () => _canvas.ZoomOut(), "縮小 (Ctrl+-)"));
        _toolStrip.Items.Add(Icon("layers", ToggleLayerPanel, "圖層面板"));
    }

    private void BuildColorPanel()
    {
        _colorPanel.Dock = DockStyle.Top;
        _colorPanel.ColorChosen += (which, c) =>
        {
            if (which == 0) { _canvas.Color1 = _colorPanel.Color1; _canvas.Color2 = _colorPanel.Color2; }
            else if (which == 1) _canvas.Color1 = c;
            else _canvas.Color2 = c;
        };
    }

    private void BuildLayerPanel()
    {
        _layerPanel.Dock = DockStyle.Right;
        _layerPanel.Width = 190;
        _layerPanel.Visible = _settings.ShowLayersPanel;
        _layerPanel.Padding = new Padding(6);
        _layerSplitter.MouseUp += (_, _) =>
        {
            int max = (int)(420 * DeviceDpi / 96f);   // 上限亦依 DPI 換算（實體像素）
            if (_layerPanel.Width > max) _layerPanel.Width = max;
        };

        var title = new Label { Text = "圖層", Dock = DockStyle.Top, AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        _layerList.Dock = DockStyle.Fill;
        _layerList.View = View.Details;
        _layerList.FullRowSelect = true;
        _layerList.HideSelection = false;
        _layerList.CheckBoxes = true;
        _layerList.Columns.Add("名稱", 100);
        _layerList.Columns.Add("屬性", 50);
        _layerList.ItemCheck += OnLayerItemCheck;
        _layerList.SelectedIndexChanged += OnLayerSelectedChanged;

        // 拖曳排序（优化：不用按上移/下移按鈕）
        _layerList.AllowDrop = true;
        _layerList.ItemDrag += (_, e) =>
        {
            if (e.Item is ListViewItem it && _doc.Layers.Count > 1)
                _layerList.DoDragDrop(it, DragDropEffects.Move);
        };
        _layerList.DragEnter += (_, e) =>
            e.Effect = e.Data?.GetDataPresent(typeof(ListViewItem)) == true ? DragDropEffects.Move : DragDropEffects.None;
        _layerList.DragOver += (_, e) =>
        {
            var pt = _layerList.PointToClient(new Point(e.X, e.Y));
            int idx = _layerList.GetItemAt(pt.X, pt.Y)?.Index ?? -1;
            if (idx >= 0 && (_layerList.SelectedIndices.Count != 1 || _layerList.SelectedIndices[0] != idx))
            {
                _suppressLayerEvents = true;
                _layerList.SelectedIndices.Clear();
                _layerList.Items[idx].Selected = true;   // 以反白提示放下的位置
                _suppressLayerEvents = false;
            }
            e.Effect = DragDropEffects.Move;
        };
        _layerList.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(typeof(ListViewItem)) is not ListViewItem it) return;
            var pt = _layerList.PointToClient(new Point(e.X, e.Y));
            int dst = _layerList.GetItemAt(pt.X, pt.Y)?.Index ?? -1;
            if (dst >= 0) ReorderLayer(it.Index, dst);
        };

        // 手動 2 欄置中排列（LayoutLayerButtons），高度依 DPI 計算，不受 AutoSize 行為影響
        var buttons = new Panel { Dock = DockStyle.Bottom };
        Button Small(string text, Action act)
        {
            var b = new Button { Text = text };
            b.Click += (_, _) => act();
            _layerButtons.Add(b);
            return b;
        }
        buttons.Controls.AddRange(new Control[]
        {
            Small("新增", AddLayer), Small("刪除", DeleteLayer),
            Small("上移", () => MoveLayer(-1)), Small("下移", () => MoveLayer(1)),
            Small("向下合併", MergeDown), Small("透明化背景", FlattenTransparent),
        });
        _layerButtonsPanel = buttons;
        _layerPanel.Resize += (_, _) => LayoutLayerButtons();

        _layerPanel.Controls.Add(_layerList);
        _layerPanel.Controls.Add(buttons);
        _layerPanel.Controls.Add(title);
        LayoutLayerButtons();
    }

    /// <summary>圖層按鈕固定 2 欄 × 3 列，尺寸依 DeviceDpi 換算（跑版防禦）。</summary>
    private void LayoutLayerButtons()
    {
        if (_layerButtonsPanel is null) return;
        float f = DeviceDpi / 96f;
        int h = (int)(26 * f), gap = Math.Max(2, (int)(3 * f));
        int availW = _layerPanel.ClientSize.Width - _layerPanel.Padding.Horizontal;
        int bw = Math.Max(40, (availW - gap) / 2);
        for (int i = 0; i < _layerButtons.Count; i++)
            _layerButtons[i].SetBounds(
                _layerPanel.Padding.Left + (i % 2 == 0 ? 0 : bw + gap),
                (i / 2) * (h + gap), bw, h);
        _layerButtonsPanel.Height = ((_layerButtons.Count + 1) / 2) * (h + gap) + gap;
    }

    private void BuildStatus()
    {
        // 一般 Panel＋Label：StatusStrip 的 Spring/Table 佈局在高 DPI 會把尾端標籤推出可見區（跑版根源之一）
        _status.Height = 28;
        _hintLabel.Dock = DockStyle.Fill;   // 剩餘空間＋AutoEllipsis 自動截断
        _status.Controls.Add(_hintLabel);   // Fill 先加入＝最後排版
        _status.Controls.Add(_posLabel);
        _status.Controls.Add(_sizeLabel);
        _status.Controls.Add(_zoomLabel);   // 後加入者先排＝最貼右緣
        _toolTip.SetToolTip(_zoomLabel, "點擊選擇縮放比例");
        _zoomLabel.Click += (_, _) =>
        {
            var p = _status.PointToScreen(new Point(_zoomLabel.Bounds.X, _status.Bottom));
            _zoomMenu.Show(p.X, Math.Max(0, p.Y - _zoomMenu.Height));
        };
    }

    private void BuildZoomMenu()
    {
        _zoomMenu = new ContextMenuStrip { Font = _menu.Font };
        foreach (var pct in new[] { 10, 25, 50, 100, 150, 200, 300, 400, 600, 800 })
        {
            var item = new ToolStripMenuItem($"{pct}%");
            int p = pct;
            item.Click += (_, _) => _canvas.Zoom = p / 100.0;
            _zoomMenu.Items.Add(item);
        }
        var fit = new ToolStripMenuItem("顯示完整圖片");
        fit.Click += (_, _) => _canvas.FitZoom();
        var actual = new ToolStripMenuItem("傳真大小 (100%)");
        actual.Click += (_, _) => _canvas.ResetZoom();
        _zoomMenu.Items.AddRange(new ToolStripItem[] { new ToolStripSeparator(), fit, actual });
    }

    // ------------------------------------------------------------ 選單

    private void BuildMenu()
    {
        ToolStripMenuItem Item(string text, string shortcutText, Keys shortcut, Action act)
        {
            var i = new ToolStripMenuItem(text);
            if (shortcut != Keys.None)
            {
                i.ShortcutKeys = shortcut;
                i.ShowShortcutKeys = true;
            }
            else if (shortcutText.Length > 0)
                i.ShortcutKeyDisplayString = shortcutText;
            i.Click += (_, _) => act();
            return i;
        }

        var file = new ToolStripMenuItem("檔案(&F)");
        file.DropDownItems.Add(Item("新增", "Ctrl+N", Keys.Control | Keys.N, NewImage));
        file.DropDownItems.Add(Item("開啟...", "Ctrl+O", Keys.Control | Keys.O, () => OpenWithDialog()));
        _recentMenu = new ToolStripMenuItem("最近使用的檔案");
        RebuildRecentMenu();
        file.DropDownItems.Add(_recentMenu);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("儲存", "Ctrl+S", Keys.Control | Keys.S, () => Save()));
        file.DropDownItems.Add(Item("另存新檔...", "Ctrl+Shift+S", Keys.Control | Keys.Shift | Keys.S, () => SaveAs()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("列印...", "Ctrl+P", Keys.Control | Keys.P, PrintImage));
        file.DropDownItems.Add(Item("設為桌面佈景主題", "", Keys.None, SetAsWallpaper));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("結束", "", Keys.None, Close));

        var edit = new ToolStripMenuItem("編輯(&E)");
        var undoItem = Item("復原", "Ctrl+Z", Keys.Control | Keys.Z, DoUndo);
        var redoItem = Item("重做", "Ctrl+Y", Keys.Control | Keys.Y, DoRedo);
        edit.DropDownItems.AddRange(new ToolStripItem[]
        {
            undoItem, redoItem, new ToolStripSeparator(),
            Item("剪下選取範圍", "Ctrl+X", Keys.Control | Keys.X, () => SelectionOps.Cut(_canvas)),
            Item("複製選取範圍", "Ctrl+C", Keys.Control | Keys.C, () => SelectionOps.Copy(_canvas)),
            Item("貼上 (新圖層)", "Ctrl+V", Keys.Control | Keys.V, Paste),
            Item("刪除 (填入顏色2)", "Del", Keys.None, () => { if (_canvas.Selection.IsActive) SelectionOps.Delete(_canvas); }),
            new ToolStripSeparator(),
            Item("全選", "Ctrl+A", Keys.Control | Keys.A, () => _canvas.Selection.SelectAll(_doc.Size)),
            Item("取消選取", "Ctrl+E", Keys.Control | Keys.E, () => _canvas.Selection.Clear()),
        });

        var image = new ToolStripMenuItem("圖像(&I)");
        image.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("調整大小...", "", Keys.None, ResizeImage),
            Item("傾斜...", "", Keys.None, SkewImage),
            new ToolStripSeparator(),
            Item("順時針旋轉 90°", "", Keys.None, () => SelectionOps.RotateImage(_canvas, -90)),
            Item("逆時針旋轉 90°", "", Keys.None, () => SelectionOps.RotateImage(_canvas, 90)),
            Item("旋轉 180°", "", Keys.None, () => SelectionOps.RotateImage(_canvas, 180)),
            Item("任意角度旋轉...", "", Keys.None, RotateCustom),
            new ToolStripSeparator(),
            Item("水平反轉", "", Keys.None, () => SelectionOps.FlipImage(_canvas, true)),
            Item("垂直反轉", "", Keys.None, () => SelectionOps.FlipImage(_canvas, false)),
            new ToolStripSeparator(),
            Item("裁剪", "", Keys.None, () => SelectionOps.CropToSelection(_canvas)),
            Item("圖像資訊", "", Keys.None, () => { using var d = new ImageInfoDialog(_doc); d.ShowDialog(this); }),
            new ToolStripSeparator(),
            Item("反相", "", Keys.None, () => SelectionOps.EffectInvert(_canvas)),
            Item("灰階", "", Keys.None, () => SelectionOps.EffectGrayscale(_canvas)),
            Item("褐色調", "", Keys.None, () => _doc.ApplyGlobal(ImageOps.Sepia)),
            Item("調整顏色...", "", Keys.None, AdjustColors),
        });

        var view = new ToolStripMenuItem("檢視(&V)");
        var gridItem = new ToolStripMenuItem("網格線") { Checked = _settings.ShowGrid };
        gridItem.Click += (_, _) => { gridItem.Checked = !gridItem.Checked; _canvas.ShowGrid = gridItem.Checked; _settings.ShowGrid = gridItem.Checked; _canvas.Invalidate(); };
        var aaItem = new ToolStripMenuItem("shapes 反鋸齒") { Checked = _settings.AntialiasShapes };
        aaItem.Click += (_, _) => { aaItem.Checked = !aaItem.Checked; _canvas.Options.AntialiasShapes = aaItem.Checked; _settings.AntialiasShapes = aaItem.Checked; };
        var layerItem = new ToolStripMenuItem("圖層面板") { Checked = _settings.ShowLayersPanel };
        layerItem.Click += (_, _) => { ToggleLayerPanel(); layerItem.Checked = _layerPanel.Visible; };
        view.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("放大", "Ctrl++", Keys.Control | Keys.Oemplus, () => _canvas.ZoomIn()),
            Item("縮小", "Ctrl+-", Keys.Control | Keys.OemMinus, () => _canvas.ZoomOut()),
            Item("顯示完整圖片", "", Keys.None, () => _canvas.FitZoom()),
            Item("傳真大小 (100%)", "Ctrl+0", Keys.Control | Keys.D0, () => _canvas.ResetZoom()),
            new ToolStripSeparator(),
            gridItem, aaItem, layerItem,
        });

        var help = new ToolStripMenuItem("說明(&H)");
        help.DropDownItems.Add(Item("關於", "", Keys.None, () => { using var d = new AboutDialog(); d.ShowDialog(this); }));

        _menu.Items.AddRange(new ToolStripItem[] { file, edit, image, view, help });
    }

    private ToolStripMenuItem? _recentMenu;

    private void RebuildRecentMenu()
    {
        if (_recentMenu is null) return;
        _recentMenu.DropDownItems.Clear();
        foreach (var path in _settings.RecentFiles)
        {
            var item = new ToolStripMenuItem(Path.GetFileName(path)) { Tag = path };
            item.Click += (_, _) => { if (ConfirmDiscard()) OpenFile(path); };
            _recentMenu.DropDownItems.Add(item);
        }
        _recentMenu.Visible = _settings.RecentFiles.Count > 0;
    }

    // ------------------------------------------------------------ 工具選單（動態）

    private void RebuildSizeMenu()
    {
        _sizeDrop.DropDownItems.Clear();
        var t = _canvas.ActiveTool;
        int[] choices;
        Func<int> get;
        Action<int> set;
        switch (t)
        {
            case PencilTool: choices = new[] { 1, 2, 3, 4, 5 }; get = () => _canvas.Options.PencilWidth; set = v => _canvas.Options.PencilWidth = v; break;
            case BrushTool: choices = new[] { 3, 5, 7, 10, 14, 20, 28 }; get = () => _canvas.Options.BrushWidth; set = v => _canvas.Options.BrushWidth = v; break;
            case EraserTool: choices = new[] { 6, 10, 16, 24, 36, 56 }; get = () => _canvas.Options.EraserWidth; set = v => _canvas.Options.EraserWidth = v; break;
            case AirbrushTool: choices = new[] { 8, 16, 28, 44 }; get = () => _canvas.Options.SprayWidth; set = v => _canvas.Options.SprayWidth = v; break;
            case LineTool or CurveTool or RectangleTool or RoundedRectangleTool or EllipseTool or PolygonTool or CalloutTool:
                choices = new[] { 1, 2, 3, 5, 8 }; get = () => _canvas.Options.ShapeWidth; set = v => _canvas.Options.ShapeWidth = v; break;
            default:
                choices = new[] { 1, 2, 3, 5 }; get = () => _canvas.Options.ShapeWidth; set = v => _canvas.Options.ShapeWidth = v; break;
        }
        foreach (var c in choices)
        {
            var item = new ToolStripMenuItem($"{c} 像素") { Checked = get() == c };
            int val = c;
            item.Click += (_, _) => { set(val); _sizeDrop.Text = $"大小 {val}"; };
            _sizeDrop.DropDownItems.Add(item);
        }
        if (t is EraserTool)
        {
            var rounded = new ToolStripMenuItem("圓頭橡皮擦") { Checked = _canvas.Options.EraserRounded };
            rounded.Click += (_, _) => _canvas.Options.EraserRounded = !_canvas.Options.EraserRounded;
            _sizeDrop.DropDownItems.Add(new ToolStripSeparator());
            _sizeDrop.DropDownItems.Add(rounded);
        }
    }

    /// <summary>切換工具時讓「大小」按鈕直接顯示目前粗細（優化：減少展開次數）。</summary>
    private void UpdateSizeDropCaption()
    {
        int? v = _canvas.ActiveTool switch
        {
            PencilTool => _canvas.Options.PencilWidth,
            BrushTool => _canvas.Options.BrushWidth,
            EraserTool => _canvas.Options.EraserWidth,
            AirbrushTool => _canvas.Options.SprayWidth,
            LineTool or CurveTool or RectangleTool or RoundedRectangleTool
                or EllipseTool or PolygonTool or CalloutTool => _canvas.Options.ShapeWidth,
            _ => null,
        };
        _sizeDrop.Text = v is null ? "大小" : $"大小 {v}";
    }

    private void RebuildStyleMenuStatic()
    {
        void AddStyle(string text, ShapeFillStyle style)
        {
            var item = new ToolStripMenuItem(text) { Tag = style };
            item.Click += (_, _) =>
            {
                _canvas.Options.ShapeStyle = style;
                foreach (ToolStripMenuItem i in _styleDrop.DropDownItems)
                    i.Checked = (ShapeFillStyle?)i.Tag == style;
            };
            _styleDrop.DropDownItems.Add(item);
        }
        AddStyle("只有外框", ShapeFillStyle.Outline);
        AddStyle("僅填色", ShapeFillStyle.Fill);
        AddStyle("外框＋填色", ShapeFillStyle.OutlineFill);
        ((ToolStripMenuItem)_styleDrop.DropDownItems[0]).Checked = true;
    }

    private void RebuildOptionsMenuStatic()
    {
        void AddNum(string label, int[] choices, Func<int> get, Action<int> set, string unit = "")
        {
            var parent = new ToolStripMenuItem(label);
            foreach (var c in choices)
            {
                var item = new ToolStripMenuItem($"{c}{unit}") { Checked = get() == c };
                int val = c;
                item.Click += (_, _) =>
                {
                    set(val);
                    foreach (ToolStripMenuItem i in parent.DropDownItems) i.Checked = false;
                    item.Checked = true;
                };
                parent.DropDownItems.Add(item);
            }
            _optionsDrop.DropDownItems.Add(parent);
        }

        AddNum("填色容差", new[] { 0, 5, 10, 20, 30, 50, 80 }, () => _canvas.Options.FloodTolerance, v => { _canvas.Options.FloodTolerance = v; _settings.FloodTolerance = v; });
        var c2 = new ToolStripMenuItem("填色使用顏色2") { Checked = _canvas.Options.FloodUseColor2 };
        c2.Click += (_, _) => { c2.Checked = !c2.Checked; _canvas.Options.FloodUseColor2 = c2.Checked; _settings.FloodUseColor2 = c2.Checked; };
        _optionsDrop.DropDownItems.Add(c2);
        AddNum("魔法棒容差", new[] { 10, 20, 30, 50, 80 }, () => _canvas.Options.WandTolerance, v => _canvas.Options.WandTolerance = v);
        AddNum("圓角半徑", new[] { 4, 8, 12, 16, 24, 32 }, () => _canvas.Options.CornerRadius, v => _canvas.Options.CornerRadius = v, " px");
        AddNum("標註框邊寬", new[] { 2, 3, 5, 8, 12 }, () => _canvas.Options.CalloutBorder, v => _canvas.Options.CalloutBorder = v, " px");

        var font = new ToolStripMenuItem("文字字型...");
        font.Click += (_, _) =>
        {
            using var fd = new FontDialog { Font = _canvas.Options.TextFont, ShowColor = false };
            if (fd.ShowDialog(this) == DialogResult.OK) _canvas.Options.TextFont = fd.Font;
        };
        _optionsDrop.DropDownItems.Add(font);

        var transparentText = new ToolStripMenuItem("文字透明背景") { Checked = _canvas.Options.TextTransparent };
        transparentText.Click += (_, _) => { transparentText.Checked = !transparentText.Checked; _canvas.Options.TextTransparent = transparentText.Checked; };
        _optionsDrop.DropDownItems.Add(transparentText);
    }

    // ------------------------------------------------------------ 工具切換

    private void UpdateUndoButtons()
    {
        _undoBtn.Enabled = _doc.CanUndo;
        _redoBtn.Enabled = _doc.CanRedo;
    }

    private void DoUndo() { _canvas.CommitActiveText(); _doc.Undo(); _canvas.Invalidate(); }
    private void DoRedo() { _canvas.CommitActiveText(); _doc.Redo(); _canvas.Invalidate(); }

    // ------------------------------------------------------------ 檔案

    private void UpdateTitle()
    {
        var name = _doc.FilePath is null ? "未命名" : Path.GetFileName(_doc.FilePath);
        Text = $"{( _doc.IsDirty ? "*" : "")}{name} - {AppTitle}";
    }

    private bool ConfirmDiscard()
    {
        if (!_doc.IsDirty) return true;
        var r = MessageBox.Show(this, "是否儲存對目前圖片的變更？", AppTitle,
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return r switch
        {
            DialogResult.Yes => Save(),
            DialogResult.No => true,
            _ => false,
        };
    }

    private void NewImage()
    {
        if (!ConfirmDiscard()) return;
        _canvas.Selection.Clear();
        _doc.New(new Size(1280, 720), Color.White);
        _canvas.FitZoom();
        UpdateTitle();
    }

    private void OpenWithDialog()
    {
        if (!ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog
        {
            Filter = "圖片檔案|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有檔案|*.*",
            Title = "開啟圖片",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            OpenFile(dlg.FileName);
    }

    private void OpenFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms, useEmbeddedColorManagement: true, validateImageData: false);
            _canvas.Selection.Clear();
            _doc.LoadFrom(img.ToArgb32(), path);
            _settings.PushRecentFile(path);
            RebuildRecentMenu();
            _canvas.FitZoom();
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"無法開啟檔案：\n{ex.Message}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private bool Save()
    {
        if (_doc.FilePath is null) return SaveAs();
        return SaveTo(_doc.FilePath);
    }

    private bool SaveAs()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG|*.png|JPEG|*.jpg|BMP|*.bmp|GIF|*.gif|TIFF|*.tiff",
            Title = "儲存圖片",
            FileName = _doc.FilePath is null ? "未命名.png" : Path.GetFileName(_doc.FilePath),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        return SaveTo(dlg.FileName);
    }

    private bool SaveTo(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            using var img = ext is ".jpg" or ".jpeg"
                ? _doc.FlattenForExport(Color.White)
                : _doc.FlattenForExport();

            if (ext is ".jpg" or ".jpeg")
            {
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
                img.Save(path, codec, ep);
            }
            else
            {
                var fmt = ext switch
                {
                    ".bmp" => System.Drawing.Imaging.ImageFormat.Bmp,
                    ".gif" => System.Drawing.Imaging.ImageFormat.Gif,
                    ".tif" or ".tiff" => System.Drawing.Imaging.ImageFormat.Tiff,
                    _ => System.Drawing.Imaging.ImageFormat.Png,
                };
                img.Save(path, fmt);
            }

            _doc.FilePath = path;
            _doc.MarkSaved();
            _settings.PushRecentFile(path);
            RebuildRecentMenu();
            UpdateTitle();
            ClearAutoSave();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"儲存失敗：\n{ex.Message}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    // ------------------------------------------------------------ 剪貼簿

    private void Paste()
    {
        if (!Clipboard.ContainsImage()) return;
        using var img = Clipboard.GetImage();
        if (img is null) return;
        SelectionOps.PasteImage(_canvas, img.ToArgb32());
    }

    private void CopyWholeImage()
    {
        using var img = _doc.ComposeCopy();
        Clipboard.SetImage(img);
    }

    // ------------------------------------------------------------ 圖像操作

    private void ResizeImage()
    {
        bool hasSel = _canvas.Selection.IsActive && _canvas.Selection.Mode == SelectionManager.SelMode.Rect;
        var baseSize = hasSel ? _canvas.Selection.Bounds.Size : _doc.Size;
        using var dlg = new ResizeDialog(baseSize.Width, baseSize.Height, hasSel);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        SelectionOps.ResizeImage(_canvas, dlg.NewWidth, dlg.NewHeight, dlg.Smoothing);
    }

    private void SkewImage()
    {
        using var dlg = new SkewDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (dlg.HorizontalDeg == 0 && dlg.VerticalDeg == 0) return;
        SelectionOps.SkewImage(_canvas, dlg.HorizontalDeg, dlg.VerticalDeg);
    }

    private void RotateCustom()
    {
        using var dlg = new RotateCustomDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (dlg.Angle == 0) return;
        SelectionOps.RotateImage(_canvas, dlg.Angle);
    }

    private void AdjustColors()
    {
        bool hasSel = _canvas.Selection.IsActive;
        // 一律取副本：無選取時若直接把文件快取交給對話框，對話框存活期間（自動存檔 20 秒排程）
        // 快取可能被別人收掉，讀 Width 就會丟 ArgumentException
        var target = hasSel ? SelectionOps.GetContent(_canvas) : _doc.ComposeCopy();
        try
        {
            using var dlg = new AdjustDialog(target);
            if (dlg.ShowDialog(this) == DialogResult.OK)
                SelectionOps.ApplyAdjust(_canvas, dlg.Hue, dlg.Saturation, dlg.Brightness, dlg.Contrast);
        }
        finally
        {
            target.Dispose();
        }
    }

    // ------------------------------------------------------------ 列印／桌布

    private void PrintImage()
    {
        using var pd = new PrintDialog { AllowPrintToFile = true };
        if (pd.ShowDialog(this) != DialogResult.OK) return;
        using var doc = new PrintDocument();
        doc.DocumentName = _doc.FilePath ?? "untitled";
        var img = _doc.FlattenForExport(Color.White);
        doc.PrintPage += (_, e) =>
        {
            var area = e.MarginBounds;
            var scale = Math.Min((double)area.Width / img.Width, (double)area.Height / img.Height);
            int w = (int)(img.Width * scale), h = (int)(img.Height * scale);
            int x = area.X + (area.Width - w) / 2, y = area.Y + (area.Height - h) / 2;
            e.Graphics!.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(img, x, y, w, h);
        };
        doc.EndPrint += (_, _) => img.Dispose();
        doc.PrinterSettings = pd.PrinterSettings;
        try { doc.Print(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"列印失敗：{ex.Message}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

    private void SetAsWallpaper()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "paintclone_wallpaper.jpg");
        using (var img = _doc.FlattenForExport(Color.White))
            img.Save(tmp, System.Drawing.Imaging.ImageFormat.Jpeg);
        SystemParametersInfo(20, 0, tmp, 3);
    }

    // ------------------------------------------------------------ 圖層

    private void ToggleLayerPanel()
    {
        _layerPanel.Visible = !_layerPanel.Visible;
        _layerSplitter.Visible = _layerPanel.Visible;
        _settings.ShowLayersPanel = _layerPanel.Visible;
        RefreshLayerList();
    }

    private bool _suppressLayerEvents;

    private void RefreshLayerList()
    {
        if (!_layerPanel.Visible) return;
        _suppressLayerEvents = true;
        _layerList.BeginUpdate();
        _layerList.Items.Clear();
        // 由上而下顯示（頂層在前）
        for (int i = _doc.Layers.Count - 1; i >= 0; i--)
        {
            var l = _doc.Layers[i];
            var item = new ListViewItem(l.Name) { Tag = i, Checked = l.Visible };
            if (l.IsBackground) item.SubItems.Add("背景");
            if (i == _doc.ActiveLayerIndex) item.BackColor = Color.FromArgb(225, 235, 250);
            _layerList.Items.Add(item);
        }
        for (int i = 0; i < _layerList.Items.Count; i++)
            if ((int)_layerList.Items[i].Tag! == _doc.ActiveLayerIndex)
                _layerList.Items[i].Selected = true;
        _layerList.EndUpdate();
        _suppressLayerEvents = false;
    }

    private int LayerIndexAt(int listIndex) =>
        _doc.Layers.Count - 1 - listIndex;

    private void OnLayerItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_suppressLayerEvents) return;
        int idx = LayerIndexAt(e.Index);
        if (idx < 0 || idx >= _doc.Layers.Count) return;
        _doc.Layers[idx].Visible = e.NewValue == CheckState.Checked;
        _doc.InvalidateComposed();
        _canvas.Invalidate();
        _doc.MarkDirty();
    }

    private void OnLayerSelectedChanged(object? sender, EventArgs e)
    {
        if (_suppressLayerEvents || _layerList.SelectedIndices.Count == 0) return;
        int idx = LayerIndexAt(_layerList.SelectedIndices[0]);
        if (idx >= 0 && idx < _doc.Layers.Count)
            _doc.SetActiveLayer(idx);
    }

    private void AddLayer()
    {
        var snap = _doc.TakeSnapshotInternal();
        var layer = new Layer($"圖層 {_doc.Layers.Count}", ImageOps.NewCanvas(_doc.Size.Width, _doc.Size.Height, Color.Transparent));
        _doc.Layers.Insert(Math.Min(_doc.ActiveLayerIndex + 1, _doc.Layers.Count), layer);
        _doc.PushStructuralUndo(snap);
        _doc.SetActiveLayer(_doc.Layers.IndexOf(layer));
    }

    private void DeleteLayer()
    {
        if (_doc.Layers.Count <= 1)
        {
            MessageBox.Show(this, "無法刪除唯一圖層。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var snap = _doc.TakeSnapshotInternal();
        var layer = _doc.Active;
        _doc.Layers.Remove(layer);
        layer.Image.Dispose();
        _doc.PushStructuralUndo(snap);
        _doc.SetActiveLayer(_doc.ActiveLayerIndex);
    }

    private void MoveLayer(int dir)
    {
        int i = _doc.ActiveLayerIndex;
        int j = i + dir;
        if (j < 0 || j >= _doc.Layers.Count) return;
        var snap = _doc.TakeSnapshotInternal();
        (_doc.Layers[i], _doc.Layers[j]) = (_doc.Layers[j], _doc.Layers[i]);
        _doc.PushStructuralUndo(snap);
        _doc.SetActiveLayer(j);
    }

    /// <summary>拖曳排序：把清單 srcList 列的圖層移到清單 dstList 列（清單由上而下＝頂層在前）。</summary>
    private void ReorderLayer(int srcList, int dstList)
    {
        if (srcList == dstList || srcList < 0 || srcList >= _layerList.Items.Count) return;
        var layers = _doc.Layers;
        int di = layers.Count - 1 - srcList;
        if (di < 0 || di >= layers.Count) return;
        var snap = _doc.TakeSnapshotInternal();
        var layer = layers[di];
        layers.RemoveAt(di);
        int di2 = Math.Clamp(layers.Count - 1 - dstList, 0, layers.Count);
        layers.Insert(di2, layer);
        _doc.PushStructuralUndo(snap);
        _doc.SetActiveLayer(layers.IndexOf(layer));
    }

    private void MergeDown()
    {
        int i = _doc.ActiveLayerIndex;
        if (i == 0)
        {
            MessageBox.Show(this, "已是最底層，無法向下合併。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var snap = _doc.TakeSnapshotInternal();
        var below = _doc.Layers[i - 1];
        using (var g = Graphics.FromImage(below.Image))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            g.DrawImageUnscaled(_doc.Layers[i].Image, 0, 0);
        }
        var top = _doc.Layers[i];
        _doc.Layers.RemoveAt(i);
        top.Image.Dispose();
        _doc.PushStructuralUndo(snap);
        _doc.SetActiveLayer(i - 1);
    }

    private void FlattenTransparent()
    {
        var snap = _doc.TakeSnapshotInternal();
        while (_doc.Layers.Count > 1)
        {
            var top = _doc.Layers[^1];
            var below = _doc.Layers[^2];
            using (var g = Graphics.FromImage(below.Image))
                g.DrawImageUnscaled(top.Image, 0, 0);
            top.Image.Dispose();
            _doc.Layers.RemoveAt(_doc.Layers.Count - 1);
        }
        _doc.Layers[0].IsBackground = true;
        _doc.Layers[0].Name = "背景";
        _doc.PushStructuralUndo(snap);
        _doc.SetActiveLayer(0);
    }

    // ------------------------------------------------------------ 狀態

    private void UpdateStatusSize()
    {
        _sizeLabel.Text = $"大小：{_doc.Size.Width} x {_doc.Size.Height}";
        _zoomLabel.Text = $"縮放：{_canvas.Zoom * 100:0}%" +
                          (_canvas.Selection.IsActive ? $"  選取：{_canvas.Selection.Bounds.Width} x {_canvas.Selection.Bounds.Height}" : "");
    }

    // ------------------------------------------------------------ 自動存檔

    private static string AutoSavePng => Path.Combine(AppSettings.AutoSaveDir, "current.png");
    private static string AutoSaveMeta => Path.Combine(AppSettings.AutoSaveDir, "current.meta");

    private void DoAutoSave()
    {
        if (!_doc.IsDirty) return;
        try
        {
            using var img = _doc.ComposeCopy();   // 必須是副本：直接 Dispose 文件快取會讓之後 Compose 全炸
            img.Save(AutoSavePng, System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllText(AutoSaveMeta, _doc.FilePath ?? "");
        }
        catch { /* 自動存檔失敗不打擾 */ }
    }

    private void ClearAutoSave()
    {
        try
        {
            if (File.Exists(AutoSavePng)) File.Delete(AutoSavePng);
            if (File.Exists(AutoSaveMeta)) File.Delete(AutoSaveMeta);
        }
        catch { }
    }

    private void CheckCrashRecovery()
    {
        if (!File.Exists(AutoSavePng)) return;
        var meta = File.Exists(AutoSaveMeta) ? File.ReadAllText(AutoSaveMeta) : "";
        if (_startupFile is not null && meta == _startupFile) return;
        var r = MessageBox.Show(this,
            "發現上次未儲存的自動存檔。\n要復原它嗎？\n（選擇「否」會刪除自動存檔）",
            AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r == DialogResult.Yes)
        {
            try
            {
                var bytes = File.ReadAllBytes(AutoSavePng);
                using var ms = new MemoryStream(bytes);
                using var img = Image.FromStream(ms);
                _doc.LoadFrom(img.ToArgb32(), string.IsNullOrEmpty(meta) ? null : meta);
                _doc.MarkDirty();
                _canvas.FitZoom();
                UpdateTitle();
            }
            catch { ClearAutoSave(); }
        }
        else ClearAutoSave();
    }

    // ------------------------------------------------------------ 拖放

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var path = files.FirstOrDefault(f =>
            Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff");
        if (path is not null && ConfirmDiscard()) OpenFile(path);
    }

    // ------------------------------------------------------------ 鍵盤

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        bool editingText = ActiveControl is TextBoxBase;

        switch (keyData)
        {
            case Keys.Control | Keys.Z: DoUndo(); return true;
            case Keys.Control | Keys.Y: DoRedo(); return true;
            case Keys.Control | Keys.N: NewImage(); return true;
            case Keys.Control | Keys.O: OpenWithDialog(); return true;
            case Keys.Control | Keys.S: Save(); return true;
            case Keys.Control | Keys.Shift | Keys.S: SaveAs(); return true;
            case Keys.Control | Keys.P: PrintImage(); return true;
            case Keys.Control | Keys.A: _canvas.Selection.SelectAll(_doc.Size); return true;
            case Keys.Control | Keys.E: _canvas.Selection.Clear(); return true;
            case Keys.Control | Keys.X when _canvas.Selection.IsActive: SelectionOps.Cut(_canvas); return true;
            case Keys.Control | Keys.C when _canvas.Selection.IsActive: SelectionOps.Copy(_canvas); return true;
            case Keys.Control | Keys.C: CopyWholeImage(); return true;
            case Keys.Control | Keys.V: Paste(); return true;
            case Keys.Control | Keys.Oemplus:
            case Keys.Control | Keys.Add: _canvas.ZoomIn(); return true;
            case Keys.Control | Keys.OemMinus:
            case Keys.Control | Keys.Subtract: _canvas.ZoomOut(); return true;
            case Keys.Control | Keys.D0: _canvas.ResetZoom(); return true;

            // Ctrl+方向鍵＝選取內容微調 1px（加 Shift 為 10px），對應原版行为
            case Keys.Control | Keys.Left when _canvas.Selection.IsActive && !editingText:
                SelectionOps.Nudge(_canvas, -1, 0); return true;
            case Keys.Control | Keys.Right when _canvas.Selection.IsActive && !editingText:
                SelectionOps.Nudge(_canvas, 1, 0); return true;
            case Keys.Control | Keys.Up when _canvas.Selection.IsActive && !editingText:
                SelectionOps.Nudge(_canvas, 0, -1); return true;
            case Keys.Control | Keys.Down when _canvas.Selection.IsActive && !editingText:
                SelectionOps.Nudge(_canvas, 0, 1); return true;
            case Keys.Control | Keys.Shift | Keys.Left when _canvas.Selection.IsActive && !editingText:
                SelectionOps.Nudge(_canvas, -10, 0); return true;
            case Keys.Control | Keys.Shift | Keys.Right when _canvas.Selection.IsActive && !editingText:
                SelectionOps.Nudge(_canvas, 10, 0); return true;
            case Keys.Control | Keys.Shift | Keys.Up when _canvas.Selection.IsActive && !editingText:
                SelectionOps.Nudge(_canvas, 0, -10); return true;
            case Keys.Control | Keys.Shift | Keys.Down when _canvas.Selection.IsActive && !editingText:
                SelectionOps.Nudge(_canvas, 0, 10); return true;

            case Keys.X when !editingText:
                _colorPanel.SwapColors();
                (_canvas.Color1, _canvas.Color2) = (_canvas.Color2, _canvas.Color1);
                _colorPanel.Color1 = _canvas.Color1;
                _colorPanel.Color2 = _canvas.Color2;
                return true;
        }

        if (keyData == Keys.Delete && _canvas.Selection.IsActive && !editingText)
        {
            SelectionOps.Delete(_canvas);
            return true;
        }

        if (!editingText && _canvas.ActiveTool.WantsKeys)
        {
            var e = new KeyEventArgs(keyData);
            _canvas.ActiveTool.OnKeyDown(e);
            if (e.Handled) return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }
        _autoSaveTimer.Stop();
        ClearAutoSave();
        var restore = WindowState == FormWindowState.Normal ? new Rectangle(Location, Size) : RestoreBounds;
        _settings.WindowWidth = Math.Max(restore.Width, 640);    // 實體像素；最大化時記 Normal 尺寸
        _settings.WindowHeight = Math.Max(restore.Height, 480);
        _settings.Maximized = WindowState == FormWindowState.Maximized;
        _settings.RecentColorsHex.Clear();
        _settings.RecentColorsHex.AddRange(_colorPanel.RecentColors.Select(ColorPanel.ToHex));
        _settings.Save();
        base.OnFormClosing(e);
    }

    /// <summary>工具欄 Flow 佈局：視窗寬度改變時重算換行列數與高度。</summary>
    private void RelayoutToolStrip()
    {
        int w = ClientSize.Width;
        if (w <= 0) return;
        var pref = _toolStrip.GetPreferredSize(new Size(Math.Max(80, w - 44), 1));
        int h = Math.Max(pref.Height + 2, (int)(30 * DeviceDpi / 96f));
        if (_toolStrip.Height != h) _toolStrip.Height = h;
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        RelayoutToolStrip();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 以實體像素還原上次視窗大小（存檔值為實體 px，不再經 DPI 二次縮放）
        if (WindowState == FormWindowState.Normal)
        {
            Width = Math.Max(640, _settings.WindowWidth);
            Height = Math.Max(480, _settings.WindowHeight);
        }
        // DPI 縮放後視窗可能超出工作區，收斂並置中
        if (WindowState == FormWindowState.Normal)
        {
            var wa = Screen.FromControl(this).WorkingArea;
            float f = DeviceDpi / 96f;
            int maxW = wa.Width - (int)(24 * f), maxH = wa.Height - (int)(24 * f);
            if (Width > maxW) Width = maxW;
            if (Height > maxH) Height = maxH;
            CenterToScreen();
        }
        ApplyHiDpiOnce();
        _canvas.Focus();
        UpdateUndoButtons();
        RefreshLayerList();
        UpdateStatusSize();
        _colorPanel.Color1 = _canvas.Color1;
        _colorPanel.Color2 = _canvas.Color2;
        UpdateSizeDropCaption();
    }

    private bool _hiDpiApplied;

    /// <summary>
    /// 表單 AutoScaleMode 只縮「尺寸」，不會縮圖示點陣圖與 ListView 欄寬；
    /// 這裡在視窗顯示後（DeviceDpi 已正確）做一次高解析修正。
    /// </summary>
    private void ApplyHiDpiOnce()
    {
        if (_hiDpiApplied) return;
        _hiDpiApplied = true;
        float f = DeviceDpi / 96f;
        if (f <= 1.01f) return;

        // 工具列圖示以原生解析度重繪（IconFactory 為向量式重繪，放大不失真）
        int iconPx = (int)Math.Round(22 * f);
        foreach (ToolStripItem item in _toolStrip.Items)
            if (item is ToolStripButton { Image: not null } b && !string.IsNullOrEmpty(b.AccessibleName))
            {
                var old = b.Image;
                b.Image = IconFactory.Make(b.AccessibleName, iconPx);
                old?.Dispose();
            }
        _toolStrip.ImageScalingSize = new Size(iconPx, iconPx);

        // ListView 欄寬不隨 ScaleControl，手動依 DPI 放大
        if (_layerList.Columns.Count >= 2)
        {
            _layerList.Columns[0].Width = (int)(100 * f);
            _layerList.Columns[1].Width = (int)(50 * f);
        }

        // 保底：若 AutoScale 未縮到（只增不減，重複執行安全）
        void AtLeast(Control c, int baseW, int baseH)
        {
            int w = (int)(baseW * f), h = (int)(baseH * f);
            if (baseW > 0 && c.Width < w) c.Width = w;
            if (baseH > 0 && c.Height < h) c.Height = h;
        }
        AtLeast(_colorPanel, 0, 52);      // 調色盤自繪內容高度
        AtLeast(_layerPanel, 190, 0);     // Dock=Right 寬度
        LayoutLayerButtons();
        RelayoutToolStrip();

        // 分割尺限制與視窗下限（MinimumSize 為實體像素）
        _layerSplitter.MinSize = (int)(150 * f);
        MinimumSize = new Size((int)(720 * f), (int)(500 * f));
    }
}
