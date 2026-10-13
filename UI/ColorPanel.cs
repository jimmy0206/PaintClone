namespace PaintClone.UI;

/// <summary>調色盤面板：顏色1/2、標準色、自訂色、最近使用（左鍵設顏色1、右鍵設顏色2）。</summary>
public sealed class ColorPanel : Control
{
    private static readonly Color[] Standard =
    {
        Color.FromArgb(0, 0, 0), Color.FromArgb(127, 127, 127), Color.FromArgb(136, 0, 21), Color.FromArgb(230, 0, 35),
        Color.FromArgb(255, 127, 39), Color.FromArgb(255, 255, 0), Color.FromArgb(33, 214, 0), Color.FromArgb(0, 162, 232),
        Color.FromArgb(64, 0, 255), Color.FromArgb(170, 0, 255),
        Color.FromArgb(255, 255, 255), Color.FromArgb(191, 191, 191), Color.FromArgb(187, 0, 0), Color.FromArgb(255, 127, 127),
        Color.FromArgb(255, 170, 0), Color.FromArgb(255, 255, 127), Color.FromArgb(127, 255, 0), Color.FromArgb(127, 255, 127),
        Color.FromArgb(0, 255, 255), Color.FromArgb(170, 170, 255),
    };

    private readonly List<Color> _custom = new();
    private readonly List<Color> _recent = new();

    private Color _color1 = Color.Black;
    private Color _color2 = Color.White;

    private const int Swatch = 15, Gap = 1, BoxSize = 26;

    public event Action<int, Color>? ColorChosen;          // 1 或 2
    public event Action<Color>? ColorUsed;                  // 任何取用（推入最近）

    public ColorPanel()
    {
        DoubleBuffered = true;
        Height = 44;
        BackColor = SystemColors.Control;
        foreach (var c in _custom) { }
    }

    public IReadOnlyList<Color> CustomColors => _custom;
    public IReadOnlyList<Color> RecentColors => _recent;

    public void LoadPersisted(IEnumerable<string> recentHex)
    {
        foreach (var hex in recentHex)
            if (TryParseHex(hex, out var c)) _recent.Add(c);
        Invalidate();
    }

