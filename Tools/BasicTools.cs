using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PaintClone.Core;
using PaintClone.Rendering;

namespace PaintClone.Tools;

/// <summary>自由_hand_ 筆劃工具基底：拖曳即直接繪入圖層，放開提交 undo。</summary>
public abstract class StrokeTool : Tool
{
    protected EditScopeHandle? Scope;
    protected PointF _last;
    protected Color _color;

    public override void OnMouseDown(PointF p, MouseButtons btn)
    {
        if (btn is not (MouseButtons.Left or MouseButtons.Right)) return;
        _color = btn == MouseButtons.Left ? C1 : C2;
        Scope = new EditScopeHandle(Doc.BeginEdit());
        _last = p;
        DrawDot(p);
        Canvas.Invalidate();
    }

    public override void OnMouseMove(PointF p, MouseButtons btn)
    {
        if (Scope is null) return;
        DrawSegment(_last, p);
        InvalidateSegment(_last, p);
        _last = p;
    }

    public override void OnMouseUp(PointF p, MouseButtons btn)
    {
        if (Scope is null) return;
        DrawSegment(_last, p);
        InvalidateSegment(_last, p);
        Scope.Commit();
        Scope = null;
    }

    /// <summary>只重繪筆劃涵蓋的螢幕範圍（即時顯示＋高效能）；pad 涵蓋最粗筆刷。</summary>
    private void InvalidateSegment(PointF a, PointF b)
    {
        if (Canvas is null) { Doc.InvalidateComposed(); return; }
        var sa = Canvas.ToScreen(a);
        var sb = Canvas.ToScreen(b);
        var r = Rectangle.Union(
            new Rectangle((int)sa.X, (int)sa.Y, 1, 1),
            new Rectangle((int)sb.X, (int)sb.Y, 1, 1));
        int pad = (int)(64 * Canvas.Zoom) + 8;
        r.Inflate(pad, pad);
        Canvas.Invalidate(r);
    }

    public override void OnDeactivate()
    {
        Scope?.Commit();
        Scope = null;
    }

    protected abstract void DrawDot(PointF p);
    protected abstract void DrawSegment(PointF a, PointF b);
}

/// <summary>EditScope 的輕量包裝（工具端不需要知道圖層細節）。</summary>
public sealed class EditScopeHandle
{
    private readonly Documents.EditScope _scope;
    internal EditScopeHandle(Documents.EditScope scope) => _scope = scope;
    public void AddDirty(Rectangle r) => _scope.AddDirty(r);
    public void AddDirty(PointF p) => _scope.AddDirty(new Point((int)Math.Floor(p.X), (int)Math.Floor(p.Y)));
    public void AddLine(PointF a, PointF b, int width = 1)
    {
        var r = Rectangle.Union(
            new Rectangle((int)Math.Floor(Math.Min(a.X, b.X)) - width, (int)Math.Floor(Math.Min(a.Y, b.Y)) - width, 1, 1),
            new Rectangle((int)Math.Floor(Math.Max(a.X, b.X)), (int)Math.Floor(Math.Max(a.Y, b.Y)), 1, 1));
        _scope.AddDirty(r);
    }
    public void Commit() => _scope.Commit();
    public void Abort() => _scope.Abort();
}

public sealed class PencilTool : StrokeTool
{
    public override string Name => "鉛筆";
    public override string Hint => "鉛筆：以 1~5 像素粗細自由繪製（左鍵＝顏色1，右鍵＝顏色2）";
    public override Cursor Cursor => Opt.PencilWidth <= 2
        ? Cursors.Cross
        : ToolCursor.ForWidth(Opt.PencilWidth, Canvas?.Zoom ?? 1);

    protected override void DrawDot(PointF p) => DrawSegment(p, p);

