using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PaintClone.Core;

public static class ImageOps
{
    /// <summary>把 32bpp 位圖複製到緊湊的 uint[]（每像素 ARGB）。</summary>
    private static unsafe uint[] CopyToPixels(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var pixels = new uint[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            fixed (uint* dst = pixels)
            {
                byte* src = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(src + (long)y * data.Stride, dst + (long)y * w, (long)w * 4, (long)w * 4);
            }
        }
        finally { bmp.UnlockBits(data); }
        return pixels;
    }

    /// <summary>將任意 Image 轉成 32bpp ARGB Bitmap。</summary>
    public static Bitmap ToArgb32(this Image src)
    {
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImageUnscaled(src, 0, 0);
        return bmp;
    }

    public static Bitmap NewCanvas(int w, int h, Color background)
    {
        var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(background);
        return bmp;
    }

    // ---------------------------------------------------------------- 填色

    /// <summary>
    /// 掃描線區域填充（含容差）。回傳變更範圍；無法填充時回 null。
    /// 優化：原版只比對精確顏色，這裡支援 0–255 容差。
    /// </summary>
    public static unsafe Rectangle? FloodFill(Bitmap bmp, Point seed, Color fillColor, int tolerance)
    {
        if (seed.X < 0 || seed.Y < 0 || seed.X >= bmp.Width || seed.Y >= bmp.Height) return null;

        int w = bmp.Width, h = bmp.Height;
        var target = CopyToPixels(bmp);
        var visited = new bool[w * h];

        uint seedArgb = target[seed.Y * w + seed.X];
        uint fillArgb = unchecked((uint)fillColor.ToArgb());
        if (seedArgb == fillArgb) return null;

        bool Near(uint a, uint b)
        {
            if (Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)) > tolerance) return false;
            if (Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) > tolerance) return false;
            if (Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) > tolerance) return false;
            return true;
        }

        int minX = seed.X, maxX = seed.X, minY = seed.Y, maxY = seed.Y;
        var stack = new Stack<int>();
        stack.Push(seed.Y * w + seed.X);

        while (stack.Count > 0)
        {
            int idx = stack.Pop();
            int y = idx / w, x0 = idx % w;
            if (visited[idx] || !Near(target[idx], seedArgb)) continue;

            int x = x0;
            while (x > 0 && !visited[y * w + x - 1] && Near(target[y * w + x - 1], seedArgb)) x--;
            int xe = x0;
            while (xe + 1 < w && !visited[y * w + xe + 1] && Near(target[y * w + xe + 1], seedArgb)) xe++;

            for (int xi = x; xi <= xe; xi++)
            {
                int i = y * w + xi;
                if (visited[i]) continue;
                visited[i] = true;
                target[i] = fillArgb;
            }

            if (x < minX) minX = x; if (xe > maxX) maxX = xe;
            if (y < minY) minY = y; if (y > maxY) maxY = y;

            foreach (int ny in new[] { y - 1, y + 1 })
            {
                if (ny < 0 || ny >= h) continue;
                bool run = false;
                for (int xi = x; xi <= xe; xi++)
                {
                    int i = ny * w + xi;
                    bool ok = !visited[i] && Near(target[i], seedArgb);
                    if (ok && !run) { stack.Push(i); run = true; }
                    else if (!ok) run = false;
                }
            }
        }

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            fixed (uint* src = target)
            {
                byte* dst = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(src + (long)y * w, dst + (long)y * data.Stride, data.Stride, (long)w * 4);
            }
        }
        finally { bmp.UnlockBits(data); }

        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>魔法棒：以容差選取相鄰同色區域，回傳遮罩（true=選取）與外框。</summary>
    public static unsafe (bool[] mask, Rectangle bounds)? MagicWand(Bitmap bmp, Point seed, int tolerance)
    {
        if (seed.X < 0 || seed.Y < 0 || seed.X >= bmp.Width || seed.Y >= bmp.Height) return null;
        int w = bmp.Width, h = bmp.Height;
        var pixels = new uint[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* src = (byte*)data.Scan0;
            fixed (uint* dst = pixels)
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(src + (long)y * data.Stride, dst + (long)y * w, (long)w * 4, (long)w * 4);
        }
        finally { bmp.UnlockBits(data); }

        uint seedArgb = pixels[seed.Y * w + seed.X];
        bool Near(uint a)
        {
            if (((a ^ seedArgb) & 0xFF000000) != 0 && ((a >> 24) == 0 || (seedArgb >> 24) == 0))
                return (a >> 24) == (seedArgb >> 24);
            if (Math.Abs((int)((a >> 16) & 0xFF) - (int)((seedArgb >> 16) & 0xFF)) > tolerance) return false;
            if (Math.Abs((int)((a >> 8) & 0xFF) - (int)((seedArgb >> 8) & 0xFF)) > tolerance) return false;
            return Math.Abs((int)(a & 0xFF) - (int)(seedArgb & 0xFF)) <= tolerance;
        }

        var mask = new bool[w * h];
        var stack = new Stack<int>();
        if (!Near(seedArgb)) return null;
        stack.Push(seed.Y * w + seed.X);
        int minX = w, minY = h, maxX = 0, maxY = 0;

        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (mask[i]) continue;
            mask[i] = true;
            int x = i % w, y = i / w;
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
            if (x > 0 && !mask[i - 1] && Near(pixels[i - 1])) stack.Push(i - 1);
            if (x + 1 < w && !mask[i + 1] && Near(pixels[i + 1])) stack.Push(i + 1);
            if (y > 0 && !mask[i - w] && Near(pixels[i - w])) stack.Push(i - w);
            if (y + 1 < h && !mask[i + w] && Near(pixels[i + w])) stack.Push(i + w);
        }
        return (mask, new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1));
    }

    // ---------------------------------------------------------------- 幾何變換

    public static Bitmap Resize(Bitmap src, int w, int h, bool smoothing)
    {
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = smoothing ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = smoothing ? SmoothingMode.HighQuality : SmoothingMode.None;
        g.DrawImage(src, new Rectangle(0, 0, w, h));
        return dst;
    }

    /// <summary>任意角度旋轉（座標系：數學逆時針為正，與小畫家一致）。</summary>
    public static Bitmap Rotate(Bitmap src, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
        int w = (int)Math.Round(src.Height * sin + src.Width * cos);
        int h = (int)Math.Round(src.Height * cos + src.Width * sin);
        var dst = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(dst.Width / 2f, dst.Height / 2f);
        g.RotateTransform((float)-degrees); // GDI+ 順時針為正
        g.TranslateTransform(-src.Width / 2f, -src.Height / 2f);
        g.DrawImageUnscaled(src, 0, 0);
        return dst;
    }

    public static Bitmap Flip(Bitmap src, bool horizontal)
    {
        var dst = src.ToArgb32();
        dst.RotateFlip(horizontal ? RotateFlipType.RotateNoneFlipX : RotateFlipType.RotateNoneFlipY);
        return dst;
    }

    /// <summary>傾斜（skew）：水平/垂直度數，正值分別向右/向下。</summary>
    public static Bitmap Skew(Bitmap src, double hDeg, double vDeg)
    {
        double th = Math.Tan(hDeg * Math.PI / 180.0); // x' 受 y 影響
        double tv = Math.Tan(vDeg * Math.PI / 180.0); // y' 受 x 影響
        double x1 = src.Width, y1 = src.Height;
        // 四角變換後範圍
        double minX = 0, minY = 0, maxX = x1, maxY = y1;
        foreach (var (px, py) in new[] { (0d, 0d), (x1, 0d), (0d, y1), (x1, y1) })
        {
            double nx = px + py * th;
            double ny = py + px * tv;
            minX = Math.Min(minX, nx); maxX = Math.Max(maxX, nx);
            minY = Math.Min(minY, ny); maxY = Math.Max(maxY, ny);
        }
        int w = Math.Max(1, (int)Math.Ceiling(maxX - minX));
        int h = Math.Max(1, (int)Math.Ceiling(maxY - minY));
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var m = new Matrix(1, (float)tv, (float)th, 1, (float)-minX, (float)-minY);
        g.MultiplyTransform(m);
        g.DrawImageUnscaled(src, 0, 0);
        return dst;
    }

    public static Bitmap Crop(Bitmap src, Rectangle rect)
    {
        rect.Intersect(new Rectangle(0, 0, src.Width, src.Height));
        if (rect.Width < 1 || rect.Height < 1) throw new ArgumentException("裁剪範圍無效");
        var dst = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
        return dst;
    }

    // ---------------------------------------------------------------- 效果

    /// <summary>套用 32bpp 像素逐點变换（保留 Alpha）。</summary>
    public static unsafe Bitmap MapPixels(Bitmap src, Func<Color, Color> map)
    {
        var dst = src.ToArgb32();
        var data = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            byte* p = (byte*)data.Scan0;
            for (int i = 0; i < data.Height; i++)
            {
                for (int j = 0; j < data.Width; j++)
                {
                    int idx = i * data.Stride + j * 4;
                    byte b = p[idx], g8 = p[idx + 1], r = p[idx + 2], a = p[idx + 3];
                    var c = map(Color.FromArgb(a, r, g8, b));
                    p[idx] = c.B; p[idx + 1] = c.G; p[idx + 2] = c.R; p[idx + 3] = c.A;
                }
            }
        }
        finally { dst.UnlockBits(data); }
        return dst;
    }

    public static Bitmap Invert(Bitmap src) =>
        MapPixels(src, c => Color.FromArgb(c.A, 255 - c.R, 255 - c.G, 255 - c.B));

    public static Bitmap Grayscale(Bitmap src) =>
        MapPixels(src, c =>
        {
            int v = (int)Math.Round(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
            return Color.FromArgb(c.A, v, v, v);
        });

    public static Bitmap Sepia(Bitmap src) =>
        MapPixels(src, c => Color.FromArgb(c.A,
            Math.Min(255, (int)(c.R * .393 + c.G * .769 + c.B * .189)),
            Math.Min(255, (int)(c.R * .349 + c.G * .686 + c.B * .168)),
            Math.Min(255, (int)(c.R * .272 + c.G * .534 + c.B * .131))));

    /// <summary>色相(-180..180)、飽和度(-100..100 %)、亮度(-100..100)、對比(-100..100) 調整。
    /// 傳回**新**位圖，由呼叫端 Dispose。</summary>
    public static Bitmap Adjust(Bitmap src, float hue, float saturation, float brightness, float contrast)
    {
        // 這裡刻意「不用」using var：下面 hue==0 時會把 img 直接當傳回值交出去，
        // 而 using var 即使在 return 之後仍會 Dispose 它，呼叫端就會拿到一張已釋放的位圖
        //（色相=0 是預設值，所以「調整顏色」按確定或預覽一定炸：
        //  PictureBox 重繪 → ImageAnimator 讀 FrameDimensionsList → ArgumentException "Parameter is not valid"）。
        var img = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(img))
        using (var attrs = new ImageAttributes())
        {
            var cm = IdentityArr();
            // 飽和度
            float s = 1f + saturation / 100f, inv = 1f - s;
            var sat = new float[][]
            {
                new[]{0.213f + 0.787f*s, 0.213f*inv, 0.213f*inv, 0f, 0f},
                new[]{0.715f*inv, 0.715f + 0.285f*s, 0.715f*inv, 0f, 0f},
                new[]{0.072f*inv, 0.072f*inv, 0.072f + 0.928f*s, 0f, 0f},
                new[]{0f,0f,0f,1f,0f},
                new[]{0f,0f,0f,0f,1f},
            };
            cm = Multiply(cm, sat);
            // 亮度
            float bAdj = brightness / 255f + 1f;
            var bright = new float[][]
            {
                new[]{bAdj,0f,0f,0f,0f}, new[]{0f,bAdj,0f,0f,0f},
                new[]{0f,0f,bAdj,0f,0f}, new[]{0f,0f,0f,1f,0f},
                new[]{0f,0f,0f,0f,1f},
            };
            cm = Multiply(cm, bright);
            // 對比
            float cAdj = contrast / 100f + 1f;
            float shift = 0.5f * (1f - cAdj);
            var cont = new float[][]
            {
                new[]{cAdj,0f,0f,0f,0f}, new[]{0f,cAdj,0f,0f,0f},
                new[]{0f,0f,cAdj,0f,0f}, new[]{0f,0f,0f,1f,0f},
                new[]{shift,shift,shift,0f,1f},
            };
            cm = Multiply(cm, cont);
            var matrix = new ColorMatrix(cm);
            attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            g.Clear(Color.Transparent);
            g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        }
        if (Math.Abs(hue) < 0.01f) return img;                          // 直接交給呼叫端釋放
        try { return MapPixels(img, c => ShiftHue(c, hue)); }           // 色相換算期間 img 必須存活
        finally { img.Dispose(); }
    }

    private static float[][] IdentityArr() => new float[][]
    {
        new float[]{1,0,0,0,0}, new float[]{0,1,0,0,0}, new float[]{0,0,1,0,0},
        new float[]{0,0,0,1,0}, new float[]{0,0,0,0,1},
    };

    private static float[][] Multiply(float[][] a, float[][] b)
    {
        var r = IdentityArr();
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
            {
                float v = 0;
                for (int k = 0; k < 5; k++) v += a[i][k] * b[k][j];
                r[i][j] = v;
            }
        return r;
    }

    private static Color ShiftHue(Color c, float degrees)
    {
        if (c.A == 0) return c;
        float h, s, l;
        ColorConverter.GetHueSaturationBrightness(c.R / 255f, c.G / 255f, c.B / 255f, out h, out s, out l);
        h = (h + degrees) % 360f; if (h < 0) h += 360f;
        var (r, g, b) = HsbToRgb(h, s, l);
        return Color.FromArgb(c.A, (int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    private static (float, float, float) HsbToRgb(float h, float s, float l)
    {
        float f(float n)
        {
            float k = MathF.Round((n + h / 30f) % 12f);
            float a = s * Math.Min(l, 1 - l);
            return l - a * Math.Max(-1f, Math.Min(Math.Min(k - 3f, 9f - k), 1f));
        }
        return (f(0), f(8), f(4));
    }

    private static class ColorConverter
    {
        public static void GetHueSaturationBrightness(float r, float g, float b,
            out float hue, out float sat, out float lum)
        {
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            lum = (max + min) / 2f;
            float d = max - min;
            if (d < 1e-6f) { hue = 0; sat = 0; return; }
            sat = lum > 0.5f ? d / (2f - max - min) : d / (max + min);
            hue = max == r ? ((g - b) / d + (g < b ? 6 : 0)) * 60f
                : max == g ? ((b - r) / d + 2) * 60f
                : ((r - g) / d + 4) * 60f;
        }
    }
}
