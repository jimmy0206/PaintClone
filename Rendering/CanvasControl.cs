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

    private double _zoom = 1.0;
    private double _scrollX, _scrollY;   // 影像像素
    private Point _panStart;
    private double _panScrollX, _panScrollY;
    private bool _panning;
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
            Invalidate();
            ToolHint?.Invoke(value.Hint);
        }
    }

    public double Zoom
    {
        get => _zoom;
        set => SetZoom(value, null);
    }

    public event Action<string>? ToolHint;
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

        _checkerTile = BuildCheckerTile();

        Doc.Changed += (_, _) => Invalidate();
        Doc.StructureChanged += (_, _) => { UpdateScrollbars(); Invalidate(); };
        Selection.Changed += (_, _) => Invalidate();

        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += OnUp;
        MouseLeave += (_, _) => PointerMoved?.Invoke(null);
        MouseWheel += OnWheel;
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
        _vbar.Bounds = new Rectangle(layout.Right - BarW, 0, BarW, layout.Height - (_hbar.Visible ? BarW : 0));
        _hbar.Bounds = new Rectangle(0, layout.Bottom - BarW, layout.Width - (_vbar.Visible ? BarW : 0), BarW);
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
        if (e.Button == MouseButtons.Middle)
        {
            _panning = true;
            _panStart = e.Location;
            _panScrollX = _scrollX; _panScrollY = _scrollY;
            Cursor = Cursors.SizeAll;
            return;
        }
        var p = ToImage(e.Location);
        if (e.Button == MouseButtons.Left)
        {
            _sprayTimer.Enabled = false;
            _tool.OnMouseDown(p, e.Button);
        }
        else if (e.Button == MouseButtons.Right)
        {
            _tool.OnMouseDown(p, e.Button);
        }
    }

    private void OnMove(object? sender, MouseEventArgs e)
    {
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
        _tool.OnMouseMove(p, e.Button);
    }

    private void OnUp(object? sender, MouseEventArgs e)
    {
        if (_panning && e.Button == MouseButtons.Middle)
        {
            _panning = false;
            Cursor = ActiveTool.Cursor;
            return;
        }
        var p = ToImage(e.Location);
        _tool.OnMouseUp(p, e.Button);
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_tool.WantsKeys) _tool.OnKeyDown(e);
        base.OnKeyDown(e);
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

        g.DrawImage(Doc.Compose(), imgRect);

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
            try { _tool.Preview(g); }
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
        bool owned;
        if (path is not null)
        {
            target = path;
            owned = true;
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
            owned = true;
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
