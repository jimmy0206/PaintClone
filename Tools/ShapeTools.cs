using System.Drawing.Drawing2D;
using PaintClone.Core;
using PaintClone.Rendering;

namespace PaintClone.Tools;

/// <summary>拖曳成型工具基底：拖曳中以快照疊加即時預覽（優化：原版預覽較粗糙），放開才寫入。</summary>
public abstract class ShapeToolBase : Tool
{
    protected Bitmap? _base;
    protected EditScopeHandle? Scope;
    protected PointF _p0, _cur;
    protected Color _outline, _fill;
    protected bool _dragging;

    protected bool Right { get; private set; }

    protected void BeginShape(PointF p, MouseButtons btn)
    {
        Right = btn == MouseButtons.Right;
        _outline = Right ? C2 : C1;
        _fill = Right ? C1 : C2;
        _p0 = _cur = p;
        _dragging = true;
        Scope = new EditScopeHandle(Doc.BeginEdit());
        _base?.Dispose();
        _base = Doc.Active.Image.ToArgb32();
    }

    public override void OnMouseDown(PointF p, MouseButtons btn)
    {
        if (btn is not (MouseButtons.Left or MouseButtons.Right)) return;
        BeginShape(p, btn);
    }

    public override void OnMouseMove(PointF p, MouseButtons btn)
    {
        if (!_dragging) return;
        _cur = p;
        Canvas.Invalidate();
    }

    public override void OnMouseUp(PointF p, MouseButtons btn)
    {
        if (!_dragging) return;
        _cur = p;
        _dragging = false;
        CommitShape();
    }

    protected virtual void CommitShape()
    {
        if (Scope is null || _base is null) return;
        var path = BuildPath(Normalize(_p0, _cur));
        Rectangle dirty;
        using (var g = OpenActiveLayer())
        {
            if (path.PointCount == 0)
            {
                dirty = Rectangle.Empty;
            }
            else
            {
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                dirty = DrawStyledShape(g, path, Opt.ShapeStyle, _outline, _fill, Opt.ShapeWidth, UseAntialias());
            }
        }
        path.Dispose();
        if (dirty.Width > 0) { Scope.AddDirty(dirty); Scope.Commit(); }
        else Scope.Abort();
        Scope = null;
        _base?.Dispose(); _base = null;
        Canvas.Invalidate();
    }

    /// <summary>以規範化的矩形（拖曳起訖點）組出路徑。</summary>
    protected abstract GraphicsPath BuildPath(RectangleF rect);

    protected virtual bool UseAntialias() => Opt.AntialiasShapes && Opt.ShapeWidth > 1;

    public override void Preview(Graphics g)
    {
        if (_base is null) return;
        var rect = Normalize(_p0, _cur);
        rect.Inflate(Opt.ShapeWidth + 4, Opt.ShapeWidth + 4);
        var clip = Rectangle.Intersect(Rectangle.Round(rect), new Rectangle(0, 0, Doc.Size.Width, Doc.Size.Height));
        if (clip.Width < 1 || clip.Height < 1) return;
        using (var state = new ClipSaver(g))
        {
            g.SetClip(clip, CombineMode.Replace);
            g.DrawImage(_base, clip, clip, GraphicsUnit.Pixel);
            using var path = BuildPath(rect);
            if (path.PointCount > 0)
                DrawStyledShape(g, path, Opt.ShapeStyle, _outline, _fill, Opt.ShapeWidth, UseAntialias());
        }
    }

    public override void OnDeactivate()
    {
        if (Scope is not null) { Scope.Abort(); Scope = null; }
        _base?.Dispose(); _base = null;
        _dragging = false;
        Canvas.Invalidate();
    }

    public override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && (_dragging || Scope is not null))
        {
            OnDeactivate();
            e.Handled = true;
        }
    }

    public override bool WantsKeys => true;

    protected static RectangleF Normalize(PointF a, PointF b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    protected static bool SquareConstraint => (Control.ModifierKeys & (Keys.Shift | Keys.Control)) != 0;
}

