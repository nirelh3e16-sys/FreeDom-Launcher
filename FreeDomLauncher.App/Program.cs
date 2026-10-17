using Avalonia;

namespace FreeDomLauncher.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

internal static class CrashLog
{
    public static void Write(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeDomLauncher", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:u}{Environment.NewLine}{FreeDomLauncher.Core.Util.Redactor.Clean(ex.ToString())}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }
}
