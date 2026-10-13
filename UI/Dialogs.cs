using PaintClone.Core;
using PaintClone.Rendering;

namespace PaintClone.UI;

/// <summary>調整圖片大小與解析度（對應原版 ResizeImage 對話框）。</summary>
public sealed class ResizeDialog : Form
{
    private readonly NumericUpDown _width, _height;
    private readonly RadioButton _px, _percent;
    private readonly CheckBox _keepAspect, _smooth;
    private readonly int _origW, _origH;
    private bool _updating;

    public int NewWidth => (int)_width.Value;
    public int NewHeight => (int)_height.Value;
    public bool Smoothing => _smooth.Checked;

    public ResizeDialog(int w, int h, bool hasSelection)
    {
        _origW = w; _origH = h;
        Text = hasSelection ? "調整選取範圍大小" : "調整圖片大小與解析度";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(300, 210);

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), FlowDirection = FlowDirection.TopDown, WrapContents = false };

        _px = new RadioButton { Text = "像素", Checked = true, AutoSize = true };
        _percent = new RadioButton { Text = "百分比", AutoSize = true };

        var row1 = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        row1.Controls.Add(new Label { Text = "水平：", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        _width = new NumericUpDown { Minimum = 1, Maximum = 30000, Value = w, Width = 90 };

        var row2 = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        row2.Controls.Add(new Label { Text = "垂直：", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        _height = new NumericUpDown { Minimum = 1, Maximum = 30000, Value = h, Width = 90 };

        _keepAspect = new CheckBox { Text = "維持長寬比", Checked = true, AutoSize = true };
        _smooth = new CheckBox { Text = "以平滑方式重新調整大小（反鋸齒）", Checked = true, AutoSize = true };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Width = 280, Height = 40 };
        var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, Width = 75 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 75 };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        layout.Controls.AddRange(new Control[] { _px, _percent, row1, row2, _keepAspect, _smooth, buttons });
        row1.Controls.Add(_width);
        row2.Controls.Add(_height);
        Controls.Add(layout);

        _width.ValueChanged += (_, _) => Sync(true);
        _height.ValueChanged += (_, _) => Sync(false);
        _percent.CheckedChanged += (_, _) =>
        {
            _updating = true;
            if (_percent.Checked)
            {
                _width.Maximum = 10000; _height.Maximum = 10000;
                _width.Value = 100; _height.Value = 100;
            }
            else
            {
                _width.Maximum = 30000; _height.Maximum = 30000;
                _width.Value = _origW; _height.Value = _origH;
            }
            _updating = false;
        };

        AcceptButton = ok; CancelButton = cancel;
    }

    private void Sync(bool fromWidth)
    {
        if (_updating || !_keepAspect.Checked) return;
        _updating = true;
        if (_percent.Checked)
        {
            if (fromWidth) _height.Value = Math.Clamp((int)Math.Round(_width.Value), 1, 10000);
            else _width.Value = Math.Clamp((int)Math.Round(_height.Value), 1, 10000);
        }
        else
        {
            double ratio = (double)_origH / _origW;
            if (fromWidth) _height.Value = Math.Clamp((decimal)Math.Round((double)_width.Value * ratio), 1, 30000);
            else _width.Value = Math.Clamp((decimal)Math.Round((double)_height.Value / ratio), 1, 30000);
        }
        _updating = false;
    }
}

/// <summary>傾斜對話框（原版 SkewImage）。</summary>
public sealed class SkewDialog : Form
{
    private readonly NumericUpDown _h, _v;
    public double HorizontalDeg => (double)_h.Value;
    public double VerticalDeg => (double)_v.Value;

    public SkewDialog()
    {
        Text = "傾斜";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(260, 140);

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var r1 = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        r1.Controls.Add(new Label { Text = "水平傾斜：", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        _h = new NumericUpDown { Minimum = -89, Maximum = 89, Width = 70 };
        r1.Controls.Add(_h);
        var r2 = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        r2.Controls.Add(new Label { Text = "垂直傾斜：", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        _v = new NumericUpDown { Minimum = -89, Maximum = 89, Width = 70 };
        r2.Controls.Add(_v);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Width = 240, Height = 40 };
        var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, Width = 75 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 75 };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel);

        layout.Controls.AddRange(new Control[] { r1, r2, buttons });
        Controls.Add(layout);
        AcceptButton = ok; CancelButton = cancel;
    }
}

/// <summary>任意角度旋轉（原版 RotateCustomDialog）。</summary>
public sealed class RotateCustomDialog : Form
{
    private readonly NumericUpDown _angle;
    public double Angle => (double)_angle.Value;