internal readonly struct ClipSaver : IDisposable
{
    private readonly Graphics _g;
    private readonly GraphicsState _state;
    public ClipSaver(Graphics g) { _g = g; _state = g.Save(); }
    public void Dispose() => _g.Restore(_state);
}

public sealed class LineTool : ShapeToolBase
{
    public override string Name => "直線";
    public override string Hint => "直線：按住 Shift 可鎖 15° 倍角";

    protected override void CommitShape()
    {
        if (Scope is null || _base is null) return;
        var (a, b) = Constrain(_p0, _cur);
        Rectangle dirty;
        using (var g = OpenActiveLayer())
        {
            g.SmoothingMode = UseAntialias() ? SmoothingMode.AntiAlias : SmoothingMode.None;
            using var pen = new Pen(_outline, Math.Max(1, Opt.ShapeWidth)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, a, b);
            dirty = BoundsOf(a, b, Opt.ShapeWidth);
        }
        Scope.AddDirty(dirty);
        Scope.Commit();
        Scope = null;
        _base?.Dispose(); _base = null;
        Canvas.Invalidate();
    }

    public override void Preview(Graphics g)
    {
        if (_base is null) return;
        var (a, b) = Constrain(_p0, _cur);
        var clip = Rectangle.Intersect(Rectangle.Round(BoundsOf(a, b, Opt.ShapeWidth + 2)),
            new Rectangle(0, 0, Doc.Size.Width, Doc.Size.Height));
        if (clip.Width < 1 || clip.Height < 1) return;
        using var state = new ClipSaver(g);
        g.SetClip(clip, CombineMode.Replace);
        g.DrawImage(_base, clip, clip, GraphicsUnit.Pixel);
        g.SmoothingMode = UseAntialias() ? SmoothingMode.AntiAlias : SmoothingMode.None;
        using var pen = new Pen(_outline, Math.Max(1, Opt.ShapeWidth)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, a, b);
    }

    private static (PointF, PointF) Constrain(PointF a, PointF b)
    {
        if (!SquareConstraint) return (a, b);
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 12)) * (Math.PI / 12);
        double len = Math.Sqrt(dx * dx + dy * dy);
        return (a, new PointF((float)(a.X + Math.Cos(ang) * len), (float)(a.Y + Math.Sin(ang) * len)));
    }

    private static Rectangle BoundsOf(PointF a, PointF b, int w) => Rectangle.Inflate(
        Rectangle.Round(new RectangleF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y))), w, w);

    protected override GraphicsPath BuildPath(RectangleF rect) => new();
}

public sealed class CurveTool : Tool
{
    private enum Stage { Idle, DragEnd, AdjustBend }
    private Stage _stage;
    private PointF _p0, _p1, _bend;
    private Bitmap? _base;
    private EditScopeHandle? _scope;
    private Color _outline;

    public override string Name => "曲線";
    public override string Hint => "曲線：點第一下取起點→第二下取終點→拖曳彎曲後再點一下完成";
    public override bool WantsKeys => true;

    public override void OnMouseDown(PointF p, MouseButtons btn)
    {
        if (btn is not (MouseButtons.Left or MouseButtons.Right)) return;
        switch (_stage)
        {
            case Stage.Idle:
                _outline = btn == MouseButtons.Left ? C1 : C2;
                _scope = new EditScopeHandle(Doc.BeginEdit());
                _base = Doc.Active.Image.ToArgb32();
                _p0 = _p1 = _bend = p;
                _stage = Stage.DragEnd;
                break;
            case Stage.DragEnd:
                _p1 = p;
                _stage = Stage.AdjustBend;
                break;
            case Stage.AdjustBend:
                Commit();
                break;
        }
    }

    public override void OnMouseMove(PointF p, MouseButtons btn)
    {
        if (_stage == Stage.DragEnd) _p1 = p;
        else if (_stage == Stage.AdjustBend) _bend = p;
        else return;
        Canvas.Invalidate();
    }