    protected override void DrawSegment(PointF a, PointF b)
    {
        int w = Math.Max(1, Opt.PencilWidth);
        using var g = OpenActiveLayer();
        g.SmoothingMode = SmoothingMode.None;
        if (w == 1)
        {
            // 逐像素 DDA，保持硬邊
            int x0 = (int)a.X, y0 = (int)a.Y, x1 = (int)b.X, y1 = (int)b.Y;
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                if (x0 >= 0 && y0 >= 0 && x0 < Doc.Size.Width && y0 < Doc.Size.Height)
                    SetPixel(g, x0, y0);

                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }
        else
        {
            using var pen = new Pen(_color, w) { StartCap = LineCap.Flat, EndCap = LineCap.Flat, LineJoin = LineJoin.Miter };
            g.DrawLines(pen, new[] { new PointF(a.X + 0.5f, a.Y + 0.5f), new PointF(b.X + 0.5f, b.Y + 0.5f) });
        }
        Scope?.AddLine(a, b, w);
    }

    private unsafe void SetPixel(Graphics g, int x, int y)
    {
        var data = Doc.Active.Image.LockBits(new Rectangle(x, y, 1, 1),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        *((uint*)data.Scan0) = unchecked((uint)_color.ToArgb());
        Doc.Active.Image.UnlockBits(data);
    }
}

public sealed class BrushTool : StrokeTool
{
    public override string Name => "筆刷";
    public override string Hint => "筆刷：平滑反鋸齒筆劃，可在工具列選擇粗細";
    public override Cursor Cursor => ToolCursor.ForWidth(Math.Max(2, Opt.BrushWidth), Canvas?.Zoom ?? 1);

    protected override void DrawDot(PointF p) => DrawSegment(p, p);

    protected override void DrawSegment(PointF a, PointF b)
    {
        int w = Math.Max(2, Opt.BrushWidth);
        using var g = OpenActiveLayer();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(_color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { a, b });
        Scope?.AddLine(a, b, w);
    }
}

public sealed class EraserTool : StrokeTool
{
    public override string Name => "橡皮擦";
    public override string Hint => "橡皮擦：背景層以顏色2擦除；其他圖層擦為透明（優化）";
    public override Cursor Cursor => ToolCursor.ForWidth(Math.Max(2, Opt.EraserWidth), Canvas?.Zoom ?? 1);

    protected override void DrawDot(PointF p) => DrawSegment(p, p);

    protected override void DrawSegment(PointF a, PointF b)
    {
        int w = Math.Max(2, Opt.EraserWidth);
        using var g = OpenActiveLayer();
        bool transparent = !Doc.Active.IsBackground;
        if (transparent) g.CompositingMode = CompositingMode.SourceCopy;
        g.SmoothingMode = Opt.EraserRounded ? SmoothingMode.AntiAlias : SmoothingMode.None;
        using var pen = new Pen(transparent ? Color.Transparent : _color, w)
        {
            StartCap = Opt.EraserRounded ? LineCap.Round : LineCap.Flat,
            EndCap = Opt.EraserRounded ? LineCap.Round : LineCap.Flat,
            LineJoin = Opt.EraserRounded ? LineJoin.Round : LineJoin.Miter,
        };
        g.DrawLines(pen, new[] { a, b });
        if (transparent) g.CompositingMode = CompositingMode.SourceOver;
        Scope?.AddLine(a, b, w);
    }
}

public sealed class AirbrushTool : StrokeTool
{
    private static readonly Random Rng = new();
    private bool _spraying;

    public override string Name => "噴槍";
    public override string Hint => "噴槍：按住滑鼠繼續噴灑（原版功能完整保留）";
    public override Cursor Cursor => ToolCursor.ForWidth(Math.Max(2, Opt.SprayWidth), Canvas?.Zoom ?? 1);

    public override void OnMouseDown(PointF p, MouseButtons btn)
    {
        if (btn is not (MouseButtons.Left or MouseButtons.Right)) return;
        _color = btn == MouseButtons.Left ? C1 : C2;
        Scope = new EditScopeHandle(Doc.BeginEdit());
        _spraying = true;
        Canvas.StartSprayTimer();
        Spray(p);
    }

    public override void OnMouseMove(PointF p, MouseButtons btn)
    {
        if (_spraying) Spray(p);
    }

    public override void OnMouseUp(PointF p, MouseButtons btn)
    {
        _spraying = false;
        Canvas.StopSprayTimer();
        Scope?.Commit();
        Scope = null;
    }

