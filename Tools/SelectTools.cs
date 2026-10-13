using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PaintClone.Core;
using PaintClone.Documents;
using PaintClone.Rendering;

namespace PaintClone.Tools;

/// <summary>選取工具：矩形選取 + 拖曳移動 + 8 個縮放把手（對應原版 ModifiableToolOperation）。</summary>
public sealed class SelectTool : Tool
{
    private enum State { Idle, MakingRect, Ready, Moving, Scaling }
    private State _state;
    private PointF _anchor;
    private Rectangle _tempRect;

    private Bitmap? _float;
    private RectangleF _floatTarget;      // 浮動內容目前的目標矩形
    private RectangleF _floatSourceRect;  // 原始選取矩形
    private PointF _grabOffset;
    private EditScopeHandle? _moveScope;
    private int _handle = -1;
    private Region? _floatRegion;

    public override string Name => "選取";
    public override string Hint => "選取：拖曳建立矩形選取；於選取內拖曳可移動，拖曳把手可縮放";
    public override Cursor Cursor => Cursors.Default;

    private RectangleF SelectionScreenBounds() => Sel.Bounds;

    // 8 個把手：0-3 四角、4-7 四邊中點
    private static PointF[] Handles(RectangleF b) => new[]
    {
        new PointF(b.Left, b.Top), new PointF(b.Right, b.Top),
        new PointF(b.Right, b.Bottom), new PointF(b.Left, b.Bottom),
        new PointF((b.Left + b.Right) / 2, b.Top), new PointF(b.Right, (b.Top + b.Bottom) / 2),
        new PointF((b.Left + b.Right) / 2, b.Bottom), new PointF(b.Left, (b.Top + b.Bottom) / 2),
    };

    private int HitHandle(PointF p)
    {
        if (_state != State.Ready || !Sel.IsActive) return -1;
        float tol = 6 / (float)Math.Max(0.05, Canvas.Zoom);
        var hs = Handles(Sel.Bounds);
        for (int i = 0; i < hs.Length; i++)
            if (Math.Abs(hs[i].X - p.X) <= tol && Math.Abs(hs[i].Y - p.Y) <= tol)
                return i;
        return -1;
    }

    public override void OnMouseDown(PointF p, MouseButtons btn)
    {
        if (btn != MouseButtons.Left) return;
        if (_state is State.Moving or State.Scaling) return;

        if (_state == State.Ready && Sel.IsActive)
        {
            _handle = HitHandle(p);
            if (_handle >= 0) { BeginFloat(p, scaling: true); return; }
            if (Sel.Contains(Point.Round(p))) { BeginFloat(p, scaling: false); return; }
        }

        _state = State.MakingRect;
        _anchor = p;
        _tempRect = Rectangle.Empty;
    }

    private void BeginFloat(PointF p, bool scaling)
    {
        _moveScope = new EditScopeHandle(Doc.BeginEdit());
        var bounds = Sel.Bounds;
        _floatSourceRect = bounds;
        _float = Sel.GetContent(Doc);
        using (var r = Sel.GetRegion())
            _floatRegion = r.Clone() as Region;

        // 挖洞：背景層以顏色2（經典行為）、其他圖層擦透明（優化）
        using (var g = Graphics.FromImage(Doc.Active.Image))
        using (var region = Sel.GetRegion())
        {
            g.SetClip(region, CombineMode.Replace);
            if (Doc.Active.IsBackground)
            {
                g.CompositingMode = CompositingMode.SourceOver;
                using var b = new SolidBrush(C2);
                g.FillRectangle(b, bounds);
            }
            else
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                using var t = new SolidBrush(Color.Transparent);
                g.FillRectangle(t, bounds);
            }
        }

