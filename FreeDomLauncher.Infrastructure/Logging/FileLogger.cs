using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Util;
using FreeDomLauncher.Infrastructure.Storage;

namespace FreeDomLauncher.Infrastructure.Logging;

/// <summary>Файловый лог лаунчера. Секреты (токены, ключи) вырезаются перед записью.</summary>
public sealed class FileLogger : IAppLogger
{
    private readonly AppPaths _paths;
    private readonly ISettingsService? _settings;
    private readonly object _lock = new();

    public FileLogger(AppPaths paths, ISettingsService? settings = null)
    {
        _paths = paths; _settings = settings;
        try { Directory.CreateDirectory(paths.LogsDir); CleanOld(); } catch { /* логирование не должно ронять приложение */ }
    }

    public string LogDirectory => _paths.LogsDir;

    private int MinLevel => (_settings?.Current.LogLevel) switch { "Debug" => 0, "Warn" => 2, "Error" => 3, _ => 1 };

    public void Debug(string message) => Write(0, "DEBUG", message);
    public void Info(string message) => Write(1, "INFO ", message);
    public void Warn(string message) => Write(2, "WARN ", message);
    public void Error(string message, Exception? ex = null) => Write(3, "ERROR", ex == null ? message : message + " :: " + ex.GetType().Name + ": " + ex.Message);

    private void Write(int level, string tag, string message)
    {
        if (level < MinLevel) return;
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {Redactor.Clean(message)}{Environment.NewLine}";
            var file = Path.Combine(_paths.LogsDir, $"launcher-{DateTime.Now:yyyyMMdd}.log");
            lock (_lock) File.AppendAllText(file, line);
        }
        catch { /* ignore */ }
    }

    private void CleanOld()
    {
        var days = _settings?.Current.LogRetentionDays ?? 14;
        var limit = DateTime.Now.AddDays(-days);
        foreach (var f in Directory.EnumerateFiles(_paths.LogsDir, "launcher-*.log"))
            if (File.GetLastWriteTime(f) < limit) { try { File.Delete(f); } catch { /* ignore */ } }
        if (Directory.Exists(_paths.GameLogsDir))
            foreach (var f in Directory.EnumerateFiles(_paths.GameLogsDir, "*.log"))
                if (File.GetLastWriteTime(f) < limit) { try { File.Delete(f); } catch { /* ignore */ } }
    }
}
