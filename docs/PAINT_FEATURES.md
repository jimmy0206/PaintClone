# 小畫家（MS Paint 11.2605.81.0）功能研究報告

調查方式：以 REA（Ghidra 12.1.4 provider）靜態分析裝機原件
`Microsoft.Paint_11.2605.81.0` 的 `BasePaint.dll`（繪圖引擎）與 `PaintUI.dll`（UI 層）。
所有符號與字串皆為 **shipped-artifact 實測證據**（observed/derived），非網路傳聞。

- 目標：`BasePaint.dll`（SHA-256 `24cda98f…b00eacf`，x64 PE，1,199 個函式、1,067 條字串）
- Evidence：`ev_5aba81b177582827966a64958287b4ec810e59b43bb0a46dd7e2896e8a33a4b3`（binary_overview）
- 來源路徑字串顯示原始碼模組：`src/basepaint/{EraserStrokeOperation,FloodFillOperation,GenerativeFillOperation,MaskBasedSelectionOperation,ObjectSelectOperation,SelectionOperation,TextureStore}.cpp`

## 1. 繪圖工具（BasePaint.dll 內部 Operation 符號，直接觀察）

| 符號（觀察） | 對應功能 |
|---|---|
| `CDrawStrokeOperation` | 鉛筆（逐筆劃） |
| `CapsuleStrokeOperation` + `CapsuleStrokeConfiguration` | 筆刷（膠囊狀平滑筆劃） |
| `DrawAirBrushOperation` | 噴槍 |
| `StampStrokeOperation` + `SolidStampStrokeConfiguration` / `PatternStampStrokeConfiguration` | 實心／圖樣圖章筆刷 |
| `EraserOperation` / `EraserStrokeOperation` / `EraserStrokeConfiguration` | 橡皮擦（多種粗細、`SkipCanvasRendering` 支援圖層透明擦除） |
| `FloodFillOperation` | _paint_ 填色（區域填充） |
| `GenerativeFillOperation`（+`GetSelectionMask`/`ReplaceContent`） | AI 產生式填色（搭配 `mager.onnxe`/`seg.onnxe` ONNX 模型） |
| `CDrawLineOperation` | 直線 |
| `CDrawBezierOperation` | 曲線（貝茲爾，三點操作） |
| `CDrawPolygonOperation`（`GetCurrentPointIndex`） | 多邊形 |
| `CDrawShapeOperation` / `CDrawAutoShapeOperation` / `CDrawRectAutoShapeOperation` | 自動圖形（矩形系） |
| `ShapeFactory::CreateRectangle / CreateEllipse / CreateRoundedRectangle / CreateRoundedRectangularCallout` | 矩形／橢圓／圓角矩形／圓角標註框（氣泡框） |
| `TextToolOperation` | 文字工具 |
| `ModifiableToolOperation` | 選取內容的可變換操作（移動／縮放把手） |

## 2. 選取系統（觀察）

- `SelectionOperation`：矩形選取（`GetSelectedRegion` / `GetSelectedContent` / `D2DSelectionMask` / `RenderMode`）
- `MaskBasedSelectionOperation`：任選（套索）—以遮罩 bitmap 實作（`RegisterStroke`/`CommitStroke`/`InvertMaskBitmap`）
- `ObjectSelectOperation`：Magic Select／物件選取，`GetMaskSelectionPoint`、`IsPointInSelectionMask`（AI 分割，PerceptiveShell + seg.onnxe）

## 3. 圖像幾何操作（`BasePaint::Image::*`，觀察）

`Resize`、`Rotate(double)`、`HorizontalFlip`、`VerticalFlip`、`HorizontalSkew(int)`、`VerticalSkew(int)`、`Crop(Rect|Region)`、`InvertColors`、`HasTransparentPixels`；另有 `IsValidSkewValue`、`ComputeRotatedDestinationSizeAndPoints`（任意角度旋轉計算）。
UI 層（PaintUI.dll 字串）另有：`Rotate90Left/90Right/180`、`RotateCustomDialog`（任意角度對話框）、`ResizeImage`、`Grayscale`、`InvertColor`、`Effects`、`Layers`（圖層）、`AirBrush`。

## 4. 渲染與色彩

- 自製引擎 `PGS`（Paint Graphics System）：`CreateTextureSkeletalStrokeBrush`（骨架筆劃平滑）、`SetAntialiasingMode`（反鋸齒開關）、`CreateRotateAtRectCenterMatrix`、`InvertBitmapColors`。
- 疊加 GDI+：`GdipFillRegion`、`GdipAddPathRectangleI/EllipseI`、`GdipImageRotateFlip` 等。
- 雙色彩模型（Color 1／Color 2）由工具行為與填充路徑推得（inferred；UI 字串 `transparent`、`EdpPaste*` 等）。

## 5. 重建範圍裁決

**完整重建**（本次實作）：全部傳統工具（鉛筆、筆刷、橡皮擦、填色、取色、直線、曲線、矩形、圓角矩形、橢圓、標註框、多邊形、文字、噴槍）、矩形／任選選取＋移動／縮放把手、圖像操作（調整大小、傾斜、旋轉任意角度、反轉、裁剪、反相、灰度）、雙色彩＋調色盤、圖層（能見度/順序/合併）、多階 undo/redo、縮放、網格線、列印、檔案開啟／儲存（PNG/JPEG/BMP/GIF/TIFF）。

**刻意不重建**（需雲端／本機 ML 服務，靜態證據顯示依賴 `mager.onnxe`、`seg.onnxe`、`AIServices.dll`）：AI 產生式填色、AI Magic Select 分割、Co-Pilot 相關功能、多人協作（Multiplayer）、故事板（Storyboard）。替代方案：以「魔法棒（依顏色容差選取）」取代 Magic Select 的核心用途。

**優化項目**（相對原版）：
1. undo/redo 上限 100 步（原版 3～10 步），且以髒矩形快照省記憶體
2. 填色容差可調（原版只能精確匹配）
3. 繪製中即時預覽（原版 shapes 拖曳時預覽較粗糙）與平滑曲線
4. 自動存檔／崩潰回覆（原版僅雲端同步）
5. 縮放 1%–3200%、Ctrl+滾輪定點縮放、中鍵拖曳平移
6. 圖層透明橡皮擦（非背景層擦成透明）
7. 可調色盤含「最近使用顏色」
8. 透明度 checkerboard 顯示（原版以洋紅表示透明）
