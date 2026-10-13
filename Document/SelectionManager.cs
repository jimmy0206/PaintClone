using System.Drawing.Drawing2D;
using PaintClone.Core;
using PaintClone.Documents;

namespace PaintClone.Documents;

/// <summary>選取狀態：矩形（選取）、任選（套索）、魔法棒（遮罩）。座標皆為影像座標。</summary>
public sealed class SelectionManager : IDisposable
{
    public enum SelMode { None, Rect, Lasso, Mask }

    public SelMode Mode { get; private set; } = SelMode.None;
    public bool IsActive => Mode != SelMode.None;

    public Rectangle Rect { get; private set; }
    public GraphicsPath? LassoPath { get; private set; }

    // 遮罩模式（魔法棒）
    private bool[]? _mask;
    private int _maskW;
    public Rectangle MaskBounds { get; private set; }

    public event EventHandler? Changed;

    public Rectangle Bounds => Mode switch
    {
        SelMode.Rect => Rect,
        SelMode.Lasso => LassoPath is { } lp ? Rectangle.Round(lp.GetBounds()) : Rectangle.Empty,
        SelMode.Mask => MaskBounds,
        _ => Rectangle.Empty,
    };

    public void SelectAll(Size size)
    {
        Clear();
        Mode = SelMode.Rect;
        Rect = new Rectangle(0, 0, size.Width, size.Height);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetRect(Rectangle r)
    {
        ClearCore();
        if (r.Width < 1 || r.Height < 1) { Changed?.Invoke(this, EventArgs.Empty); return; }
        Mode = SelMode.Rect;
        Rect = r;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetLasso(GraphicsPath path)
    {
        ClearCore();
        var b = Rectangle.Round(path.GetBounds());
        if (b.Width < 2 || b.Height < 2) { Changed?.Invoke(this, EventArgs.Empty); return; }
        Mode = SelMode.Lasso;
        LassoPath = path;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>以遮罩設定選取（魔法棒）。</summary>
    public void SetMask(bool[] mask, int maskW, Rectangle bounds)
    {
        ClearCore();
        Mode = SelMode.Mask;
        _mask = mask;
        _maskW = maskW;
        MaskBounds = bounds;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        ClearCore();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearCore()
    {
        Mode = SelMode.None;
        LassoPath?.Dispose();
        LassoPath = null;
        _mask = null;
        _boundaryCache = null;
        Rect = Rectangle.Empty;
        MaskBounds = Rectangle.Empty;
    }

    public bool Contains(Point p) => Mode switch
    {
        SelMode.Rect => Rect.Contains(p),
        SelMode.Lasso => LassoPath!.IsVisible(p),
        // 遮罩陣列以全畫布座標索引（與 GetRegion/GetBoundaryPixels 一致）
        SelMode.Mask => MaskBounds.Contains(p) && _mask![p.Y * _maskW + p.X],
        _ => false,
    };

    public Region GetRegion()
    {
        switch (Mode)
        {
            case SelMode.Rect:
                return new Region(Rect);
            case SelMode.Lasso:
                return new Region(LassoPath!);
            case SelMode.Mask:
            {
                var region = new Region();
                region.MakeEmpty();
                int h = MaskBounds.Height;
                var rects = new List<Rectangle>();
                for (int y = 0; y < h; y++)
                {
                    int x = 0;
                    int row = (MaskBounds.Y + y) * _maskW + MaskBounds.X;
                    while (x < MaskBounds.Width)
                    {
                        if (_mask![row + x])
                        {
                            int x0 = x;
                            while (x < MaskBounds.Width && _mask[row + x]) x++;
                            rects.Add(new Rectangle(MaskBounds.X + x0, MaskBounds.Y + y, x - x0, 1));
                        }
                        else x++;
                    }
                }
                if (rects.Count > 0)
                    foreach (var r in rects)
                        region.Union(r);
                return region;
            }
            default:
                var empty = new Region();
                empty.MakeEmpty();
                return empty;
        }
    }

    /// <summary>取得選取內容（套用遮罩的透明背景裁圖）。</summary>
    public Bitmap GetContent(PaintDocument doc)
    {
        var b = Bounds;
        b.Intersect(new Rectangle(0, 0, doc.Size.Width, doc.Size.Height));
        var bmp = new Bitmap(Math.Max(1, b.Width), Math.Max(1, b.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        switch (Mode)
        {
            case SelMode.Rect:
                g.DrawImage(doc.Active.Image, new Rectangle(0, 0, b.Width, b.Height), b, GraphicsUnit.Pixel);
                break;
            case SelMode.Lasso:
            {
                using var path = new GraphicsPath();
                var el = LassoPath!.PathPoints;
                var types = LassoPath.PathTypes;
                // 平移到圖元座標
                using var m = new Matrix();
                m.Translate(-b.X, -b.Y);
                using var moved = (GraphicsPath)LassoPath.Clone();
                moved.Transform(m);
                g.SetClip(moved);
                g.DrawImage(doc.Active.Image, new Rectangle(0, 0, b.Width, b.Height), b, GraphicsUnit.Pixel);
                GC.KeepAlive(path);
                break;
            }
            case SelMode.Mask:
            {
                using var region = GetRegion();
                using var m = new Matrix();
                m.Translate(-b.X, -b.Y);
                region.Transform(m);
                g.SetClip(region, CombineMode.Replace);
                g.DrawImage(doc.Active.Image, new Rectangle(0, 0, b.Width, b.Height), b, GraphicsUnit.Pixel);
                break;
            }
        }
        return bmp;
    }

    /// <summary>取得遮罩邊界像素（螞蟻線繪製用，含快取）。</summary>
    private Point[]? _boundaryCache;
    public Point[] GetBoundaryPixels()
    {
        if (_boundaryCache is not null) return _boundaryCache;
        if (Mode != SelMode.Mask || _mask is null) return Array.Empty<Point>();
        int w = MaskBounds.Width, h = MaskBounds.Height;
        var list = new List<Point>();
        for (int y = 0; y < h && list.Count < 100_000; y++)
            for (int x = 0; x < w; x++)
            {
                if (!_mask[(MaskBounds.Y + y) * _maskW + MaskBounds.X + x]) continue;
                bool edge = x == 0 || !_mask[(MaskBounds.Y + y) * _maskW + MaskBounds.X + x - 1]
                         || x == w - 1 || !_mask[(MaskBounds.Y + y) * _maskW + MaskBounds.X + x + 1]
                         || y == 0 || !_mask[(MaskBounds.Y + y - 1) * _maskW + MaskBounds.X + x]
                         || y == h - 1 || !_mask[(MaskBounds.Y + y + 1) * _maskW + MaskBounds.X + x];
                if (edge) list.Add(new Point(MaskBounds.X + x, MaskBounds.Y + y));
            }
        _boundaryCache = list.ToArray();
        return _boundaryCache;
    }

    /// <summary>整體平移選取（拖曳移動/方向鍵微調後讓螞蟻線跟隨內容）。</summary>
    public void Translate(int dx, int dy)
    {
        if (!IsActive || (dx == 0 && dy == 0)) return;
        switch (Mode)
        {
            case SelMode.Rect:
                Rect = new Rectangle(Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height);
                break;
            case SelMode.Lasso:
                using (var m = new Matrix())
                {
                    m.Translate(dx, dy);
                    LassoPath!.Transform(m);
                }
                break;
            case SelMode.Mask:
            {
                // 遮罩陣列為全畫布尺寸，需整體位移後重建
                int w = _maskW, h = _mask!.Length / w;
                var shifted = new bool[_mask.Length];
                for (int y = 0; y < h; y++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= h) continue;
                    int srcRow = y * w, dstRow = ny * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (!_mask[srcRow + x]) continue;
                        int nx = x + dx;
                        if (nx >= 0 && nx < w) shifted[dstRow + nx] = true;
                    }
                }
                _mask = shifted;
                MaskBounds = new Rectangle(MaskBounds.X + dx, MaskBounds.Y + dy, MaskBounds.Width, MaskBounds.Height);
                _boundaryCache = null;
                break;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => ClearCore();
}
