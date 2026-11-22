namespace FeigeP2P;

static class Program
{
    [STAThread]
    static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.ExceptionObject?.ToString() ?? "unknown", "UnhandledException");
        Application.ThreadException += (_, e) =>
            LogCrash(e.Exception.ToString(), "ThreadException");

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            LogCrash(ex.ToString(), "Main");
            MessageBox.Show("程序发生错误，详情见 crash.log", "数据线", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void LogCrash(string text, string source)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, $"===== {DateTime.Now} [{source}] =====\n{text}\n\n");
        }
        catch { }
    }
}
