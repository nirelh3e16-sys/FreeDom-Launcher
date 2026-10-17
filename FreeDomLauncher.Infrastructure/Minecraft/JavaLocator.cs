using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FreeDomLauncher.Infrastructure.Minecraft;

public sealed record JavaInstall(string Path, string Version);

/// <summary>Поиск установленных Java и проверка выбранного пути.</summary>
public static class JavaLocator
{
    private static readonly Regex VersionRegex = new("version \"(?<v>[^\"]+)\"", RegexOptions.Compiled);

    public static string? ParseVersion(string javaVersionOutput)
    {
        var m = VersionRegex.Match(javaVersionOutput ?? "");
        return m.Success ? m.Groups["v"].Value : null;
    }

    /// <summary>Основная (major) версия: "1.8.0_392" → 8, "21.0.1" → 21.</summary>
    public static int? MajorVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var parts = version.Split('.', '_', '-', '+');
        if (!int.TryParse(parts[0], out var first)) return null;
        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var second)) return second;
        return first;
    }

    public static async Task<string?> GetVersionAsync(string javaPath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(javaPath)) return null;
            var psi = new ProcessStartInfo(javaPath, "-version")
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var err = await p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return ParseVersion(err);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        { return null; }
    }

    public static async Task<IReadOnlyList<JavaInstall>> FindInstalledAsync(CancellationToken ct = default)
    {
        var candidates = new List<string>();
        var exeName = OperatingSystem.IsWindows() ? "javaw.exe" : "java";
        void AddHome(string? home)
        {
            if (!string.IsNullOrWhiteSpace(home)) candidates.Add(System.IO.Path.Combine(home, "bin", exeName));
        }
        AddHome(Environment.GetEnvironmentVariable("JAVA_HOME"));

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(System.IO.Path.Combine(dir.Trim('"'), exeName));

        if (OperatingSystem.IsWindows())
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            var vendors = new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto", "Semeru", "AdoptOpenJDK" };
            foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
                foreach (var v in vendors)
                {
                    var vd = System.IO.Path.Combine(root, v);
                    if (!Directory.Exists(vd)) continue;
                    try { foreach (var jdk in Directory.EnumerateDirectories(vd)) AddHome(jdk); } catch { /* ignore */ }
                }
        }

        var result = new List<JavaInstall>();
        foreach (var path in candidates.Where(File.Exists).Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var ver = await GetVersionAsync(path, ct);
            if (ver != null) result.Add(new JavaInstall(path, ver));
        }
        return result;
    }
}