        if (scaling)
        {
            _state = State.Scaling;
            _floatTarget = bounds;
        }
        else
        {
            _state = State.Moving;
            _grabOffset = new PointF(p.X - bounds.X, p.Y - bounds.Y);
            _floatTarget = bounds;
        }
        Canvas.Invalidate();
    }

    public override void OnMouseMove(PointF p, MouseButtons btn)
    {
        switch (_state)
        {
            case State.MakingRect:
                _tempRect = Rectangle.Intersect(
                    Rectangle.Round(new RectangleF(
                        Math.Min(_anchor.X, p.X), Math.Min(_anchor.Y, p.Y),
                        Math.Abs(p.X - _anchor.X), Math.Abs(p.Y - _anchor.Y))),
                    new Rectangle(0, 0, Doc.Size.Width, Doc.Size.Height));
                Canvas.Invalidate();
                break;

            case State.Moving:
                _floatTarget = new RectangleF(
                    Math.Clamp(p.X - _grabOffset.X, -_floatSourceRect.Width / 2, Doc.Size.Width - _floatSourceRect.Width / 2),
                    Math.Clamp(p.Y - _grabOffset.Y, -_floatSourceRect.Height / 2, Doc.Size.Height - _floatSourceRect.Height / 2),
                    _floatSourceRect.Width, _floatSourceRect.Height);
                Canvas.Invalidate();
                break;

            case State.Scaling:
                _floatTarget = ComputeScale(_floatSourceRect, _handle, p);
                Canvas.Invalidate();
                break;
        }
    }

    private static RectangleF ComputeScale(RectangleF s, int handle, PointF p)
    {
        float l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;
        switch (handle)
        {
            case 0: l = p.X; t = p.Y; break;
            case 1: r = p.X; t = p.Y; break;
            case 2: r = p.X; b = p.Y; break;
            case 3: l = p.X; b = p.Y; break;
            case 4: t = p.Y; break;
            case 5: r = p.X; break;
            case 6: b = p.Y; break;
            case 7: l = p.X; break;
        }
        return RectangleF.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
    }

    public override void OnMouseUp(PointF p, MouseButtons btn)
    {
        switch (_state)
        {
            case State.MakingRect:
                if (_tempRect.Width >= 1 && _tempRect.Height >= 1)
                {
                    Sel.SetRect(_tempRect);
                    _state = State.Ready;
                }
                else { Sel.Clear(); _state = State.Idle; }
                break;

            case State.Moving:
            case State.Scaling:
                FinishFloat(scaling: _state == State.Scaling);
                _state = State.Ready;
                break;
        }
    }

    private void FinishFloat(bool scaling)
    {
        if (_moveScope is null || _float is null) return;
        var target = Rectangle.Round(_floatTarget);
        target.Width = Math.Max(1, target.Width);
        target.Height = Math.Max(1, target.Height);

        using var g = Graphics.FromImage(Doc.Active.Image);
        if (_floatRegion is not null)
        {
            using var movedRegion = _floatRegion.Clone() as Region;
            using var m = new Matrix();
            m.Translate(target.X - _floatSourceRect.X, target.Y - _floatSourceRect.Y);
            movedRegion!.Transform(m);
            g.SetClip(movedRegion, CombineMode.Replace);
        }
        g.InterpolationMode = scaling
            ? (_floatTarget.Width > _floatSourceRect.Width ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic)
            : InterpolationMode.NearestNeighbor;
        g.DrawImage(_float, target);

        _moveScope.AddDirty(Rectangle.Union(Rectangle.Round(_floatSourceRect), target));
        _moveScope.Commit();
        _moveScope = null;
        _float?.Dispose(); _float = null;
        _floatRegion?.Dispose(); _floatRegion = null;

        if (scaling && Sel.Mode == SelectionManager.SelMode.Rect)
            Sel.SetRect(target);
        else
            Sel.Clear();
        Canvas.Invalidate();
    }

    public override void Preview(Graphics g)
    {
        if (_state == State.MakingRect && _tempRect.Width > 0)
        {
            using var pen = new Pen(Color.Black, 1 / (float)Math.Max(0.05, Canvas.Zoom))
            { DashPattern = new float[] { 3, 3 } };
            g.DrawRectangle(pen, _tempRect.X, _tempRect.Y, _tempRect.Width, _tempRect.Height);
        }
        else if ((_state == State.Moving || _state == State.Scaling) && _float is not null)
        {
            var target = Rectangle.Round(_floatTarget);
            if (_floatRegion is not null)
            {
                using var moved = _floatRegion.Clone() as Region;
                using var m = new Matrix();
                m.Translate(target.X - _floatSourceRect.X, target.Y - _floatSourceRect.Y);
                moved!.Transform(m);
                g.SetClip(moved, CombineMode.Replace);
            }
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_float, target);
        }
    }

    public override void OnDeactivate()
    {
        if (_state is State.Moving or State.Scaling)
            FinishFloat(scaling: _state == State.Scaling);
        _state = Sel.IsActive ? State.Ready : State.Idle;
    }

    public override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _state == State.MakingRect)
        {
            _state = State.Idle;
            Canvas.Invalidate();
            e.Handled = true;
        }
    }

    public override bool WantsKeys => true;

    /// <summary>由外部（Edit 選單）設定選取。</summary>
    public void SetExternalSelection() => _state = Sel.IsActive ? State.Ready : State.Idle;
}

