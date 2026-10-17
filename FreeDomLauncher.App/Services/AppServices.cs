using Avalonia.Platform.Storage;
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using FreeDomLauncher.Core.Models;

namespace FreeDomLauncher.App.Services;

/// <summary>Встроенные ресурсы (логотип, иконки, картинки сборок).</summary>
public static class AppAssets
{
    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new();

    public static Bitmap Load(string relative)
        => Cache.GetOrAdd(relative, r => new Bitmap(AssetLoader.Open(new Uri("avares://FreeDomLauncher/Assets/" + r))));

    /// <summary>Подбирает картинку сборки по версии Minecraft.</summary>
    public static string ProfileArtFile(string? mc)
    {
        const string fallback = "1_21_3-1_1_12_2.jpg";
        if (string.IsNullOrWhiteSpace(mc)) return fallback;
        var parts = mc.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var major))
        {
            if (major >= 26)
            {
                int.TryParse(parts[1], out var m2);
                return m2 switch { 1 => "26_1_2-26_1.jpg", 2 => "26_2.jpg", _ => "26_3.jpg" };
            }
            if (major == 1 && int.TryParse(parts[1], out var minor) && minor == 21)
            {
                int patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
                return patch switch
                {
                    >= 11 => "1_21_11.jpg",
                    >= 9 => "1_21_10-1_21_9.jpg",
                    >= 6 => "1_21_8-1_21_6.jpg",
                    5 => "1_21_5.jpg",
                    4 => "1_21_4.jpg",
                    _ => fallback
                };
            }
        }
        return fallback;
    }

    public static Bitmap ProfileArt(string? mc) => Load("profiles/" + ProfileArtFile(mc));
}

/// <summary>Асинхронная загрузка и кэш иконок проектов Modrinth (только https).</summary>
public static class ImageCache
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Cache = new();
    private static readonly SemaphoreSlim Gate = new(4, 4);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(FreeDomLauncher.Infrastructure.Modrinth.ModrinthClient.UserAgent);
        return c;
    }

    public static Task<Bitmap?> LoadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult<Bitmap?>(null);
        return Cache.GetOrAdd(url, _ => FetchAsync(uri));
    }

    private static async Task<Bitmap?> FetchAsync(Uri uri)
    {
        await Gate.WaitAsync();
        try
        {
            var bytes = await Http.GetByteArrayAsync(uri);
            if (bytes.Length > 8 * 1024 * 1024) return null;
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch { return null; }
        finally { Gate.Release(); }
    }
}

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string okText = "OK", string cancelText = "Отмена");
    Task ShowAsync(string title, string message);
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickFileAsync(string title, params string[] patterns);
    Task CopyToClipboardAsync(string text);
    void OpenFolder(string path);
    void OpenFile(string path);
    void OpenUrl(string url);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<bool> ConfirmAsync(string title, string message, string okText = "OK", string cancelText = "Отмена")
        => await ShowCoreAsync(title, message, okText, cancelText);

    public async Task ShowAsync(string title, string message) => await ShowCoreAsync(title, message, "OK", null);

    private static async Task<bool> ShowCoreAsync(string title, string message, string ok, string? cancel)
    {
        var owner = Owner;
        var win = new Window
        {
            Title = title, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var okBtn = new Button { Content = ok, MinWidth = 96, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        okBtn.Classes.Add("primary");
        okBtn.Click += (_, _) => win.Close(true);
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        if (cancel != null)
        {
            var cb = new Button { Content = cancel, MinWidth = 96, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            cb.Classes.Add("ghost");
            cb.Click += (_, _) => win.Close(false);
            buttons.Children.Add(cb);
        }
        buttons.Children.Add(okBtn);
        win.Content = new StackPanel
        {
            Margin = new Thickness(22), Spacing = 18,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap },
                new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } },
                buttons
            }
        };
        if (owner == null) { win.Show(); return false; }
        return await win.ShowDialog<bool>(owner);
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var owner = Owner; if (owner == null) return null;
        var r = await owner.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return r.Count > 0 ? r[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, params string[] patterns)
    {
        var owner = Owner; if (owner == null) return null;
        var opts = new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = title, AllowMultiple = false };
        if (patterns.Length > 0) opts.FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Файлы") { Patterns = patterns } };
        var r = await owner.StorageProvider.OpenFilePickerAsync(opts);
        return r.Count > 0 ? r[0].TryGetLocalPath() : null;
    }

    public async Task CopyToClipboardAsync(string text)
    {
        var cb = Owner?.Clipboard;
        if (cb != null) await cb.SetTextAsync(text);
    }

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch { /* не критично */ }
    }

    public void OpenFile(string path)
    {
        try { if (File.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* не критично */ }
    }

    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* не критично */ }
    }
}

public static class ThemeService
{
    public static void Apply(AppSettings s)
    {
        var app = Application.Current; if (app == null) return;
        app.RequestedThemeVariant = s.Theme switch { "Light" => ThemeVariant.Light, "System" => ThemeVariant.Default, _ => ThemeVariant.Dark };
        Color accent;
        try { accent = Color.Parse(s.AccentColor); } catch { accent = Color.Parse("#8B5CF6"); }
        var orange = Color.Parse("#F97316");
        app.Resources["FdAccentColor"] = accent;
        app.Resources["FdAccent"] = new SolidColorBrush(accent);
        app.Resources["SystemAccentColor"] = accent;
        app.Resources["FdGradient"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(accent, 0), new GradientStop(orange, 1) }
        };
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } w })
            { if (s.AnimationsEnabled) { if (!w.Classes.Contains("anim")) w.Classes.Add("anim"); } else w.Classes.Remove("anim"); }
    }
}
