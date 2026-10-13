using System.Drawing.Drawing2D;
using PaintClone.Documents;

namespace PaintClone.Rendering;

public enum ShapeFillStyle { Outline, Fill, OutlineFill }

/// <summary>工具共用的選項列（對應小畫家工具列上的大小/樣式選單）。</summary>
public sealed class ToolOptions
{
    public int PencilWidth = 1;
    public int BrushWidth = 7;
    public int EraserWidth = 16;
    public bool EraserRounded = true;
    public int ShapeWidth = 2;
    public ShapeFillStyle ShapeStyle = ShapeFillStyle.Outline;
    public int CornerRadius = 12;
    public int CalloutBorder = 8;
    public int SprayWidth = 16;
    public int FloodTolerance = 10;
    public bool FloodUseColor2;
    public int WandTolerance = 30;
    public Font TextFont = new("Microsoft JhengHei UI", 20f, FontStyle.Regular);
    public bool TextTransparent = true;
    public bool AntialiasShapes = true;
}

/// <summary>繪圖工具基底。所有座標為影像座標（float）。</summary>
public abstract class Tool
{
    public CanvasControl Canvas = null!;
    public virtual string Name => "";
    public virtual string Hint => "選擇工具";
    public virtual Cursor Cursor => Cursors.Cross;
    /// <summary>true 時工具自行處理鍵盤（如文字、多邊形的 Enter/Esc）。</summary>
    public virtual bool WantsKeys => false;

    protected PaintDocument Doc => Canvas.Doc;
    protected SelectionManager Sel => Canvas.Selection;
    protected ToolOptions Opt => Canvas.Options;
    protected Color C1 => Canvas.Color1;
    protected Color C2 => Canvas.Color2;

    public virtual void OnMouseDown(PointF p, MouseButtons btn) { }
    public virtual void OnMouseMove(PointF p, MouseButtons btn) { }
    public virtual void OnMouseUp(PointF p, MouseButtons btn) { }
    public virtual void OnKeyDown(KeyEventArgs e) { }
    public virtual void OnTick() { }
    public virtual void OnDeactivate() { }
    public virtual void OnViewChanged() { }

    /// <summary>在影像座標系繪製預覽（拖曳中、尚未提交的內容）。</summary>
    public virtual void Preview(Graphics g) { }

    /// <summary>建立圖形並依樣式（外框/填色）繪製，回傳涵蓋範圍。</summary>
    protected static Rectangle DrawStyledShape(Graphics g, GraphicsPath path, ShapeFillStyle style,
        Color outline, Color fill, int width, bool antialias)
    {
        var prev = g.SmoothingMode;
        g.SmoothingMode = antialias ? SmoothingMode.AntiAlias : SmoothingMode.None;
        try
        {
            if (style is ShapeFillStyle.Fill or ShapeFillStyle.OutlineFill)
                using (var b = new SolidBrush(fill))
                    g.FillPath(b, path);
            if (style is ShapeFillStyle.Outline or ShapeFillStyle.OutlineFill)
            {
                using var pen = new Pen(outline, Math.Max(1, width))
                { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawPath(pen, path);
            }
        }
        finally { g.SmoothingMode = prev; }
        return Rectangle.Inflate(Rectangle.Round(path.GetBounds()), width + 2, width + 2);
    }

    /// <summary>取得作用中圖層的 Graphics；若有選取則裁剪至選取範圍（與小畫家一致）。</summary>
    protected Graphics OpenActiveLayer()
    {
        var g = Graphics.FromImage(Doc.Active.Image);
        if (Sel.IsActive)
            g.SetClip(Sel.GetRegion(), CombineMode.Replace);
        return g;
    }
}

/// <summary>縮放/捲動畫布：以視埠方式繪製，支援 1%–3200%、中鍵平移、Ctrl+滾輪縮放。</summary>
public sealed class CanvasControl : Control
{
    private readonly HScrollBar _hbar = new();
    private readonly VScrollBar _vbar = new();
    private readonly System.Windows.Forms.Timer _antsTimer;
    private readonly System.Windows.Forms.Timer _sprayTimer;
    private readonly System.Windows.Forms.Timer _edgeTimer;
    private readonly Label _dragInfo = new()
    {
        AutoSize = true,
        Visible = false,
        BackColor = Color.FromArgb(255, 251, 222),
        BorderStyle = BorderStyle.FixedSingle,
        Padding = new Padding(3, 0, 3, 0),
        Font = new Font("Microsoft JhengHei UI", 8.25f),
    };

