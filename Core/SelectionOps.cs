using System.Drawing.Drawing2D;
using PaintClone.Documents;
using PaintClone.Rendering;
using PaintClone.Tools;

namespace PaintClone.Core;

/// <summary>選取相關操作，對應原版編輯選單（剪下／複製／貼上／刪除／裁剪／在選取內旋轉翻轉）。</summary>
public static class SelectionOps
{
    public static bool HasSelection(CanvasControl canvas) => canvas.Selection.IsActive;

    public static Bitmap GetContent(CanvasControl canvas) => canvas.Selection.GetContent(canvas.Doc);

    public static void Copy(CanvasControl canvas)
    {
        if (!canvas.Selection.IsActive) return;
        using var bmp = GetContent(canvas);
        Clipboard.SetImage(bmp);
    }

    public static void Cut(CanvasControl canvas)
    {
        if (!canvas.Selection.IsActive) return;
        Copy(canvas);
        ClearRegion(canvas);
    }

    /// <summary>刪除鍵：原版以顏色2填滿；非背景層擦為透明（優化）。</summary>
    public static void Delete(CanvasControl canvas)
    {
        if (!canvas.Selection.IsActive) return;
        ClearRegion(canvas);
    }

    private static void ClearRegion(CanvasControl canvas)
    {
        var doc = canvas.Doc;
        var sel = canvas.Selection;
        var scope = doc.BeginEdit();
        using (var g = Graphics.FromImage(doc.Active.Image))
        using (var region = sel.GetRegion())
        {
            g.SetClip(region, CombineMode.Replace);
            if (doc.Active.IsBackground)
            {
                using var b = new SolidBrush(canvas.Color2);
                g.FillRectangle(b, sel.Bounds);
            }
            else
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                using var t = new SolidBrush(Color.Transparent);
                g.FillRectangle(t, sel.Bounds);
            }
        }
        scope.AddDirty(sel.Bounds);
        scope.Commit();
        canvas.Invalidate();
    }

    /// <summary>貼上：建立新圖層（優化：原版會阻塞的浮動選取在圖層制下更直覺）。</summary>
    public static void PasteImage(CanvasControl canvas, Bitmap bmp)
    {
        var doc = canvas.Doc;
        var snap = doc.TakeSnapshotInternal();
        var layer = new Layer("貼上", ImageOps.NewCanvas(doc.Size.Width, doc.Size.Height, Color.Transparent));
        using (var g = Graphics.FromImage(layer.Image))
        {
            var pos = new Point(
                (doc.Size.Width - bmp.Width) / 2,
                (doc.Size.Height - bmp.Height) / 2);
            if (canvas.Selection.IsActive)
                pos = canvas.Selection.Bounds.Location;
            g.DrawImageUnscaled(bmp, pos);
        }
        doc.Layers.Insert(Math.Min(doc.ActiveLayerIndex + 1, doc.Layers.Count), layer);
        doc.PushStructuralUndo(snap);
        doc.SetActiveLayer(doc.Layers.IndexOf(layer));
    }

    public static void CropToSelection(CanvasControl canvas)
    {
        if (!canvas.Selection.IsActive) return;
        var rect = canvas.Selection.Bounds;
        if (rect.Width < 1 || rect.Height < 1) return;
        canvas.Doc.ApplyGlobal(img => ImageOps.Crop(img, rect));
        canvas.Selection.Clear();
    }

    /// <summary>裁剪至選取以外版本：保留畫布大小、只保留選取內容（原版「裁剪」是裁掉外面）。</summary>
    public static void RotateSelection90(CanvasControl canvas, bool clockwise)
    {
        var doc = canvas.Doc;
        var sel = canvas.Selection;
        if (!sel.IsActive) return;
        var content = GetContent(canvas);

        var scope = doc.BeginEdit();
        var oldBounds = sel.Bounds;
        // 先清掉原內容
        ClearRegionQuiet(canvas, oldBounds);
        var center = new PointF(oldBounds.X + oldBounds.Width / 2f, oldBounds.Y + oldBounds.Height / 2f);
        var target = new Rectangle(
            (int)Math.Round(center.X - content.Width / 2f),
            (int)Math.Round(center.Y - content.Height / 2f),
            content.Width, content.Height);
        using (var g = Graphics.FromImage(doc.Active.Image))
            g.DrawImageUnscaled(content, target);
        content.Dispose();
        scope.AddDirty(Rectangle.Union(oldBounds, target));
        scope.Commit();
        sel.SetRect(Rectangle.Intersect(target, new Rectangle(0, 0, doc.Size.Width, doc.Size.Height)));
        canvas.Invalidate();
    }

    public static void FlipSelection(CanvasControl canvas, bool horizontal)
    {
        var doc = canvas.Doc;
        var sel = canvas.Selection;
        if (!sel.IsActive) return;
        var content = GetContent(canvas);
        content.RotateFlip(horizontal ? RotateFlipType.RotateNoneFlipX : RotateFlipType.RotateNoneFlipY);
        var scope = doc.BeginEdit();
        var bounds = sel.Bounds;
        ClearRegionQuiet(canvas, bounds);
        using (var g = Graphics.FromImage(doc.Active.Image))
            g.DrawImageUnscaled(content, bounds);
        content.Dispose();
        scope.AddDirty(bounds);
        scope.Commit();
        canvas.Invalidate();
    }

    private static void ClearRegionQuiet(CanvasControl canvas, Rectangle bounds)
    {
        var doc = canvas.Doc;
        var sel = canvas.Selection;
        using var g = Graphics.FromImage(doc.Active.Image);
        using var region = sel.GetRegion();
        g.SetClip(region, CombineMode.Replace);
        if (doc.Active.IsBackground)
        {
            using var b = new SolidBrush(canvas.Color2);
            g.FillRectangle(b, bounds);
        }
        else
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            using var t = new SolidBrush(Color.Transparent);
            g.FillRectangle(t, bounds);
        }
    }

    /// <summary>調整圖片大小：有選取時只縮放選取內容（與原版一致）。</summary>
    public static void ResizeImage(CanvasControl canvas, int w, int h, bool smoothing)
    {
        var doc = canvas.Doc;
        if (doc.Active is null) return;
        if (canvas.Selection.IsActive && canvas.Selection.Mode == SelectionManager.SelMode.Rect)
        {
            var rect = canvas.Selection.Bounds;
            var content = canvas.Selection.GetContent(doc);
            var resized = ImageOps.Resize(content, w, h, smoothing);
            content.Dispose();
            var target = new Rectangle(rect.X, rect.Y, w, h);
            var scope = doc.BeginEdit();
            using (var g = Graphics.FromImage(doc.Active.Image))
                g.DrawImageUnscaled(resized, target);
            resized.Dispose();
            scope.AddDirty(Rectangle.Union(rect, target));
            scope.Commit();
            canvas.Selection.SetRect(Rectangle.Intersect(target, new Rectangle(0, 0, doc.Size.Width, doc.Size.Height)));
            canvas.Invalidate();
        }
        else
        {
            doc.ApplyGlobal(img => ImageOps.Resize(img, w, h, smoothing));
            canvas.Selection.Clear();
        }
    }

    public static void SkewImage(CanvasControl canvas, double hDeg, double vDeg)
    {
        canvas.Doc.ApplyGlobal(img => ImageOps.Skew(img, hDeg, vDeg));
        canvas.Selection.Clear();
    }

    public static void RotateImage(CanvasControl canvas, double degrees)
    {
        canvas.Doc.ApplyGlobal(img => ImageOps.Rotate(img, degrees));
        canvas.Selection.Clear();
    }

    public static void FlipImage(CanvasControl canvas, bool horizontal)
    {
        if (canvas.Selection.IsActive) { FlipSelection(canvas, horizontal); return; }
        canvas.Doc.ApplyGlobal(img => ImageOps.Flip(img, horizontal));
    }

    public static void EffectInvert(CanvasControl canvas)
    {
        if (canvas.Selection.IsActive)
        {
            var sel = canvas.Selection;
            var content = GetContent(canvas);
            var result = ImageOps.Invert(content);
            content.Dispose();
            var scope = canvas.Doc.BeginEdit();
            using (var g = Graphics.FromImage(canvas.Doc.Active.Image))
                g.DrawImageUnscaled(result, sel.Bounds);
            result.Dispose();
            scope.AddDirty(sel.Bounds);
            scope.Commit();
            canvas.Invalidate();
        }
        else canvas.Doc.ApplyGlobal(ImageOps.Invert);
    }

    public static void EffectGrayscale(CanvasControl canvas)
    {
        if (canvas.Selection.IsActive)
        {
            var sel = canvas.Selection;
            var content = GetContent(canvas);
            var result = ImageOps.Grayscale(content);
            content.Dispose();
            var scope = canvas.Doc.BeginEdit();
            using (var g = Graphics.FromImage(canvas.Doc.Active.Image))
                g.DrawImageUnscaled(result, sel.Bounds);
            result.Dispose();
            scope.AddDirty(sel.Bounds);
            scope.Commit();
            canvas.Invalidate();
        }
        else canvas.Doc.ApplyGlobal(ImageOps.Grayscale);
    }

    public static void ApplyAdjust(CanvasControl canvas, float hue, float sat, float bright, float contrast)
    {
        if (canvas.Selection.IsActive)
        {
            var sel = canvas.Selection;
            var content = GetContent(canvas);
            var result = ImageOps.Adjust(content, hue, sat, bright, contrast);
            content.Dispose();
            var scope = canvas.Doc.BeginEdit();
            using (var g = Graphics.FromImage(canvas.Doc.Active.Image))
                g.DrawImageUnscaled(result, sel.Bounds);
            result.Dispose();
            scope.AddDirty(sel.Bounds);
            scope.Commit();
            canvas.Invalidate();
        }
        else canvas.Doc.ApplyGlobal(img => ImageOps.Adjust(img, hue, sat, bright, contrast));
    }
}
