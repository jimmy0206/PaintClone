using PaintClone.Core;
using System.Drawing.Imaging;

namespace PaintClone.Documents;

public sealed class Layer
{
    public string Name;
    public Bitmap Image;          // 一律 Format32bppArgb
    public bool Visible = true;
    public bool IsBackground;     // 背景層：橡皮擦以顏色2擦除（經典行為）

    public Layer(string name, Bitmap image, bool isBackground = false)
    { Name = name; Image = image; IsBackground = isBackground; }

    public Layer CloneLayer() => new(Name, Image.ToArgb32(), IsBackground) { Visible = Visible };
}

/// <summary>一次編輯的範圍：開始時快取舊像素，提交時只保留髒矩形（記憶體優化）。</summary>
public sealed class EditScope
{
    private readonly PaintDocument _doc;
    private readonly Bitmap _capture;
    private Rectangle _dirty = Rectangle.Empty;
    private bool _finished;

    internal EditScope(PaintDocument doc)
    {
        _doc = doc;
        _capture = doc.Active.Image.ToArgb32();
    }

    public void AddDirty(Rectangle r)
    {
        r.Inflate(2, 2);
        _dirty = _dirty.IsEmpty ? r : Rectangle.Union(_dirty, r);
    }

    public void AddDirty(Point p) => AddDirty(new Rectangle(p.X, p.Y, 1, 1));

    public void Commit()
    {
        if (_finished) return;
        _finished = true;
        var rect = Rectangle.Intersect(_dirty, new Rectangle(0, 0, _capture.Width, _capture.Height));
        if (rect.Width > 0 && rect.Height > 0)
            _doc.PushRectUndo(_capture, rect);
        else
            _capture.Dispose();
    }

    public void Abort()
    {
        if (_finished) return;
        _finished = true;
        _capture.Dispose();
    }
}

internal abstract class UndoEntry
{
    /// <summary>還原／重做：與目前狀態互換。</summary>
    public abstract void Swap(PaintDocument doc);
}

/// <summary>單圖層髒矩形 undo：以 swap 方式同時服務 undo 與 redo，省一半記憶體。</summary>
internal sealed class RectUndoEntry : UndoEntry
{
    private readonly Layer _layer;
    private readonly Rectangle _rect;
    private uint[] _pixels;

    public RectUndoEntry(Layer layer, Rectangle rect, uint[] pixels)
    { _layer = layer; _rect = rect; _pixels = pixels; }

    private static unsafe uint[] ReadRect(Bitmap bmp, Rectangle rect)
    {
        var buf = new byte[rect.Height * rect.Width * 4];
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* p = (byte*)data.Scan0;
            fixed (byte* d = buf)
                for (int y = 0; y < rect.Height; y++)
                    Buffer.MemoryCopy(p + (long)y * data.Stride, d + (long)y * rect.Width * 4,
                        rect.Width * 4L, rect.Width * 4L);
        }
        finally { bmp.UnlockBits(data); }
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(buf.AsSpan()).ToArray();
    }

    private static unsafe void WriteRect(Bitmap bmp, Rectangle rect, uint[] pixels)
    {
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* p = (byte*)data.Scan0;
            fixed (uint* src = pixels)
                for (int y = 0; y < rect.Height; y++)
                    Buffer.MemoryCopy(src + (long)y * rect.Width, p + (long)y * data.Stride,
                        data.Stride, rect.Width * 4L);
        }
        finally { bmp.UnlockBits(data); }
    }

    public override void Swap(PaintDocument doc)
    {
        var current = ReadRect(_layer.Image, _rect);
        WriteRect(_layer.Image, _rect, _pixels);
        _pixels = current;
    }
}

internal sealed record LayerSnapshot(string Name, bool Visible, bool IsBackground, Bitmap Image);

/// <summary>整份文件快照 undo（結構變動：旋轉/調整大小/圖層增刪）。</summary>
internal sealed class WholeDocSnapshot : UndoEntry
{
    private List<LayerSnapshot> _layers;
    private int _activeIndex;