    public override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _stage != Stage.Idle) { Abort(); e.Handled = true; }
    }

    public override void OnDeactivate() => Abort();

    private void Abort()
    {
        _scope?.Abort(); _scope = null;
        _base?.Dispose(); _base = null;
        _stage = Stage.Idle;
        Canvas.Invalidate();
    }

    private GraphicsPath BuildCurve()
    {
        var d = new PointF(_bend.X - _p1.X, _bend.Y - _p1.Y);
        var path = new GraphicsPath();
        path.AddBezier(_p0,
            new PointF(_p0.X + d.X, _p0.Y + d.Y),
            new PointF(_p1.X - d.X, _p1.Y - d.Y),
            _p1);
        return path;
    }

    private void Commit()
    {
        if (_scope is null || _base is null) return;
        Rectangle dirty;
        using (var g = OpenActiveLayer())
        {
            dirty = Rectangle.Empty;
            if (_stage == Stage.AdjustBend)
            {
                using var path = BuildCurve();
                dirty = DrawStyledOutline(g, path);
            }
        }
        if (dirty.Width > 0) { _scope.AddDirty(dirty); _scope.Commit(); }
        else _scope.Abort();
        _scope = null;
        _base?.Dispose(); _base = null;
        _stage = Stage.Idle;
        Canvas.Invalidate();
    }

    private Rectangle DrawStyledOutline(Graphics g, GraphicsPath path)
    {
        var prev = g.SmoothingMode;
        g.SmoothingMode = Opt.AntialiasShapes ? SmoothingMode.AntiAlias : SmoothingMode.None;
        using var pen = new Pen(_outline, Math.Max(1, Opt.ShapeWidth)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawPath(pen, path);
        g.SmoothingMode = prev;
        return Rectangle.Inflate(Rectangle.Round(path.GetBounds()), Opt.ShapeWidth + 2, Opt.ShapeWidth + 2);
    }

    public override void Preview(Graphics g)
    {
        if (_base is null || _stage == Stage.Idle) return;
        using var path = _stage == Stage.DragEnd
            ? LinePath(_p0, _p1)
            : BuildCurve();
        var clip = Rectangle.Inflate(Rectangle.Round(path.GetBounds()), Opt.ShapeWidth + 3, Opt.ShapeWidth + 3);
        clip = Rectangle.Intersect(clip, new Rectangle(0, 0, Doc.Size.Width, Doc.Size.Height));
        if (clip.Width < 1 || clip.Height < 1) return;
        using var state = new ClipSaver(g);
        g.SetClip(clip, CombineMode.Replace);
        g.DrawImage(_base, clip, clip, GraphicsUnit.Pixel);
        using var pen = new Pen(_outline, Math.Max(1, Opt.ShapeWidth)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.SmoothingMode = Opt.AntialiasShapes ? SmoothingMode.AntiAlias : SmoothingMode.None;
        g.DrawPath(pen, path);
    }

    private static GraphicsPath LinePath(PointF a, PointF b)
    {
        var p = new GraphicsPath();
        p.AddLine(a, b);
        return p;
    }
}

public sealed class RectangleTool : ShapeToolBase
{
    public override string Name => "矩形";
    public override string Hint => "矩形：按住 Shift 畫正方形";

    protected override GraphicsPath BuildPath(RectangleF rect)
    {
        if (SquareConstraint)
            rect = MakeSquare(rect, _p0);
        var path = new GraphicsPath();
        if (rect.Width < 1 && rect.Height < 1) return path;
        path.AddRectangle(rect);
        return path;
    }

    internal static RectangleF MakeSquare(RectangleF r, PointF anchor)
    {
        float side = Math.Max(r.Width, r.Height);
        float x = anchor.X <= r.X ? r.X : anchor.X - side;
        float y = anchor.Y <= r.Y ? r.Y : anchor.Y - side;
        return new RectangleF(x, y, side, side);
    }
}

public sealed class RoundedRectangleTool : ShapeToolBase
{
    public override string Name => "圓角矩形";
    public override string Hint => "圓角矩形：半徑可在「選項」調整";

    protected override GraphicsPath BuildPath(RectangleF rect)
    {
        if (SquareConstraint) rect = RectangleTool.MakeSquare(rect, _p0);
        var path = new GraphicsPath();
        AddRoundedRect(path, rect, Opt.CornerRadius);
        return path;
    }

    public static void AddRoundedRect(GraphicsPath path, RectangleF r, float radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
        if (r.Width < 1 || r.Height < 1) return;
        if (radius <= 0.5f) { path.AddRectangle(r); return; }
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
    }
}

public sealed class EllipseTool : ShapeToolBase
{
    public override string Name => "橢圓";
    public override string Hint => "橢圓：按住 Shift 畫正圓";

    protected override GraphicsPath BuildPath(RectangleF rect)
    {
        if (SquareConstraint) rect = RectangleTool.MakeSquare(rect, _p0);
        var path = new GraphicsPath();
        if (rect.Width < 1 || rect.Height < 1) return path;
        path.AddEllipse(rect);
        return path;
    }
}

/// <summary>圓角標註框（對應原版 RoundedRectangularCallout）：拖出框後移動尾端，再點一下完成。</summary>
public sealed class CalloutTool : ShapeToolBase
{
    private bool _tailPhase;

    protected override GraphicsPath BuildPath(RectangleF rect) => new(); // 自訂 Commit/Preview 流程

    public override string Name => "標註框";
    public override string Hint => "標註框：拖出圓角框後放開，移動滑鼠決定尾端，再點一下完成";

    public override void OnMouseDown(PointF p, MouseButtons btn)
    {
        if (_tailPhase)
        {
            _cur = p;
            _tailPhase = false;
            CommitShape();
            return;
        }
        if (btn is not (MouseButtons.Left or MouseButtons.Right)) return;
        BeginShape(p, btn);
    }

    public override void OnMouseUp(PointF p, MouseButtons btn)
    {
        if (!_dragging) return;
        _cur = p;
        _firstUp = p;
        _dragging = false;
        _tailPhase = true; // 進入尾端定位階段
        Canvas.Invalidate();
    }

    public override void OnMouseMove(PointF p, MouseButtons btn)
    {
        if (_dragging || _tailPhase)
        {
            _cur = p;
            Canvas.Invalidate();
        }
    }

    public override void OnDeactivate()
    {
        _tailPhase = false;
        base.OnDeactivate();
    }

    protected override void CommitShape()
    {
        if (Scope is null || _base is null) return;
        var rect = Normalize(_p0, _firstUp ?? _cur);
        Rectangle dirty;
        using (var g = OpenActiveLayer())
        {
            g.SmoothingMode = Opt.AntialiasShapes ? SmoothingMode.AntiAlias : SmoothingMode.None;
            dirty = DrawCallout(g, rect, _cur);
        }
        if (dirty.Width > 0 && rect.Width >= 2 && rect.Height >= 2) { Scope.AddDirty(dirty); Scope.Commit(); }
        else Scope.Abort();
        Scope = null;
        _base?.Dispose(); _base = null;
        _firstUp = null;
        Canvas.Invalidate();
    }

    private PointF? _firstUp;

    public override void Preview(Graphics g)
    {
        if (_base is null) return;
        var rect = Normalize(_p0, _firstUp ?? _cur);
        var big = RectangleF.Union(rect, new RectangleF(Math.Min(_cur.X, rect.X), Math.Min(_cur.Y, rect.Y), 0, 0));
        big.Inflate(Opt.CalloutBorder + 6, Opt.CalloutBorder + 6);
        var clip = Rectangle.Intersect(Rectangle.Round(big), new Rectangle(0, 0, Doc.Size.Width, Doc.Size.Height));
        if (clip.Width < 1 || clip.Height < 1) return;
        using var state = new ClipSaver(g);
        g.SetClip(clip, CombineMode.Replace);
        g.DrawImage(_base, clip, clip, GraphicsUnit.Pixel);
        g.SmoothingMode = Opt.AntialiasShapes ? SmoothingMode.AntiAlias : SmoothingMode.None;
        DrawCallout(g, rect, _cur, previewOnly: true);
    }

    private Rectangle DrawCallout(Graphics g, RectangleF rect, PointF tail, bool previewOnly = false)
    {
        if (rect.Width < 2 || rect.Height < 2) return Rectangle.Empty;
        int b = Math.Max(1, Opt.CalloutBorder);
        using var outer = new GraphicsPath();
        RoundedRectangleTool.AddRoundedRect(outer, rect, Opt.CornerRadius);

        using var pen = new Pen(_outline, b) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        SolidBrush? brush = Opt.ShapeStyle is ShapeFillStyle.Fill or ShapeFillStyle.OutlineFill ? new SolidBrush(_fill) : null;

        // 尾端三角形
        PointF anchor = ClampToRect(rect, tail, out PointF dir);
        PointF perp = new(-dir.Y, dir.X);
        PointF b1 = new(anchor.X + perp.X * b * 1.1f, anchor.Y + perp.Y * b * 1.1f);
        PointF b2 = new(anchor.X - perp.X * b * 1.1f, anchor.Y - perp.Y * b * 1.1f);
        using var tailPath = new GraphicsPath();
        tailPath.AddPolygon(new[] { b1, tail, b2 });

        if (brush is not null && Opt.ShapeStyle != ShapeFillStyle.Outline)
        {
            g.FillPath(brush, outer);
            g.FillPath(brush, tailPath);
        }
        if (Opt.ShapeStyle != ShapeFillStyle.Fill || previewOnly)
        {
            if (Opt.ShapeStyle != ShapeFillStyle.Fill)
            {
                g.DrawPath(pen, outer);
                g.DrawLines(pen, new[] { b1, tail, b2 });
            }
        }
        brush?.Dispose();
        return Rectangle.Inflate(Rectangle.Round(RectangleF.Union(rect,
            new RectangleF(Math.Min(tail.X, rect.X), Math.Min(tail.Y, rect.Y), 0, 0))), b + 4, b + 4);
    }

    private static PointF ClampToRect(RectangleF r, PointF t, out PointF dir)
    {
        PointF c = new(r.X + r.Width / 2, r.Y + r.Height / 2);
        dir = new PointF(t.X - c.X, t.Y - c.Y);
        float len = Math.Max(0.001f, (float)Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y));
        dir = new PointF(dir.X / len, dir.Y / len);
        // 從中心沿方向求與矩形邊界的交點
        float tx = dir.X == 0 ? float.MaxValue : (dir.X > 0 ? (r.Right - c.X) / dir.X : (r.X - c.X) / dir.X);
        float ty = dir.Y == 0 ? float.MaxValue : (dir.Y > 0 ? (r.Bottom - c.Y) / dir.Y : (r.Y - c.Y) / dir.Y);
        float tt = Math.Min(tx, ty);
        return new PointF(c.X + dir.X * tt, c.Y + dir.Y * tt);
    }
}