/// <summary>任選（套索）工具：對應原版 MaskBasedSelectionOperation。</summary>
public sealed class LassoTool : Tool
{
    private readonly List<PointF> _pts = new();
    private bool _drawing;

    public override string Name => "任選";
    public override string Hint => "任選：按住滑鼠畫封閉曲線，放開後選取圈內區域";
    public override Cursor Cursor => Cursors.Cross;

    public override void OnMouseDown(PointF p, MouseButtons btn)
    {
        if (btn != MouseButtons.Left) return;
        _drawing = true;
        _pts.Clear();
        _pts.Add(p);
    }

    public override void OnMouseMove(PointF p, MouseButtons btn)
    {
        if (!_drawing) return;
        var last = _pts[^1];
        float tol = 2 / (float)Math.Max(0.05, Canvas.Zoom);
        if (Math.Abs(p.X - last.X) > tol || Math.Abs(p.Y - last.Y) > tol)
            _pts.Add(p);
        Canvas.Invalidate();
    }

    public override void OnMouseUp(PointF p, MouseButtons btn)
    {
        if (!_drawing) return;
        _drawing = false;
        if (_pts.Count > 4)
        {
            var path = new GraphicsPath();
            path.AddPolygon(_pts.ToArray());
            Sel.SetLasso(path);
        }
        else Sel.Clear();
        _pts.Clear();
        Canvas.Invalidate();
    }

    public override void Preview(Graphics g)
    {
        if (!_drawing || _pts.Count < 2) return;
        using var pen = new Pen(Color.Black, 1 / (float)Math.Max(0.05, Canvas.Zoom))
        { DashPattern = new float[] { 3, 3 } };
        g.DrawLines(pen, _pts.ToArray());
    }

    public override void OnDeactivate()
    {
        _drawing = false;
        _pts.Clear();
    }
}

/// <summary>文字工具：畫布上即時編輯；優化了原版的「不透明背景」限制（可選透明底色）。</summary>
public sealed class TextTool : Tool
{
    private TextBox? _editor;
    private PointF _imgPos;
    private bool _committing;

    public override string Name => "文字";
    public override string Hint => "文字：點擊畫布輸入；Esc 取消、點擊他處確定";
    public override Cursor Cursor => Cursors.IBeam;

    public override void OnMouseDown(PointF p, MouseButtons btn)
    {
        if (btn != MouseButtons.Left) return;
        CommitEditor();
        CreateEditor(p);
    }

