using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PaintClone.Rendering;

/// <summary>
/// 依工具粗細與縮放動態產生圓形輪廓游標（筆刷/橡皮擦/噴槍），
/// 讓「會畫多大」在落筆前即可見（原版小畫家僅在光點處繪製）。
/// 依尺寸快取，避免重複產生_handles_ 洩漏。
/// </summary>
public static class ToolCursor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, ref ICONINFO info);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    private static readonly Dictionary<int, Cursor> _cache = new();

    /// <summary>產生（或取用快取）直徑為螢幕像素數的圓形游標；失敗時退回十字準線。</summary>
    public static Cursor Dot(int screenDiameter)
    {
        int d = Math.Clamp(screenDiameter, 3, 49);
        if (d % 2 == 0) d++; // 奇數才能置中熱點
        if (_cache.TryGetValue(d, out var cached)) return cached;

        try
        {
            int s = d + 1;
            using var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float r = d / 2f;
                var rect = new RectangleF((s - 1) / 2f - r + 0.5f, (s - 1) / 2f - r + 0.5f, d, d);
                // 黑圈 + 內白圈，深淺背景都看得见
                using (var p = new Pen(Color.Black, 1)) g.DrawEllipse(p, rect);
                if (d >= 9)
                {
                    using var p = new Pen(Color.White, 1);
                    g.DrawEllipse(p, RectangleF.Inflate(rect, -1.5f, -1.5f));
                }
            }

            IntPtr hicon = bmp.GetHicon();
            var info = new ICONINFO();
            if (!GetIconInfo(hicon, ref info))
            {
                DestroyIcon(hicon);
                return Cursors.Cross;
            }
            info.fIcon = false;             // 轉為 cursor
            info.xHotspot = s / 2;          // 熱點＝圓心
            info.yHotspot = s / 2;
            IntPtr hcur = CreateIconIndirect(ref info);
            DestroyIcon(hicon);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (hcur == IntPtr.Zero) return Cursors.Cross;

            var cursor = new Cursor(hcur);  // Cursor 接管 hcur
            _cache[d] = cursor;
            return cursor;
        }
        catch
        {
            return Cursors.Cross;
        }
    }

    /// <summary>依影像粗細與目前縮放換算螢幕直徑（上限 49px）。</summary>
    public static Cursor ForWidth(double imageWidth, double zoom) =>
        Dot((int)Math.Round(imageWidth * Math.Max(0.2, zoom)));
}
