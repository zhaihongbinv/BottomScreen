using System.Text;

namespace BottomScreen;

internal static class Program
{
    internal static bool PerformanceLoggingEnabled { get; private set; }
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--cursor-guard") { CursorGuardWorker.Run(args); return; }
        if (args.Length > 0 && args[0] == "--restore-cursor") { SystemCursorGuard.RestoreSystemCursor(); return; }
        if (args.Length > 0 && args[0] == "--cursor-test-parent") { Thread.Sleep(15000); return; }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject.ToString() ?? "Unhandled exception");
        PerformanceLoggingEnabled = args.Contains("--diagnostics");
        if (args.Contains("--cursor-self-test")) { Application.Run(new CursorSelfTestForm(args)); return; }
        if (args.Contains("--self-test"))
        {
            Application.Run(new SelfTestForm(args));
            return;
        }
        if (args.Contains("--demo")) { Application.Run(new PatternForm()); return; }
        using (var single = new Mutex(true, "Local\\BottomScreen_SingleInstance", out bool created))
        {
            if (!created) { MessageBox.Show("BottomScreen 已经在运行。请切换到原来的窗口。", "BottomScreen"); return; }
            // HDR policy is applied explicitly to the selected captured process.
            // Starting this utility must not change any application's HDR setting.
            Application.Run(new MainForm());
            single.ReleaseMutex();
        }
    }

    internal static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "BottomScreen.log"), $"{DateTime.Now:O} {message}\n", Encoding.UTF8); } catch { }
    }
    internal static void Report(Exception e)
    {
        Log(e.ToString());
        MessageBox.Show(e.Message + "\n详细信息已写入程序目录的 BottomScreen.log。", "BottomScreen", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