public sealed class PolygonTool : ShapeToolBase
{
    private readonly List<PointF> _points = new();

    protected override GraphicsPath BuildPath(RectangleF rect) => new(); // 點選制，自訂 Commit/Preview 流程

    public override string Name => "多邊形";
    public override string Hint => "多邊形：逐點點選，右鍵或雙擊閉合完成，Esc 取消";

    public override void OnMouseDown(PointF p, MouseButtons btn)
    {
        if (btn == MouseButtons.Right)
        {
            if (_points.Count >= 2) CommitPolygon();
            return;
        }
        if (btn != MouseButtons.Left) return;

        // 與上一點極接近視為雙擊 → 閉合
        if (_points.Count >= 2 &&
            Math.Abs(p.X - _points[^1].X) < 4 / Math.Max(0.2, Canvas.Zoom) + 1 &&
            Math.Abs(p.Y - _points[^1].Y) < 4 / Math.Max(0.2, Canvas.Zoom) + 1)
        {
            CommitPolygon();
            return;
        }

        if (_points.Count == 0)
        {
            _outline = C1; _fill = C2;
            Scope = new EditScopeHandle(Doc.BeginEdit());
            _base = Doc.Active.Image.ToArgb32();
        }
        _points.Add(p);
        Canvas.Invalidate();
    }