    private double _zoom = 1.0;
    private double _scrollX, _scrollY;   // 影像像素
    private Point _panStart;
    private double _panScrollX, _panScrollY;
    private bool _panning;
    private bool _spacePan;              // 空白鍵＋左鍵＝平移（無中鍵滑鼠的替代）
    private bool _leftDown;              // 拖曳中靠近視埠邊緣時自動捲動
    private Point _lastMouse;            // 最新滑鼠客戶區座標
    private int _antPhase;
    private Bitmap? _checkerTile;

    public PaintDocument Doc { get; }
    public SelectionManager Selection { get; } = new();
    public ToolOptions Options { get; } = new();
    public Color Color1 = Color.Black;
    public Color Color2 = Color.White;

    private Tool _tool = null!;
    public Tool ActiveTool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            _tool?.OnDeactivate();
            _tool = value;
            value.Canvas = this;
            Cursor = value.Cursor;
            ShowDragInfo(null);
            Invalidate();
            ToolHint?.Invoke(value.Hint);
            ToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double Zoom
    {
        get => _zoom;
        set => SetZoom(value, null);
    }

    public event Action<string>? ToolHint;
    public event EventHandler? ToolChanged;
    public event Action<Point?>? PointerMoved;
    public event EventHandler? ZoomChanged;
    public event Action<int, Color>? ColorChanged;

    /// <summary>取色工具寫入顏色並通知 UI。</summary>
    public void SetColor(int which, Color c)
    {
        if (which == 1) Color1 = c; else Color2 = c;
        ColorChanged?.Invoke(which, c);
        Invalidate();
    }

    public CanvasControl(PaintDocument doc)
    {
        Doc = doc;
        DoubleBuffered = true;
        BackColor = SystemColors.AppWorkspace;
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer, true);

        Controls.Add(_hbar);
        Controls.Add(_vbar);
        _hbar.Scroll += (_, _) => { _scrollX = _hbar.Value; Invalidate(); };
        _vbar.Scroll += (_, _) => { _scrollY = _vbar.Value; Invalidate(); };

        _antsTimer = new System.Windows.Forms.Timer { Interval = 140 };
        _antsTimer.Tick += (_, _) => { _antPhase = (_antPhase + 1) % 8; if (Selection.IsActive) Invalidate(); };
        _antsTimer.Start();

        _sprayTimer = new System.Windows.Forms.Timer { Interval = 60 };
        _sprayTimer.Tick += (_, _) => _tool?.OnTick();

        // 拖曳接近視埠邊緣時自動捲動（繪圖/選取拖出可見範圍）
        _edgeTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _edgeTimer.Tick += (_, _) => EdgeScrollTick();
        _edgeTimer.Start();

        Controls.Add(_dragInfo);

        _checkerTile = BuildCheckerTile();

        Doc.Changed += (_, _) => Invalidate();
        Doc.StructureChanged += (_, _) => { UpdateScrollbars(); Invalidate(); };
        Selection.Changed += (_, _) => Invalidate();

        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += OnUp;
        MouseLeave += (_, _) => PointerMoved?.Invoke(null);
        MouseWheel += OnWheel;
        MouseCaptureChanged += OnCaptureChanged;
    }

    private static Bitmap BuildCheckerTile()
    {
        var tile = new Bitmap(16, 16);
        using var g = Graphics.FromImage(tile);
        g.Clear(Color.Gainsboro);
        using var b = new SolidBrush(Color.White);
        g.FillRectangle(b, 0, 0, 8, 8);
        g.FillRectangle(b, 8, 8, 8, 8);
        return tile;
    }

    // ------------------------------------------------------------ 幾何