    public WholeDocSnapshot(List<LayerSnapshot> layers, int activeIndex)
    { _layers = layers; _activeIndex = activeIndex; }

    public override void Swap(PaintDocument doc)
    {
        var current = doc.Layers
            .Select(l => new LayerSnapshot(l.Name, l.Visible, l.IsBackground, l.Image.ToArgb32()))
            .ToList();
        int curActive = doc.ActiveLayerIndex;

        foreach (var l in doc.Layers) l.Image.Dispose();
        doc.Layers.Clear();
        foreach (var s in _layers)
            doc.Layers.Add(new Layer(s.Name, s.Image.ToArgb32(), s.IsBackground) { Visible = s.Visible });
        doc.SetActiveLayerSafe(_activeIndex);

        foreach (var s in _layers) s.Image.Dispose();
        _layers = current;
        _activeIndex = curActive;
        doc.RaiseStructure();
    }
}

/// <summary>繪圖文件：圖層堆疊 + undo/redo 堆疊。undo 上限 100（原版僅 3～10，此為優化）。</summary>
public sealed class PaintDocument : IDisposable
{
    public const int MaxUndoSteps = 100;

    public List<Layer> Layers { get; } = new();
    public int ActiveLayerIndex { get; private set; }
    public Layer Active => Layers[ActiveLayerIndex];
    public Size Size => Layers.Count > 0 ? Layers[0].Image.Size : Size.Empty;
    public string? FilePath { get; set; }
    public bool IsDirty { get; private set; }

    public event EventHandler? Changed;           // 像素變動
    public event EventHandler? StructureChanged;  // 圖層/文件結構變動

    private readonly LinkedList<UndoEntry> _undo = new();
    private readonly LinkedList<UndoEntry> _redo = new();
    private Bitmap? _composed;

    public PaintDocument() => New(new Size(1280, 720), Color.White);

