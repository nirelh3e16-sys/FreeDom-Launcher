using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeDomLauncher.App.Services;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Util;
using FreeDomLauncher.Infrastructure.Minecraft;
using FreeDomLauncher.Infrastructure.Storage;

namespace FreeDomLauncher.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly AppState _state;

    public IReadOnlyList<string> Themes { get; } = new[] { "Тёмная", "Светлая", "Системная" };
    public IReadOnlyList<string> LogLevels { get; } = new[] { "Debug", "Info", "Warn", "Error" };
    public IReadOnlyList<string> AccentPresets { get; } = new[] { "#8B5CF6", "#F97316", "#EC4899", "#22C55E", "#3B82F6", "#EAB308" };
    public double MaxMemoryMb { get; }
    public string VersionText => "FreeDom Launcher 1.0.0";
    public string DataPath => _paths.Root;

    [ObservableProperty] private string _theme = "Тёмная";
    [ObservableProperty] private string _accent = "#8B5CF6";
    [ObservableProperty] private bool _animations = true;
    [ObservableProperty] private string _instancesRoot = "";
    [ObservableProperty] private string _javaPath = "";
    [ObservableProperty] private double _memory = 4096;
    [ObservableProperty] private string _memoryText = "4096 МБ";
    [ObservableProperty] private decimal? _parallel = 3;
    [ObservableProperty] private decimal? _timeout = 60;
    [ObservableProperty] private string _logLevel = "Info";
    [ObservableProperty] private decimal? _logDays = 14;
    [ObservableProperty] private bool _snapshots;
    [ObservableProperty] private string _clientId = "";
    [ObservableProperty] private string _status = "";

    public SettingsViewModel(ISettingsService settings, AppPaths paths, IDialogService dialogs, AppState state)
    {
        _settings = settings; _paths = paths; _dialogs = dialogs; _state = state;
        var totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576.0;
        MaxMemoryMb = Math.Clamp(Math.Floor(totalMb / 512) * 512, 2048, 32768);
        Reload();
    }

    partial void OnMemoryChanged(double value) => MemoryText = $"{(int)value} МБ";

    public void Reload()
    {
        var s = _settings.Current;
        Theme = s.Theme switch { "Light" => "Светлая", "System" => "Системная", _ => "Тёмная" };
        Accent = s.AccentColor; Animations = s.AnimationsEnabled;
        InstancesRoot = s.InstancesRoot ?? ""; JavaPath = s.JavaPath ?? "";
        Memory = Math.Min(s.DefaultMemoryMb, MaxMemoryMb);
        Parallel = s.MaxParallelDownloads; Timeout = s.DownloadTimeoutSeconds;
        LogLevel = s.LogLevel; LogDays = s.LogRetentionDays; Snapshots = s.IncludeSnapshots;
        ClientId = s.MicrosoftClientId ?? "";
        Status = "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!ColorUtil.IsValidHex(Accent)) { Status = "Цвет должен быть в формате #RRGGBB."; return; }
        if (JavaPath.Trim().Length > 0 && await JavaLocator.GetVersionAsync(JavaPath.Trim()) == null)
        { Status = "По указанному пути нет рабочей Java."; return; }
        if (InstancesRoot.Trim().Length > 0)
        {
            try { Directory.CreateDirectory(InstancesRoot.Trim()); }
            catch (Exception ex) { Status = "Папка сборок недоступна: " + ex.Message; return; }
        }
        var s = _settings.Current.Clone();
        s.Theme = Theme switch { "Светлая" => "Light", "Системная" => "System", _ => "Dark" };
        s.AccentColor = Accent.Trim(); s.AnimationsEnabled = Animations;
        s.InstancesRoot = InstancesRoot.Trim(); s.JavaPath = JavaPath.Trim();
        s.DefaultMemoryMb = (int)Memory; s.MaxParallelDownloads = (int)(Parallel ?? 3); s.DownloadTimeoutSeconds = (int)(Timeout ?? 60);
        s.LogLevel = LogLevel; s.LogRetentionDays = (int)(LogDays ?? 14); s.IncludeSnapshots = Snapshots;
        s.MicrosoftClientId = ClientId.Trim();
        try
        {
            await _settings.SaveAsync(s);
            ThemeService.Apply(_settings.Current);
            Status = "Настройки сохранены.";
            _state.Status = "Настройки сохранены";
        }
        catch (Exception ex) { Status = "Не удалось сохранить: " + ex.Message; }
    }

    [RelayCommand] private void ApplyPreset(string? hex) { if (hex != null) Accent = hex; }

    [RelayCommand]
    private async Task BrowseInstancesAsync()
    {
        var f = await _dialogs.PickFolderAsync("Папка для сборок");
        if (f != null) InstancesRoot = f;
    }

    [RelayCommand]
    private async Task BrowseJavaAsync()
    {
        var f = await _dialogs.PickFileAsync("Выберите java.exe или javaw.exe", "java.exe", "javaw.exe", "java");
        if (f != null) JavaPath = f;
    }

    [RelayCommand]
    private async Task DetectJavaAsync()
    {
        Status = "Поиск Java…";
        var found = await JavaLocator.FindInstalledAsync();
        if (found.Count == 0) { Status = "Java не найдена. Оставьте поле пустым — лаунчер скачает её сам."; return; }
        var best = found.OrderByDescending(j => JavaLocator.MajorVersion(j.Version) ?? 0).First();
        JavaPath = best.Path;
        Status = $"Найдена Java {best.Version}. Не забудьте нажать «Сохранить».";
    }

    [RelayCommand] private void ClearJava() => JavaPath = "";
    [RelayCommand] private void ClearInstances() => InstancesRoot = "";
    [RelayCommand] private void OpenData() => _dialogs.OpenFolder(_paths.Root);
    [RelayCommand] private void OpenLogs() => _dialogs.OpenFolder(_paths.LogsDir);
}