    private void CreateEditor(PointF p)
    {
        _imgPos = p;
        var screen = Canvas.ToScreen(p);
        var sample = Doc.Compose().GetPixel(
            Math.Clamp((int)p.X, 0, Math.Max(0, Doc.Size.Width - 1)),
            Math.Clamp((int)p.Y, 0, Math.Max(0, Doc.Size.Height - 1)));
        Color bg = Opt.TextTransparent
            ? Color.FromArgb(sample.A == 0 ? 255 : sample.A, sample.R, sample.G, sample.B)
            : C2;

        _editor = new TextBox
        {
            Multiline = true,
            BorderStyle = BorderStyle.FixedSingle,
            AcceptsReturn = true,
            Font = new Font(Opt.TextFont.FontFamily, Opt.TextFont.Size * (float)Canvas.Zoom),
            ForeColor = C1,
            BackColor = bg,
            MaximumSize = new Size(Canvas.Width - Math.Max(0, (int)screen.X), 0),
        };
        _editor.Location = new Point((int)screen.X, (int)screen.Y);
        _editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { AbortEditor(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && !e.Shift && !_editor!.Multiline) CommitEditor();
        };
        _editor.TextChanged += (_, _) => AutoSizeEditor();
        _editor.LostFocus += (_, _) => { if (!_committing) CommitEditor(); };
        Canvas.Controls.Add(_editor);
        _editor.BringToFront();
        _editor.Focus();
        AutoSizeEditor();
    }

    private void AutoSizeEditor()
    {
        if (_editor is null) return;
        var size = TextRenderer.MeasureText(_editor.Text.Length > 0 ? _editor.Text : " ", _editor.Font,
            Size.Empty, TextFormatFlags.NoPadding);
        _editor.Size = new Size(Math.Max(40, size.Width + 8), Math.Max(_editor.Font.Height + 8, size.Height + 6));
    }

    public override void OnViewChanged()
    {
        if (_editor is null) return;
        var screen = Canvas.ToScreen(_imgPos);
        _editor.Location = new Point((int)screen.X, (int)screen.Y);
        _editor.Font = new Font(Opt.TextFont.FontFamily, Opt.TextFont.Size * (float)Canvas.Zoom);
    }

    private void AbortEditor()
    {
        if (_editor is null) return;
        _committing = true;
        RemoveEditor();
        _committing = false;
    }

    private void RemoveEditor()
    {
        if (_editor is null) return;
        Canvas.Controls.Remove(_editor);
        _editor.Dispose();
        _editor = null;
        Canvas.Invalidate();
    }

    private void CommitEditor()
    {
        if (_editor is null) return;
        var text = _editor.Text;
        _committing = true;
        RemoveEditor();
        _committing = false;
        if (string.IsNullOrEmpty(text)) return;
        var scope = new EditScopeHandle(Doc.BeginEdit());
        using var g = Graphics.FromImage(Doc.Active.Image);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.LineLimit;
        var size = g.MeasureString(text, Opt.TextFont, int.MaxValue, fmt);
        var rect = new RectangleF(_imgPos.X, _imgPos.Y, size.Width, size.Height);

        if (!Opt.TextTransparent)
        {
            using var bgb = new SolidBrush(C2);
            g.FillRectangle(bgb, Rectangle.Round(new RectangleF(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4)));
        }
        using (var brush = new SolidBrush(C1))
            g.DrawString(text, Opt.TextFont, brush, _imgPos, fmt);

        scope.AddDirty(Rectangle.Round(new RectangleF(rect.X - 4, rect.Y - 4, rect.Width + 8, rect.Height + 8)));
        scope.Commit();
        Canvas.Invalidate();
    }

    public override void OnDeactivate() => CommitEditor();

    /// <summary>外部要求提交目前輸入（如 undo 前）。</summary>
    public void CommitPending() => CommitEditor();

    public override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) AbortEditor();
    }

    public override bool WantsKeys => true;
}