    public void New(Size size, Color background)
    {
        foreach (var l in Layers) l.Image.Dispose();
        Layers.Clear();
        Layers.Add(new Layer("背景", ImageOps.NewCanvas(size.Width, size.Height, background), isBackground: true));
        ActiveLayerIndex = 0;
        _undo.Clear(); _redo.Clear();
        FilePath = null;
        IsDirty = false;
        InvalidateComposed();
        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>從外部載入（單一圖層）。</summary>
    public void LoadFrom(Bitmap bmp, string? path)
    {
        foreach (var l in Layers) l.Image.Dispose();
        Layers.Clear();
        Layers.Add(new Layer("背景", bmp.ToArgb32(), isBackground: true));
        ActiveLayerIndex = 0;
        _undo.Clear(); _redo.Clear();
        FilePath = path;
        IsDirty = false;
        InvalidateComposed();
        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 圖層堆疊的合成結果。傳回的是「文件持有」的快取物件：呼叫端只能讀取，不得 Dispose，
    /// 否則之後所有用到 Compose 的功能（取色、調整顏色、存檔、圖像資訊）都會丟
    /// ArgumentException("Parameter is not valid")。需要能自行釋放的副本請用 ComposeCopy。
    /// </summary>
    public Bitmap Compose()
    {
        // 自我修復：萬一快取被呼叫端誤 Dispose，重建而不是讓後續功能全部崩潰
        if (_composed is not null && CacheAlive(_composed)) return _composed;
        _composed = null;
        var bmp = new Bitmap(Math.Max(1, Size.Width), Math.Max(1, Size.Height), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            foreach (var l in Layers)
                if (l.Visible) g.DrawImageUnscaled(l.Image, 0, 0);
        }
        _composed = bmp;
        return bmp;
    }

    /// <summary>合成的獨立副本，呼叫端負責 Dispose。</summary>
    public Bitmap ComposeCopy() => Compose().ToArgb32();

    /// <summary>GDI+ 沒有 Image.IsDisposed；已釋放的位圖存取任何屬性都會丟 ArgumentException，只能先存取一次試探。</summary>
    private static bool CacheAlive(Image img)
    {
        try { _ = img.Width; return true; }
        catch (ArgumentException) { return false; }
    }

    public void InvalidateComposed()
    {
        _composed?.Dispose();
        _composed = null;
    }

    public void SetActiveLayerSafe(int index) => ActiveLayerIndex = Math.Clamp(index, 0, Layers.Count - 1);

    public void SetActiveLayer(int index)
    {
        SetActiveLayerSafe(index);
        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------- 編輯

    public EditScope BeginEdit() => new(this);

    internal void PushRectUndo(Bitmap capture, Rectangle rect)
    {
        var pixels = ReadRectStatic(capture, rect);
        capture.Dispose();
        PushUndo(new RectUndoEntry(Active, rect, pixels));
        IsDirty = true;
        InvalidateComposed();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static unsafe uint[] ReadRectStatic(Bitmap bmp, Rectangle rect)
    {
        var buf = new byte[rect.Height * rect.Width * 4];
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* p = (byte*)data.Scan0;
            fixed (byte* d = buf)
                for (int y = 0; y < rect.Height; y++)
                    Buffer.MemoryCopy(p + (long)y * data.Stride, d + (long)y * rect.Width * 4,
                        rect.Width * 4L, rect.Width * 4L);
        }
        finally { bmp.UnlockBits(data); }
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(buf.AsSpan()).ToArray();
    }

    /// <summary>結構性操作（新增/刪除圖層、調整大小、旋轉…）前呼叫，取回快照。</summary>
    internal WholeDocSnapshot TakeSnapshotInternal() =>
        new(Layers.Select(l => new LayerSnapshot(l.Name, l.Visible, l.IsBackground, l.Image.ToArgb32())).ToList(),
            ActiveLayerIndex);

    internal void PushStructuralUndo(WholeDocSnapshot snapshot)
    {
        PushUndo(snapshot);
        IsDirty = true;
        InvalidateComposed();
        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>套用到全部圖層的整幅變換（調整大小、旋轉、反相等），可復原。</summary>
    public void ApplyGlobal(Func<Bitmap, Bitmap> transform)
    {
        var snap = TakeSnapshotInternal();
        for (int i = 0; i < Layers.Count; i++)
        {
            var old = Layers[i].Image;
            var result = transform(old);
            if (!ReferenceEquals(result, old)) old.Dispose();
            Layers[i].Image = result;
        }
        PushStructuralUndo(snap);
    }

    private void PushUndo(UndoEntry entry)
    {
        _undo.AddLast(entry);
        while (_undo.Count > MaxUndoSteps) _undo.RemoveFirst();
        _redo.Clear();
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Undo()
    {
        var node = _undo.Last;
        if (node is null) return;
        _undo.RemoveLast();
        node.Value.Swap(this);
        _redo.AddLast(node.Value);
        IsDirty = true;
        InvalidateComposed();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        var node = _redo.Last;
        if (node is null) return;
        _redo.RemoveLast();
        node.Value.Swap(this);
        _undo.AddLast(node.Value);
        IsDirty = true;
        InvalidateComposed();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void RaiseStructure() => StructureChanged?.Invoke(this, EventArgs.Empty);

    public void MarkSaved() => IsDirty = false;
    public void MarkDirty() => IsDirty = true;

    /// <summary>PNG 存檔（圖層合併；有透明時保留 Alpha）。</summary>
    public Bitmap FlattenForExport(Color? replaceTransparentWith = null)
    {
        var img = Compose().ToArgb32();
        if (replaceTransparentWith is { } c)
            img = ImageOps.MapPixels(img, p => p.A == 0 ? c : p);
        return img;
    }

    public void Dispose()
    {
        foreach (var l in Layers) l.Image.Dispose();
        Layers.Clear();
        _composed?.Dispose();
        _composed = null;
    }
}