    public static bool TryParseHex(string hex, out Color c)
    {
        c = Color.Black;
        if (hex.Length != 7 || hex[0] != '#') return false;
        try
        {
            c = Color.FromArgb(
                Convert.ToInt32(hex.Substring(1, 2), 16),
                Convert.ToInt32(hex.Substring(3, 2), 16),
                Convert.ToInt32(hex.Substring(5, 2), 16));
            return true;
        }
        catch { return false; }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public Color Color1
    {
        get => _color1;
        set { _color1 = value; Invalidate(); }
    }

    public Color Color2
    {
        get => _color2;
        set { _color2 = value; Invalidate(); }
    }

    public void SwapColors() => (_color1, _color2) = (_color2, _color1);

    public void PushRecent(Color c)
    {
        _recent.RemoveAll(x => x.ToArgb() == c.ToArgb());
        _recent.Insert(0, c);
        while (_recent.Count > 10) _recent.RemoveAt(_recent.Count - 1);
        Invalidate();
    }

    private Rectangle Color1Rect => new(6, 6, BoxSize, BoxSize);
    private Rectangle Color2Rect => new(6 + BoxSize - 8, 6 + BoxSize - 10, BoxSize, BoxSize);
    private Rectangle SwapRect => new(70, 8, 18, 14);
    private Rectangle ResetRect => new(70, 24, 18, 14);
    private int PaletteStartX => 96;

    private IEnumerable<(Rectangle rect, Color color, int kind)> Swatches()
    {
        int x = PaletteStartX;
        for (int i = 0; i < Standard.Length; i++)
        {
            int col = i % 10, row = i / 10;
            yield return (new Rectangle(x + col * (Swatch + Gap), 5 + row * (Swatch + Gap) + (row > 0 ? 0 : 1), Swatch, Swatch), Standard[i], 0);
        }
        x = PaletteStartX + 10 * (Swatch + Gap) + 12;
        int idx = 0;
        foreach (var c in _custom)
        {
            int col = idx % 5, row = idx / 5;
            yield return (new Rectangle(x + col * (Swatch + Gap), 5 + row * (Swatch + Gap) + (row > 0 ? 0 : 1), Swatch, Swatch), c, 1);
            idx++;
        }
        idx = 0;
        foreach (var c in _recent)
        {
            int col = idx % 5, row = idx / 5;
            yield return (new Rectangle(x + col * (Swatch + Gap), 5 + row * (Swatch + Gap) + (row > 0 ? 0 : 1), Swatch, Swatch), c, 2);
            idx++;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        // 顏色2 在底、顏色1 在前（經典重疊式）
        using (var b2 = new SolidBrush(_color2))
        using (var b1 = new SolidBrush(_color1))
        using (var border = new Pen(Color.Gray))
        using (var hi = new Pen(Color.SteelBlue, 2))
        {
            g.FillRectangle(b2, Color2Rect);
            g.DrawRectangle(border, Color2Rect);
            g.FillRectangle(b1, Color1Rect);
            g.DrawRectangle(border, Color1Rect);
            g.DrawRectangle(hi, Rectangle.Inflate(Color1Rect, 1, 1));
        }

        using (var f = new Font(Font.FontFamily, 7.5f))
        {
            g.DrawString("對調", f, Brushes.Black, SwapRect.X + 18, SwapRect.Y - 1);
            g.DrawString("重設", f, Brushes.Black, ResetRect.X + 18, ResetRect.Y - 1);
        }

        foreach (var (rect, color, kind) in Swatches())
        {
            using var b = new SolidBrush(color);
            g.FillRectangle(b, rect);
            using var p = new Pen(Color.FromArgb(120, 120, 120));
            g.DrawRectangle(p, rect);
            if (kind == 1)
                using (var star = new SolidBrush(Color.White))
                    g.DrawString("★", new Font(Font.FontFamily, 6f), Brushes.DimGray, rect.X, rect.Y - 2);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Color1Rect.Contains(e.Location) || Color2Rect.Contains(e.Location))
        {
            // 點擊色塊開啟調色盤編輯該色
            bool first = Color1Rect.Contains(e.Location);
            using var dlg = new ColorDialog { FullOpen = true, Color = first ? _color1 : _color2 };
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
            {
                SetColor(first ? 1 : 2, dlg.Color);
                AddCustom(dlg.Color);
            }
            return;
        }
        if (new Rectangle(SwapRect.X - 2, SwapRect.Y, 52, SwapRect.Height).Contains(e.Location))
        {
            SwapColors();
            ColorChosen?.Invoke(0, _color1); // 0 = 通知同步
            Invalidate();
            return;
        }
        if (new Rectangle(ResetRect.X - 2, ResetRect.Y, 52, ResetRect.Height).Contains(e.Location))
        {
            _color1 = Color.Black; _color2 = Color.White;
            ColorChosen?.Invoke(0, _color1);
            Invalidate();
            return;
        }

        foreach (var (rect, color, kind) in Swatches())
        {
            if (!rect.Contains(e.Location)) continue;
            int which = e.Button == MouseButtons.Right ? 2 : 1;
            SetColor(which, color);
            if (kind != 0) PushRecent(color);
            ColorUsed?.Invoke(color);
            return;
        }
    }

    private void SetColor(int which, Color c)
    {
        if (which == 1) _color1 = c; else _color2 = c;
        ColorChosen?.Invoke(which, c);
        Invalidate();
    }

    /// <summary>從取色工具等外部來源同步顏色。</summary>
    public void SetFromExternal(int which, Color c)
    {
        if (which == 1) _color1 = c; else _color2 = c;
        Invalidate();
    }

    private void AddCustom(Color c)
    {
        if (_custom.Count >= 10) _custom.RemoveAt(0);
        if (!_custom.Any(x => x.ToArgb() == c.ToArgb())) _custom.Add(c);
        Invalidate();
    }
}
