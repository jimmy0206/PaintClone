using System.Text.Json;

namespace PaintClone.Core;

/// <summary>應用程式設定（視窗狀態、最近檔案、最近顏色），存於 %APPDATA%\PaintClone。</summary>
public sealed class AppSettings
{
    public int WindowWidth = 1400, WindowHeight = 900;   // 實體像素（OnShown 套用，超出螢幕自動收斂）
    public bool Maximized;
    public bool ShowLayersPanel = true;
    public bool ShowGrid;
    public bool AntialiasShapes = true;
    public int FloodTolerance = 10;
    public bool FloodUseColor2;
    public List<string> RecentFiles = new();
    public List<string> RecentColorsHex = new();

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaintClone");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    // 設定值是 public 欄位，System.Text.Json 預設只處理 property，
    // 少了 IncludeFields 就會寫出空的 {}（視窗大小、最近檔案從不曾真的存進去）
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, IncludeFields = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch { /* 設定損毀時使用預設值 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* 無法儲存時靜默略過 */ }
    }

    public void PushRecentFile(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 8) RecentFiles.RemoveAt(RecentFiles.Count - 1);
    }

    public void PushRecentColor(Color c)
    {
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        RecentColorsHex.Remove(hex);
        RecentColorsHex.Insert(0, hex);
        if (RecentColorsHex.Count > 10) RecentColorsHex.RemoveAt(RecentColorsHex.Count - 1);
    }

    /// <summary>自動存檔（崩潰回覆）目錄。</summary>
    public static string AutoSaveDir
    {
        get
        {
            var d = Path.Combine(Dir, "recovery");
            Directory.CreateDirectory(d);
            return d;
        }
    }
}