    private int BarW => SystemInformation.HorizontalScrollBarHeight;
    private Rectangle Viewport => new(0, 0,
        ClientSize.Width - (_vbar.Visible ? BarW : 0),
        ClientSize.Height - (_hbar.Visible ? BarW : 0));

    public PointF ToImage(Point screen)
    {
        var vp = Viewport;
        var (ox, oy) = ImageOrigin(vp);
        return new PointF((float)((screen.X - ox) / _zoom + _scrollX), (float)((screen.Y - oy) / _zoom + _scrollY));
    }

    public PointF ToScreen(PointF img)
    {
        var vp = Viewport;
        var (ox, oy) = ImageOrigin(vp);
        return new PointF((float)((img.X - _scrollX) * _zoom + ox), (float)((img.Y - _scrollY) * _zoom + oy));
    }

    /// <summary>圖片左上角在螢幕上的位置（小圖置中）。</summary>
    private (double, double) ImageOrigin(Rectangle vp)
    {
        double imgW = Doc.Size.Width * _zoom, imgH = Doc.Size.Height * _zoom;
        double ox = imgW < vp.Width ? (vp.Width - imgW) / 2 : 0;
        double oy = imgH < vp.Height ? (vp.Height - imgH) / 2 : 0;
        return (ox, oy);
    }

    public void SetZoom(double z, PointF? anchorImage)
    {
        z = Math.Clamp(z, 0.01, 32.0);
        if (Math.Abs(z - _zoom) < 1e-9) return;
        var vp = Viewport;
        var (ox, oy) = ImageOrigin(vp);
        // 以滑鼠點為錨點縮放：該點的螢幕位置保持不變
        PointF anchor = anchorImage ?? new PointF((float)(_scrollX + vp.Width / 2 / _zoom), (float)(_scrollY + vp.Height / 2 / _zoom));
        double old = _zoom;
        _zoom = z;
        _scrollX = anchor.X - (anchor.X - _scrollX) * old / z;
        _scrollY = anchor.Y - (anchor.Y - _scrollY) * old / z;
        UpdateScrollbars();
        _tool?.OnViewChanged();
        if (!_panning && !_spacePan) Cursor = _tool?.Cursor ?? Cursors.Default;
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void ZoomIn() => SetZoom(_zoom <= 1 ? _zoom * 2 : _zoom * 1.25, null);
    public void ZoomOut() => SetZoom(_zoom < 1 ? _zoom / 2 : _zoom / 1.25, null);

    public void FitZoom()
    {
        var vp = Viewport;
        double z = Math.Min((vp.Width - 20.0) / Doc.Size.Width, (vp.Height - 20.0) / Doc.Size.Height);
        SetZoom(Math.Min(1, z), null);
    }

    public void ResetZoom() => SetZoom(1, null);

    // ------------------------------------------------------------ 捲動

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateScrollbars();
        Invalidate();   // 尺寸變動（含拖曳調整視窗）時整幅重繪，捲軸隱現不留殘影
    }