    public RotateCustomDialog()
    {
        Text = "旋轉";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(260, 120);

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var r1 = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        r1.Controls.Add(new Label { Text = "角度（逆時針為正）：", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        _angle = new NumericUpDown { Minimum = -359, Maximum = 359, Width = 80 };
        r1.Controls.Add(_angle);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Width = 240, Height = 40 };
        var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, Width = 75 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 75 };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
        layout.Controls.AddRange(new Control[] { r1, buttons });
        Controls.Add(layout);
        AcceptButton = ok; CancelButton = cancel;
    }
}

/// <summary>色彩調整（色相／飽和度／亮度／對比）＋即時預覽。</summary>
public sealed class AdjustDialog : Form
{
    private readonly TrackBar _hue, _sat, _bright, _contrast;
    private readonly Label _previewHolder = new();
    private readonly PictureBox _preview;
    private readonly Bitmap _source;
    private Bitmap? _previewBmp;

    public float Hue => _hue.Value / 1f;
    public float Saturation => _sat.Value;
    public float Brightness => _bright.Value;
    public float Contrast => _contrast.Value;

    public AdjustDialog(Bitmap target)
    {
        _source = target;
        Text = "調整顏色";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, 300);

        int maxPreview = 180;
        double scale = Math.Min(1, (double)maxPreview / Math.Max(_source.Width, _source.Height));
        _preview = new PictureBox
        {
            Size = new Size(Math.Max(1, (int)(_source.Width * scale)), Math.Max(1, (int)(_source.Height * scale))),
            Location = new Point(280, 14),
            SizeMode = PictureBoxSizeMode.StretchImage,
            BorderStyle = BorderStyle.FixedSingle,
        };

        _hue = AddTrack("色相", -180, 180, 0, 20);
        _sat = AddTrack("飽和度", -100, 100, 0, 80);
        _bright = AddTrack("亮度", -100, 100, 0, 140);
        _contrast = AddTrack("對比", -100, 100, 0, 200);

        var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, Width = 80, Location = new Point(280, 250) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 80, Location = new Point(370, 250) };
        var reset = new Button { Text = "重設", Width = 80, Location = new Point(280, 210) };
        reset.Click += (_, _) => { _hue.Value = _sat.Value = _bright.Value = _contrast.Value = 0; };

        Controls.AddRange(new Control[] { _preview, ok, cancel, reset });
        AcceptButton = ok; CancelButton = cancel;
        foreach (var t in new[] { _hue, _sat, _bright, _contrast })
            t.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private TrackBar AddTrack(string label, int min, int max, int val, int y)
    {
        Controls.Add(new Label { Text = label, Location = new Point(14, y), AutoSize = true });
        var t = new TrackBar { Minimum = min, Maximum = max, Value = val, Location = new Point(80, y - 6), Width = 180, TickStyle = TickStyle.None };
        Controls.Add(t);
        return t;
    }

    private void UpdatePreview()
    {
        try
        {
            using var small = ImageOps.Resize(_source, Math.Max(1, _preview.Width), Math.Max(1, _preview.Height), true);
            var result = ImageOps.Adjust(small, Hue, Saturation, Brightness, Contrast);
            _previewBmp?.Dispose();
            _previewBmp = result;
            _preview.Image = result;
        }
        catch { /* 預覽失敗不影響主流程 */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewBmp?.Dispose();
            _preview.Dispose();
        }
        base.Dispose(disposing);
    }
}

public sealed class ImageInfoDialog : Form
{
    public ImageInfoDialog(PaintClone.Documents.PaintDocument doc)
    {
        Text = "圖像資訊";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(300, 190);

        long bytes = (long)doc.Size.Width * doc.Size.Height * 4;
        var info = new Label
        {
            Location = new Point(14, 12),
            AutoSize = false,
            Size = new Size(275, 130),
            Text = $"圖像大小：{doc.Size.Width} x {doc.Size.Height} 像素\n" +
                   $"圖層數：{doc.Layers.Count}\n" +
                   $"色彩深度：32 位元 (RGBA)\n" +
                   $"位元組大小：{bytes:N0} 位元組\n" +
                   $"檔案：{(doc.FilePath is null ? "(尚未儲存)" : Path.GetFileName(doc.FilePath))}\n" +
                   $"儲存格式：{(doc.FilePath is null ? "-" : Path.GetExtension(doc.FilePath).ToUpperInvariant())}",
        };
        var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, Width = 80, Location = new Point(195, 150) };
        Controls.AddRange(new Control[] { info, ok });
        AcceptButton = ok;
    }
}

public sealed class AboutDialog : Form
{
    public AboutDialog()
    {
        Text = "關於 小畫家 Plus";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(380, 220);
        var label = new Label
        {
            Location = new Point(16, 14),
            Size = new Size(350, 160),
            Text = "小畫家 Plus 1.0\n" +
                   "以 .NET 10 (WinForms) 重建 Windows 小畫家\n\n" +
                   "功能依據 MS Paint 11.2605.81.0 之靜態逆向研究重建\n" +
                   "（見 docs/PAINT_FEATURES.md）\n\n" +
                   "優化：100 步復原、填色容差、圖層、自動存檔、\n" +
                   "1%–3200% 縮放、Ctrl+滾輪定點縮放、透明 checkerboard",
        };
        var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, Width = 80, Location = new Point(284, 180) };
        Controls.AddRange(new Control[] { label, ok });
        AcceptButton = ok;
    }
}
