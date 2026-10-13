namespace PaintClone;

using PaintClone.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetDefaultFont(new Font("Microsoft JhengHei UI", 9f)); }
        catch { /* 找不到字型時退回預設 */ }

        var form = new MainForm();
        if (args.Length > 0 && File.Exists(args[0]))
            form.LoadOnStartup(args[0]);

        Application.Run(form);
    }
}
