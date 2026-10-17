using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Util;
using FreeDomLauncher.Infrastructure.Storage;

namespace FreeDomLauncher.Infrastructure.Minecraft;

/// <summary>
/// Установка файлов и запуск Minecraft Java Edition через CmlLib.Core.
/// Библиотеки, ассеты, версии и Java общие для всех сборок; рабочая папка игры (моды, миры, конфиги) у каждой сборки своя.
/// </summary>
public sealed class MinecraftLauncherService : IMinecraftLauncherService
{
    private readonly AppPaths _paths;
    private readonly IVersionCatalogService _catalog;
    private readonly ISettingsService _settings;
    private readonly IAppLogger _log;
    private readonly ConcurrentDictionary<Guid, List<Process>> _running = new();

    public MinecraftLauncherService(AppPaths paths, IVersionCatalogService catalog, ISettingsService settings, IAppLogger log)
    { _paths = paths; _catalog = catalog; _settings = settings; _log = log; }

    public event EventHandler<ProfileExitedEventArgs>? ProfileExited;

    public bool IsRunning(Guid profileId)
    {
        if (!_running.TryGetValue(profileId, out var list)) return false;
        lock (list) { list.RemoveAll(p => { try { return p.HasExited; } catch { return true; } }); return list.Count > 0; }
    }

    public void Kill(Guid profileId)
    {
        if (!_running.TryGetValue(profileId, out var list)) return;
        lock (list)
            foreach (var p in list.ToList())
                try { if (!p.HasExited) p.Kill(true); } catch { /* уже завершён */ }
    }