    private void UpdateScrollbars()
    {
        var vp = Viewport;
        double needW = Doc.Size.Width * _zoom, needH = Doc.Size.Height * _zoom;
        bool showH = needW > ClientSize.Width, showV = needH > ClientSize.Height;
        if (_hbar.Visible != showH || _vbar.Visible != showV)
        {
            _hbar.Visible = showH;
            _vbar.Visible = showV;
            // 重算（捲軸出現會縮小視埠）
            showH = needW > ClientSize.Width; showV = needH > ClientSize.Height;
        }
        _vbar.Enabled = showV; _hbar.Enabled = showH;

        double visibleW = (_hbar.Visible ? vp.Width : ClientSize.Width) / _zoom;
        double visibleH = (_vbar.Visible ? vp.Height : ClientSize.Height) / _zoom;
        _hbar.Maximum = Math.Max(0, (int)Math.Ceiling(Doc.Size.Width - visibleW)) + _hbar.LargeChange - 1;
        _vbar.Maximum = Math.Max(0, (int)Math.Ceiling(Doc.Size.Height - visibleH)) + _vbar.LargeChange - 1;
        _hbar.LargeChange = Math.Max(1, Math.Min(_hbar.Maximum + 1, Math.Max(1, (int)visibleW / 2)));
        _vbar.LargeChange = Math.Max(1, Math.Min(_vbar.Maximum + 1, Math.Max(1, (int)visibleH / 2)));
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, Doc.Size.Width - visibleW));
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, Doc.Size.Height - visibleH));
        _hbar.Value = (int)_scrollX;
        _vbar.Value = (int)_scrollY;

        var layout = ClientRectangle;
        // 兩條捲軸都鋪滿整邊，角落重疊處由 z 序覆蓋，避免右下角露出未繪製缺口
        _vbar.Bounds = new Rectangle(layout.Right - BarW, 0, BarW, layout.Height);
        _hbar.Bounds = new Rectangle(0, layout.Bottom - BarW, layout.Width, BarW);
    }

    private void CenterView()
    {
        var vp = Viewport;
        _scrollX = Math.Max(0, (Doc.Size.Width - vp.Width / _zoom) / 2);
        _scrollY = Math.Max(0, (Doc.Size.Height - vp.Height / _zoom) / 2);
        UpdateScrollbars();
        Invalidate();
    }

    // ------------------------------------------------------------ 滑鼠

    private void OnDown(object? sender, MouseEventArgs e)
    {
        Focus();
        _lastMouse = e.Location;
        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && _spacePan))
        {
            _panning = true;
            _panStart = e.Location;
            _panScrollX = _scrollX; _panScrollY = _scrollY;
            Cursor = Cursors.SizeAll;
            Capture = true;
            return;
        }
        var p = ToImage(e.Location);
        if (e.Button == MouseButtons.Left)
        {
            _sprayTimer.Enabled = false;
            _leftDown = true;
            Capture = true; // 擷取滑鼠：放開在畫布外也能收到 Up，工具不會卡住
            _tool.OnMouseDown(p, e.Button);
        }
        else if (e.Button == MouseButtons.Right)
        {
            Capture = true;
            _tool.OnMouseDown(p, e.Button);
        }
    }

    private void OnMove(object? sender, MouseEventArgs e)
    {
        _lastMouse = e.Location;
        if (_panning)
        {
            _scrollX = Math.Max(0, _panScrollX - (e.X - _panStart.X) / _zoom);
            _scrollY = Math.Max(0, _panScrollY - (e.Y - _panStart.Y) / _zoom);
            UpdateScrollbars();
            Invalidate();
            return;
        }
        var p = ToImage(e.Location);
        PointerMoved?.Invoke(new Point((int)Math.Floor(p.X), (int)Math.Floor(p.Y)));
        // 游標即時跟隨工具粗細/縮放（Cursor setter 內部會比較相等性，可安全重複指定）
        if (!_panning && !_spacePan) Cursor = _tool.Cursor;
        _tool.OnMouseMove(p, e.Button); // 工具可在此覆寫游標（如選取把手的方向箭頭）
        if (_dragInfo.Visible) PositionDragInfo();
    }

    private void OnUp(object? sender, MouseEventArgs e)
    {
        if (_panning && e.Button is MouseButtons.Middle or MouseButtons.Left)
        {
            _panning = false;
            Capture = false;
            Cursor = _spacePan ? Cursors.SizeAll : ActiveTool.Cursor;
            return;
        }
        if (e.Button == MouseButtons.Left) _leftDown = false;
        if (!Capture && !_leftDown) return;
        Capture = false;
        var p = ToImage(e.Location);
        _tool.OnMouseUp(p, e.Button);
        ShowDragInfo(null);
    }

    /// <summary>擷取被外部搶走（如 Alt+Tab）：補發 Up，避免工具卡在拖曳狀態。</summary>
    private void OnCaptureChanged(object? sender, EventArgs e)
    {
        if (Capture || (!_leftDown && !_panning)) return;
        _panning = false;
        if (_leftDown)
        {
            _leftDown = false;
            _tool.OnMouseUp(ToImage(_lastMouse), MouseButtons.Left);
            ShowDragInfo(null);
        }
        Cursor = ActiveTool.Cursor;
    }

    /// <summary>拖曳中滑鼠靠近視埠邊緣 → 定時朝該方向捲動（小畫家行為）。</summary>
    private void EdgeScrollTick()
    {
        if (!_leftDown || _panning) return;
        bool canX = Doc.Size.Width * _zoom > ClientSize.Width;
        bool canY = Doc.Size.Height * _zoom > ClientSize.Height;
        if (!canX && !canY) return;

        const int margin = 36;
        double speed = Math.Max(1.0, 16 / _zoom); // 每 tick 約 16 螢幕像素
        double dx = 0, dy = 0;
        if (_lastMouse.X < margin) dx = -speed;
        else if (_lastMouse.X > ClientSize.Width - margin) dx = speed;
        if (_lastMouse.Y < margin) dy = -speed;
        else if (_lastMouse.Y > ClientSize.Height - margin) dy = speed;
        if (dx == 0 && dy == 0) return;

        _scrollX += canX ? dx : 0;
        _scrollY += canY ? dy : 0;
        UpdateScrollbars();
        Invalidate();
    }

    /// <summary>在滑鼠旁顯示拖曳即時資訊（尺寸/位置）；null 隱藏。</summary>
    public void ShowDragInfo(string? text)
    {
        if (text is null) { _dragInfo.Visible = false; return; }
        if (_dragInfo.Text != text) _dragInfo.Text = text;
        if (!_dragInfo.Visible) { _dragInfo.Visible = true; _dragInfo.BringToFront(); }
        PositionDragInfo();
    }

    private void PositionDragInfo()
    {
        int x = Math.Min(_lastMouse.X + 16, ClientSize.Width - _dragInfo.Width - 4);
        int y = Math.Min(_lastMouse.Y + 20, ClientSize.Height - _dragInfo.Height - 4);
        _dragInfo.Location = new Point(Math.Max(2, x), Math.Max(2, y));
    }

    private void OnWheel(object? sender, MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0)
        {
            var anchor = ToImage(e.Location);
            double z = e.Delta > 0 ? _zoom * 1.2 : _zoom / 1.2;
            SetZoom(z, anchor);
        }
        else
        {
            double delta = e.Delta > 0 ? -3 : 3;
            if ((ModifierKeys & Keys.Shift) != 0)
                _scrollX = Math.Max(0, _scrollX + delta * 10);
            else
                _scrollY = Math.Max(0, _scrollY + delta * 10);
            UpdateScrollbars();
            Invalidate();
        }
    }

    public void StartSprayTimer() => _sprayTimer.Enabled = true;
    public void StopSprayTimer() => _sprayTimer.Enabled = false;

    /// <summary>提交文字工具的待確定編輯（undo/redo 前呼叫）。</summary>
    public void CommitActiveText() => (ActiveTool as Tools.TextTool)?.CommitPending();

    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && !_spacePan)
        {
            _spacePan = true;
            if (!_panning) Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }
        if (_tool.WantsKeys) _tool.OnKeyDown(e);
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            _spacePan = false;
            if (!_panning) Cursor = ActiveTool.Cursor;
            e.Handled = true;
            return;
        }
        base.OnKeyUp(e);
    }

    // ------------------------------------------------------------ 繪製

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var vp = Viewport;
        g.Clear(BackColor);
        if (Doc.Layers.Count == 0) return;

        var (ox, oy) = ImageOrigin(vp);
        float imgScreenW = (float)(Doc.Size.Width * _zoom);
        float imgScreenH = (float)(Doc.Size.Height * _zoom);
        var imgRect = new RectangleF((float)ox, (float)oy, imgScreenW, imgScreenH);

        g.InterpolationMode = _zoom > 1.5 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        // 透明區 checkerboard（優化：原版以洋紅表示透明）
        if (_checkerTile is not null)
        {
            float factor = (float)Math.Max(0.25, _zoom > 4 ? 2 : 1);
            using var tb = new TextureBrush(_checkerTile, System.Drawing.Drawing2D.WrapMode.Tile);
            tb.ScaleTransform(factor, factor);
            tb.TranslateTransform((float)(ox % (16 * factor)), (float)(oy % (16 * factor)));
            g.FillRectangle(tb, Rectangle.Round(imgRect));
        }

        // 逐層直接合成（不使用 Doc.Compose() 快取：筆刷拖曳直接改圖層位圖、不會失效快取，
        // 會造成「放開滑鼠才看到線條」與縮放視窗時的殘影）
        foreach (var l in Doc.Layers)
            if (l.Visible) g.DrawImage(l.Image, imgRect);

        // 紙緣
        using (var border = new Pen(Color.Gray))
            g.DrawRectangle(border, Rectangle.Round(imgRect));

        // 工具預覽（影像座標系）
        var state = g.Save();
        using (var m = new Matrix())
        {
            m.Scale((float)_zoom, (float)_zoom);
            m.Translate((float)(ox - _scrollX * _zoom), (float)(oy - _scrollY * _zoom));
            g.MultiplyTransform(m);
            g.InterpolationMode = _zoom > 1.5 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
            try { _tool?.Preview(g); }
            finally { g.Restore(state); }
        }

        DrawSelectionAnts(g);
        if (Options is not null && ShowGrid && _zoom >= 4)
            DrawGrid(g, vp, ox, oy);
    }

    public bool ShowGrid { get; set; }

    private void DrawGrid(Graphics g, Rectangle vp, double ox, double oy)
    {
        using var pen = new Pen(Color.FromArgb(70, 60, 60, 60));
        int x0 = (int)Math.Floor(_scrollX), x1 = (int)Math.Ceiling(_scrollX + vp.Width / _zoom);
        int y0 = (int)Math.Floor(_scrollY), y1 = (int)Math.Ceiling(_scrollY + vp.Height / _zoom);
        if ((x1 - x0) > 3000 || (y1 - y0) > 3000) return;
        for (int x = x0; x <= x1; x++)
        {
            float sx = (float)((x - _scrollX) * _zoom + ox);
            g.DrawLine(pen, sx, 0, sx, vp.Height);
        }
        for (int y = y0; y <= y1; y++)
        {
            float sy = (float)((y - _scrollY) * _zoom + oy);
            g.DrawLine(pen, 0, sy, vp.Width, sy);
        }
    }

    private void DrawSelectionAnts(Graphics g)
    {
        if (!Selection.IsActive) return;
        float w = Math.Max(1f, (float)_zoom);
        int phase = _antPhase;

        if (Selection.Mode == SelectionManager.SelMode.Mask)
        {
            foreach (var px in Selection.GetBoundaryPixels())
            {
                var s = ToScreen(px);
                bool black = ((px.X + px.Y + phase) % 8) < 4;
                using var b = new SolidBrush(black ? Color.Black : Color.White);
                g.FillRectangle(b, s.X, s.Y, w, w);
            }
            return;
        }

        using var path = Selection.Mode == SelectionManager.SelMode.Lasso
            ? (GraphicsPath)Selection.LassoPath!.Clone()
            : null;
        GraphicsPath target;
        if (path is not null)
        {
            target = path;
            var baseOrigin = ToScreen(PointF.Empty);
            using var mt = new Matrix();
            mt.Scale((float)_zoom, (float)_zoom);
            mt.Translate(baseOrigin.X, baseOrigin.Y);
            target.Transform(mt);
        }
        else
        {
            var r = Selection.Bounds;
            var s0 = ToScreen(new PointF(r.X, r.Y));
            target = new GraphicsPath();
            target.AddRectangle(new RectangleF(s0.X, s0.Y, (float)(r.Width * _zoom), (float)(r.Height * _zoom)));
        }

        using (target)
        {
            foreach (var (color, off) in new (Color, float)[] { (Color.Black, phase), (Color.White, phase + 4) })
            {
                using var pen = new Pen(color, 1)
                {
                    DashPattern = new float[] { 4, 4 },
                    DashOffset = off,
                };
                g.DrawPath(pen, target);
            }
        }
    }
}
