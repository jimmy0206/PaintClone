using System.Drawing.Drawing2D;

namespace PaintClone.UI;

/// <summary>以 GDI+ 程式繪製工具列圖示（不依賴外部圖檔資源）。</summary>
public static class IconFactory
{
    public static Bitmap Make(string kind, int size = 20)
    {
        var bmp = new Bitmap(size, size);
        bmp.MakeTransparent();
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = size;
        var ink = Color.FromArgb(40, 40, 40);
        using var pen = new Pen(ink, Math.Max(1.6f, s / 12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var thin = new Pen(ink, Math.Max(1f, s / 18f));
        using var blue = new Pen(Color.FromArgb(30, 100, 200), Math.Max(1.6f, s / 12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        switch (kind)
        {
            case "pencil":
                g.DrawLine(pen, s * .2f, s * .8f, s * .78f, s * .22f);
                g.FillPolygon(Brushes.Gold, new[] { P(s, .12, .88), P(s, .2, .74), P(s, .3, .84) });
                break;
            case "brush":
                g.DrawLine(blue, s * .25f, s * .75f, s * .8f, s * .2f);
                using (var tip = new SolidBrush(Color.FromArgb(120, 180, 240)))
                    g.FillEllipse(tip, s * .1f, s * .68f, s * .3f, s * .25f);
                break;
            case "airbrush":
                using (var b = new SolidBrush(ink))
                {
                    var rnd = new Random(7);
                    for (int i = 0; i < 18; i++)
                        g.FillRectangle(b, s * (.15f + (float)rnd.NextDouble() * .7f), s * (.15f + (float)rnd.NextDouble() * .7f), 2, 2);
                }
                g.DrawArc(pen, s * .3f, s * .3f, s * .5f, s * .5f, 200, 140);
                break;
            case "eraser":
                using (var eb = new SolidBrush(Color.FromArgb(255, 150, 160)))
                    g.FillPolygon(eb, new[] { P(s, .2, .62), P(s, .55, .27), P(s, .85, .55), P(s, .5, .9) });
                g.DrawPolygon(thin, new[] { P(s, .2, .62), P(s, .55, .27), P(s, .85, .55), P(s, .5, .9) });
                break;
            case "fill":
                g.DrawPolygon(pen, new[] { P(s, .25, .35), P(s, .55, .15), P(s, .8, .5), P(s, .5, .75) });
                using (var pb = new SolidBrush(Color.FromArgb(60, 160, 80)))
                    g.FillEllipse(pb, s * .6f, s * .58f, s * .25f, s * .33f);
                break;
            case "picker":
                g.DrawLine(pen, s * .18f, s * .85f, s * .6f, s * .43f);
                g.DrawEllipse(pen, s * .55f, s * .15f, s * .3f, s * .3f);
                break;
            case "wand":
                g.DrawLine(blue, s * .2f, s * .8f, s * .62f, s * .38f);
                for (int i = 0; i < 4; i++)
                {
                    double a = Math.PI / 4 + i * Math.PI / 2;
                    float cx = s * .72f, cy = s * .28f, r = s * .17f;
                    g.DrawLine(thin, cx + (float)Math.Cos(a) * r * .5f, cy + (float)Math.Sin(a) * r * .5f,
                        cx + (float)Math.Cos(a) * r * 1.3f, cy + (float)Math.Sin(a) * r * 1.3f);
                }
                break;
            case "line":
                g.DrawLine(pen, s * .15f, s * .82f, s * .85f, s * .18f);
                break;
            case "curve":
                using (var gp = new GraphicsPath())
                {
                    gp.AddBezier(P(s, .12, .82), P(s, .35, .1), P(s, .65, .95), P(s, .88, .25));
                    g.DrawPath(pen, gp);
                }
                break;
            case "rect":
                g.DrawRectangle(thin, s * .15f, s * .25f, s * .7f, s * .5f);
                break;
            case "roundrect":
                DrawRounded(g, thin, new RectangleF(s * .15f, s * .25f, s * .7f, s * .5f), s * .15f);
                break;
            case "ellipse":
                g.DrawEllipse(thin, s * .15f, s * .22f, s * .7f, s * .56f);
                break;
            case "callout":
                DrawRounded(g, pen, new RectangleF(s * .12f, s * .12f, s * .62f, s * .44f), s * .1f);
                g.DrawLine(pen, s * .3f, s * .55f, s * .2f, s * .88f);
                g.DrawLine(pen, s * .5f, s * .55f, s * .42f, s * .88f);
                break;
            case "polygon":
                g.DrawPolygon(thin, new[] { P(s, .5, .1), P(s, .88, .42), P(s, .7, .9), P(s, .28, .82), P(s, .14, .35) });
                break;
            case "text":
                using (var f = new Font("Times New Roman", s * .68f, FontStyle.Bold, GraphicsUnit.Pixel))
                    g.DrawString("A", f, Brushes.Black, 0, -s * .06f);
                break;
            case "select":
                using (var dp = new Pen(Color.Black, 1.6f) { DashPattern = new float[] { 3, 3 } })
                    g.DrawRectangle(dp, s * .15f, s * .2f, s * .7f, s * .6f);
                break;
            case "lasso":
                using (var lp = new Pen(Color.Black, 1.6f) { DashPattern = new float[] { 4, 3 } })
                using (var gp = new GraphicsPath())
                {
                    gp.AddEllipse(s * .12f, s * .18f, s * .66f, s * .5f);
                    g.DrawPath(lp, gp);
                }
                g.DrawLine(pen, s * .7f, s * .62f, s * .85f, s * .88f);
                g.FillEllipse(Brushes.Black, s * .8f, s * .82f, s * .12f, s * .12f);
                break;
            case "new":
                g.DrawRectangle(thin, s * .22f, s * .15f, s * .5f, s * .7f);
                g.DrawLine(pen, s * .6f, s * .18f, s * .78f, s * .36f);
                break;
            case "open":
                g.DrawArc(pen, s * .12f, s * .3f, s * .7f, s * .55f, 150, 240);
                g.DrawLine(pen, s * .12f, s * .55f, s * .85f, s * .55f);
                break;
            case "save":
                g.DrawRectangle(thin, s * .18f, s * .18f, s * .64f, s * .64f);
                g.FillRectangle(Brushes.Gray, s * .35f, s * .55f, s * .3f, s * .27f);
                g.FillRectangle(Brushes.White, s * .62f, s * .24f, s * .1f, s * .16f);
                break;
            case "print":
                g.DrawRectangle(thin, s * .15f, s * .4f, s * .7f, s * .35f);
                g.DrawRectangle(thin, s * .3f, s * .15f, s * .4f, s * .3f);
                g.DrawRectangle(thin, s * .3f, s * .6f, s * .4f, s * .28f);
                break;
            case "undo":
                using (var up = new Pen(Color.FromArgb(30, 110, 60), 2f) { StartCap = LineCap.Round })
                {
                    g.DrawArc(up, s * .25f, s * .25f, s * .55f, s * .55f, 90, 240);
                    g.DrawLine(up, s * .25f, s * .52f, s * .12f, s * .4f);
                    g.DrawLine(up, s * .25f, s * .52f, s * .4f, s * .42f);
                }
                break;
            case "redo":
                using (var rp = new Pen(Color.FromArgb(30, 110, 60), 2f) { StartCap = LineCap.Round })
                {
                    g.DrawArc(rp, s * .2f, s * .25f, s * .55f, s * .55f, 30, 240);
                    g.DrawLine(rp, s * .75f, s * .52f, s * .88f, s * .4f);
                    g.DrawLine(rp, s * .75f, s * .52f, s * .6f, s * .42f);
                }
                break;
            case "zoomin":
                g.DrawEllipse(pen, s * .18f, s * .18f, s * .5f, s * .5f);
                g.DrawLine(pen, s * .62f, s * .62f, s * .85f, s * .85f);
                g.DrawLine(thin, s * .43f, s * .32f, s * .43f, s * .55f);
                g.DrawLine(thin, s * .32f, s * .43f, s * .55f, s * .43f);
                break;
            case "zoomout":
                g.DrawEllipse(pen, s * .18f, s * .18f, s * .5f, s * .5f);
                g.DrawLine(pen, s * .62f, s * .62f, s * .85f, s * .85f);
                g.DrawLine(thin, s * .32f, s * .43f, s * .55f, s * .43f);
                break;
            case "layers":
                using (var lp2 = new SolidBrush(Color.FromArgb(160, 200, 240)))
                    g.FillPolygon(lp2, new[] { P(s, .5, .1), P(s, .9, .32), P(s, .5, .54), P(s, .1, .32) });
                g.DrawPolygon(thin, new[] { P(s, .5, .18), P(s, .9, .4), P(s, .5, .62), P(s, .1, .4) });
                g.DrawPolygon(thin, new[] { P(s, .5, .34), P(s, .9, .56), P(s, .5, .78), P(s, .1, .56) });
                break;
            default:
                g.DrawRectangle(thin, 2, 2, s - 5, s - 5);
                break;
        }
        return bmp;
    }

    private static PointF P(float s, double x, double y) => new((float)(s * x), (float)(s * y));

    private static void DrawRounded(Graphics g, Pen pen, RectangleF r, float radius)
    {
        using var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }
}