    private void CommitPolygon()
    {
        if (_points.Count < 2) { Cleanup(); return; }
        if (Scope is null || _base is null) { _points.Clear(); return; }
        var path = new GraphicsPath();
        path.AddPolygon(_points.ToArray());
        Rectangle dirty;
        using (var g = OpenActiveLayer())
            dirty = DrawStyledShape(g, path, Opt.ShapeStyle, _outline, _fill, Opt.ShapeWidth, Opt.AntialiasShapes);
        path.Dispose();
        if (dirty.Width > 0) { Scope.AddDirty(dirty); Scope.Commit(); }
        else Scope.Abort();
        Scope = null;
        _base?.Dispose(); _base = null;
        _points.Clear();
        Canvas.Invalidate();
    }

    private void Cleanup()
    {
        Scope?.Abort(); Scope = null;
        _base?.Dispose(); _base = null;
        _points.Clear();
        Canvas.Invalidate();
    }

    public override void OnMouseMove(PointF p, MouseButtons btn)
    {
        if (_points.Count == 0) return;
        _cur = p;
        Canvas.Invalidate();
    }

    public override void OnMouseUp(PointF p, MouseButtons btn) { /* 點選制 */ }

    public override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Cleanup(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter && _points.Count >= 2) { CommitPolygon(); e.Handled = true; }
    }

    public override void OnDeactivate() => Cleanup();

    public override void Preview(Graphics g)
    {
        if (_base is null || _points.Count == 0) return;
        var all = new List<PointF>(_points) { _cur };
        var bb = BoundsOf(all);
        bb.Inflate(Opt.ShapeWidth + 3, Opt.ShapeWidth + 3);
        var clip = Rectangle.Intersect(bb, new Rectangle(0, 0, Doc.Size.Width, Doc.Size.Height));
        if (clip.Width < 1 || clip.Height < 1) return;
        using var state = new ClipSaver(g);
        g.SetClip(clip, CombineMode.Replace);
        g.DrawImage(_base, clip, clip, GraphicsUnit.Pixel);
        g.SmoothingMode = Opt.AntialiasShapes ? SmoothingMode.AntiAlias : SmoothingMode.None;
        using var pen = new Pen(_outline, Math.Max(1, Opt.ShapeWidth)) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, all.ToArray());
        if (_points.Count >= 2)
        {
            using var dash = new Pen(Color.Gray, 1) { DashPattern = new float[] { 3, 3 } };
            g.DrawLine(dash, _cur, _points[0]);
        }
        // 頂點小方塊
        using var vb = new SolidBrush(Color.White);
        using var vp = new Pen(Color.Black, 1);
        float r = Math.Max(2, 3 / (float)Math.Max(0.2, Canvas.Zoom));
        foreach (var pt in _points)
        {
            var rr = new RectangleF(pt.X - r / 2, pt.Y - r / 2, r, r);
            g.FillRectangle(vb, rr.X, rr.Y, rr.Width, rr.Height);
            g.DrawRectangle(vp, rr.X, rr.Y, rr.Width, rr.Height);
        }
    }

    private static Rectangle BoundsOf(List<PointF> pts)
    {
        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        return Rectangle.Round(new RectangleF(minX, minY, maxX - minX, maxY - minY));
    }
}