    // ---- вспомогательные методы для настройки путей CmlLib через рефлексию (не зависят от точной сигнатуры свойств) ----
    private static void TrySet(object target, string prop, object? value)
    {
        var pi = target.GetType().GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var setter = pi?.GetSetMethod(true);
        if (pi != null && setter != null && value != null && pi.PropertyType.IsInstanceOfType(value)) setter.Invoke(target, new[] { value });
    }
    private static string? TryGet(object target, string prop)
        => target.GetType().GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target) as string;

    public async Task<LaunchResult> LaunchAsync(GameProfile profile, AuthSession session, IProgress<LaunchProgress> progress, CancellationToken ct = default)
    {
        string? logPath = null;
        try
        {
            if (!profile.Loader.IsInstallSupported())
                return Fail($"Загрузчик {profile.Loader.DisplayName()} пока не поддерживается в версии 1.0.0 (доступны Vanilla и Fabric).");
            if (string.IsNullOrWhiteSpace(profile.MinecraftVersion))
                return Fail("В сборке не выбрана версия Minecraft.");

            // Java: пользовательская (сборка → настройки) либо автоматическая от CmlLib
            var javaPath = !string.IsNullOrWhiteSpace(profile.JavaPath) ? profile.JavaPath : _settings.Current.JavaPath;
            if (!string.IsNullOrWhiteSpace(javaPath))
            {
                progress.Report(new LaunchProgress("Проверка Java…", null));
                if (!File.Exists(javaPath)) return Fail($"Java не найдена по пути «{javaPath}». Выберите другой путь в настройках сборки или очистите поле, чтобы лаунчер скачал Java сам.");
                var ver = await JavaLocator.GetVersionAsync(javaPath, ct);
                if (ver == null) return Fail($"Файл «{javaPath}» не похож на рабочую Java (команда -version не сработала).");
                var major = JavaLocator.MajorVersion(ver);
                var need = RequiredJavaMajor(profile.MinecraftVersion);
                if (major != null && major < need)
                    return Fail($"Для Minecraft {profile.MinecraftVersion} нужна Java {need}+, а выбрана Java {major}. Выберите более новую Java или очистите поле — лаунчер скачает подходящую.");
            }

            Directory.CreateDirectory(profile.FolderPath);
            _paths.EnsureCreated();

            // Пути: общий кэш + изолированная папка игры
            var mcPath = new MinecraftPath(profile.FolderPath);
            var shared = _paths.SharedMinecraftDir;
            TrySet(mcPath, "Library", Path.Combine(shared, "libraries"));
            TrySet(mcPath, "Assets", Path.Combine(shared, "assets"));
            TrySet(mcPath, "Versions", Path.Combine(shared, "versions"));
            TrySet(mcPath, "Runtime", Path.Combine(shared, "runtime"));
            var versionsDir = TryGet(mcPath, "Versions") ?? Path.Combine(profile.FolderPath, "versions");
            Directory.CreateDirectory(versionsDir);

            // Версия для запуска
            string versionName = profile.MinecraftVersion;
            if (profile.Loader == LoaderType.Fabric)
            {
                progress.Report(new LaunchProgress("Подготовка Fabric…", null));
                var loaderVer = profile.LoaderVersion;
                if (string.IsNullOrWhiteSpace(loaderVer))
                    loaderVer = await _catalog.GetLatestFabricLoaderAsync(profile.MinecraftVersion, ct)
                                ?? throw new InvalidOperationException($"Для Minecraft {profile.MinecraftVersion} нет версии Fabric Loader.");
                versionName = await _catalog.EnsureFabricProfileAsync(profile.MinecraftVersion, loaderVer, versionsDir, ct);
            }

            // Запуск (новый экземпляр лаунчера на каждую установку, чтобы он перечитал локальные версии)
            var launcher = new MinecraftLauncher(mcPath);
            HookProgress(launcher, progress);

            var option = new MLaunchOption
            {
                MaximumRamMb = profile.MemoryMb,
                Session = new MSession(session.Username, session.AccessToken, session.Uuid),
            };
            TrySetOption(option, "MinimumRamMb", Math.Min(512, profile.MemoryMb));
            if (!string.IsNullOrWhiteSpace(javaPath)) TrySetOption(option, "JavaPath", javaPath);

            progress.Report(new LaunchProgress("Установка файлов игры…", null));
            _log.Info($"Установка и запуск: сборка «{profile.Name}», версия {versionName}");
            Process process = await launcher.InstallAndBuildProcessAsync(versionName, option).WaitAsync(ct);

            // Лог игры
            Directory.CreateDirectory(_paths.GameLogsDir);
            logPath = Path.Combine(_paths.GameLogsDir, $"{SafePath.Slug(profile.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var logWriter = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            var logLock = new object();
            void Line(string? s)
            {
                if (s == null) return;
                lock (logLock) { try { logWriter.WriteLine(Redactor.Clean(s)); } catch (ObjectDisposedException) { } }
            }
            Line($"=== FreeDom Launcher 1.0.0 | {profile.Name} | Minecraft {profile.MinecraftVersion} | {profile.Loader} | {DateTime.Now:u} ===");

            var psi = process.StartInfo;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            if (string.IsNullOrEmpty(psi.WorkingDirectory)) psi.WorkingDirectory = profile.FolderPath;
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += (_, e) => Line(e.Data);
            process.ErrorDataReceived += (_, e) => Line(e.Data);
            process.Exited += (_, _) =>
            {
                int code = -1; try { code = process.ExitCode; } catch { /* ignore */ }
                Line($"=== Игра завершена, код выхода {code} ===");
                lock (logLock) { try { logWriter.Dispose(); } catch { /* ignore */ } }
                if (_running.TryGetValue(profile.Id, out var l)) lock (l) l.Remove(process);
                _log.Info($"Сборка «{profile.Name}» завершена, код {code}");
                ProfileExited?.Invoke(this, new ProfileExitedEventArgs { ProfileId = profile.Id, ExitCode = code, LogPath = logPath });
            };

            progress.Report(new LaunchProgress("Запуск игры…", null));
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var list = _running.GetOrAdd(profile.Id, _ => new List<Process>());
            lock (list) list.Add(process);

            progress.Report(new LaunchProgress("Игра запущена", 1));
            return new LaunchResult(true, process.Id, logPath, null);
        }
        catch (OperationCanceledException)
        {
            _log.Info("Запуск отменён пользователем");
            return new LaunchResult(false, null, logPath, "Запуск отменён.");
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка установки/запуска", ex);
            return new LaunchResult(false, null, logPath, Friendly(ex));
        }

        LaunchResult Fail(string message) => new(false, null, logPath, message);
    }

    private static void TrySetOption(object option, string prop, object value)
    {
        var pi = option.GetType().GetProperty(prop);
        if (pi == null || !pi.CanWrite) return;
        try
        {
            var target = Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType;
            pi.SetValue(option, Convert.ChangeType(value, target));
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or TargetInvocationException) { /* необязательная настройка */ }
    }

    /// <summary>
    /// Подписка на события прогресса CmlLib через рефлексию: если у библиотеки изменятся имена,
    /// лаунчер всё равно соберётся и запустит игру, просто без детального прогресса.
    /// </summary>
    private static void HookProgress(object launcher, IProgress<LaunchProgress> progress)
    {
        try
        {
            var ei = launcher.GetType().GetEvent("FileProgressChanged");
            if (ei?.EventHandlerType is { IsGenericType: true } handlerType)
            {
                var argType = handlerType.GetGenericArguments()[0];
                var hook = new ProgressHook(progress);
                var method = typeof(ProgressHook).GetMethod(nameof(ProgressHook.OnFile))!.MakeGenericMethod(argType);
                ei.AddEventHandler(launcher, Delegate.CreateDelegate(handlerType, hook, method));
            }
        }
        catch (Exception) { /* прогресс — косметика */ }
    }

    private sealed class ProgressHook
    {
        private readonly IProgress<LaunchProgress> _progress;
        public ProgressHook(IProgress<LaunchProgress> progress) => _progress = progress;

        public void OnFile<T>(object? sender, T e)
        {
            try
            {
                dynamic d = e!;
                int total = (int)d.TotalTasks, done = (int)d.ProgressedTasks;
                string name = Convert.ToString((object?)d.Name) ?? "";
                var stage = total > 0 ? $"Загрузка файлов: {done}/{total}  {name}" : "Загрузка файлов…";
                _progress.Report(new LaunchProgress(stage, total > 0 ? (double)done / total : null));
            }
            catch (Exception) { /* ignore */ }
        }
    }

    private static int RequiredJavaMajor(string mc)
    {
        // Приблизительная таблица требований Mojang.
        var parts = mc.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var major) && major >= 26) return 21;   // новая схема версий (26.x): минимум 21
        if (parts.Length >= 2 && int.TryParse(parts[1], out var minor))
        {
            int patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
            if (minor >= 21 || (minor == 20 && patch >= 5)) return 21;
            if (minor >= 18 || (minor == 17)) return minor == 17 ? 16 : 17;
            return 8;
        }
        return 8;
    }

    private static string Friendly(Exception ex)
    {
        var msg = ex switch
        {
            HttpRequestException => "Не удалось скачать файлы игры. Проверьте подключение к интернету и повторите попытку.",
            TimeoutException => "Время ожидания истекло при скачивании файлов игры.",
            UnauthorizedAccessException => "Нет доступа к папке сборки или кэша лаунчера. Проверьте права и антивирус.",
            IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase) || io.HResult == unchecked((int)0x80070070)
                => "Недостаточно места на диске.",
            IOException io => "Ошибка записи файлов: " + io.Message,
            InvalidOperationException => ex.Message,
            _ => "Не удалось установить или запустить игру: " + ex.Message
        };
        return msg;
    }
}