    public override void OnDeactivate()
    {
        _spraying = false;
        Canvas.StopSprayTimer();
        Scope?.Commit();
        Scope = null;
    }

    public override void OnTick()
    {
        if (_spraying) Spray(_lastSpray);
    }

    private PointF _lastSpray;

    private unsafe void Spray(PointF p)
    {
        _lastSpray = p;
        if (Scope is null) return;
        float r = Math.Max(2, Opt.SprayWidth) / 2f;
        using var g = OpenActiveLayer();
        int n = 25;
        for (int i = 0; i < n; i++)
        {
            double ang = Rng.NextDouble() * Math.PI * 2;
            double rad = Math.Sqrt(Rng.NextDouble()) * r;
            float x = p.X + (float)(Math.Cos(ang) * rad);
            float y = p.Y + (float)(Math.Sin(ang) * rad);
            if (x < 0 || y < 0 || x >= Doc.Size.Width || y >= Doc.Size.Height) continue;
            var data = Doc.Active.Image.LockBits(new Rectangle((int)x, (int)y, 1, 1),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            *((uint*)data.Scan0) = unchecked((uint)_color.ToArgb());
            Doc.Active.Image.UnlockBits(data);
        }
        Scope.AddDirty(Rectangle.Round(new RectangleF(p.X - r, p.Y - r, r * 2, r * 2)));
        Canvas.Invalidate();
    }

    protected override void DrawDot(PointF p) => Spray(p);
    protected override void DrawSegment(PointF a, PointF b) { }
}

public sealed class FillTool : Tool
{
    public override string Name => "填色";
    public override string Hint => "填色：填充相鄰區域；「填色容差」可在選項調整（優化：原版無容差）";
    public override Cursor Cursor => Cursors.Cross;

    public override void OnMouseDown(PointF p, MouseButtons btn)
    {
        if (btn is not (MouseButtons.Left or MouseButtons.Right)) return;
        var color = Opt.FloodUseColor2 ? C2 : (btn == MouseButtons.Left ? C1 : C2);
        var layer = Doc.Active;
        var scope = Doc.BeginEdit();
        var rect = ImageOps.FloodFill(layer.Image,
            new Point((int)p.X, (int)p.Y), color, Math.Clamp(Opt.FloodTolerance, 0, 255));
        if (rect is { } r && r.Width > 0)
        {
            scope.AddDirty(r);
            scope.Commit();
        }
        else scope.Abort();
        Canvas.Invalidate();
    }
}

public sealed class ColorPickerTool : Tool
{
    public override string Name => "取色";
    public override string Hint => "取色：左鍵取為顏色1、右鍵取為顏色2";
    public override Cursor Cursor => Cursors.Cross;

    public override void OnMouseDown(PointF p, MouseButtons btn)
    {
        if (btn is not (MouseButtons.Left or MouseButtons.Right)) return;
        var img = Doc.Compose();
        int x = Math.Clamp((int)p.X, 0, img.Width - 1), y = Math.Clamp((int)p.Y, 0, img.Height - 1);
        var c = img.GetPixel(x, y);
        if (c.A < 255) c = Color.FromArgb(255, c.R, c.G, c.B);
        Canvas.SetColor(btn == MouseButtons.Left ? 1 : 2, c);
    }
}

public sealed class MagicWandTool : Tool
{
    public override string Name => "魔法棒";
    public override string Hint => "魔法棒：依顏色容差選取相鄰區域（取代 AI Magic Select 的核心用途）";
    public override Cursor Cursor => Cursors.Cross;

    public override void OnMouseDown(PointF p, MouseButtons btn)
    {
        if (btn != MouseButtons.Left) return;
        var result = ImageOps.MagicWand(Doc.Active.Image, new Point((int)p.X, (int)p.Y), Math.Clamp(Opt.WandTolerance, 0, 255));
        if (result is { } m)
            Sel.SetMask(m.mask, Doc.Active.Image.Width, m.bounds);
        else
            Sel.Clear();
    }
}
