using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeDomLauncher.App.Services;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Modrinth;
using FreeDomLauncher.Core.Mods;
using FreeDomLauncher.Core.Util;

namespace FreeDomLauncher.App.ViewModels;

public partial class VersionItemViewModel : ObservableObject
{
    private readonly ProjectViewModel _owner;
    public ModrinthVersion Version { get; }
    public string Title { get; }
    public string Meta { get; }
    public string Files { get; }
    public bool IsCompatible { get; }
    public string CompatText => IsCompatible ? "✔ подходит для активной сборки" : "";

    public VersionItemViewModel(ModrinthVersion v, bool compatible, ProjectViewModel owner)
    {
        Version = v; _owner = owner; IsCompatible = compatible;
        Title = $"{(string.IsNullOrWhiteSpace(v.Name) ? v.VersionNumber : v.Name)}  ({v.VersionNumber})";
        var mc = v.GameVersions.Count <= 4 ? string.Join(", ", v.GameVersions) : $"{v.GameVersions.First()} … {v.GameVersions.Last()} ({v.GameVersions.Count})";
        Meta = $"{v.VersionType} · {mc} · {string.Join(", ", v.Loaders)} · {v.DatePublished.LocalDateTime:dd.MM.yyyy} · ⬇ {DownloadFormat.Count(v.Downloads)}";
        Files = string.Join("\n", v.Files.Select(f => $"📄 {f.Filename} — {DownloadFormat.Size(f.Size)}{(f.Primary ? " (основной)" : "")}"));
    }

    [RelayCommand] private Task Install() => _owner.InstallAsync(Version.Id);
}

public partial class ProjectViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly IModrinthService _api;
    private readonly IModManagementService _mods;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _log;
    private readonly ModsHostViewModel _host;
    private ModrinthProject? _project;
    private CancellationTokenSource? _cts;

    public ObservableCollection<VersionItemViewModel> Versions { get; } = new();

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _authors = "";
    [ObservableProperty] private string _downloadsText = "";
    [ObservableProperty] private string _datesText = "";
    [ObservableProperty] private string _categoriesText = "";
    [ObservableProperty] private string _gameVersionsText = "";
    [ObservableProperty] private string _loadersText = "";
    [ObservableProperty] private string _bodyText = "";
    [ObservableProperty] private Bitmap? _icon;
    [ObservableProperty] private bool _canInstall;
    [ObservableProperty] private string? _note;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _installStatus = "";

    public string WebUrl => _project?.WebUrl ?? "";
    public string TargetText => _state.ActiveProfile == null ? "Нет активной сборки" : $"Установить в «{_state.ActiveProfile.Name}»";

    public ProjectViewModel(string idOrSlug, ModsHostViewModel host, AppState state, IModrinthService api, IModManagementService mods, IDialogService dialogs, IAppLogger log)
    {
        _host = host; _state = state; _api = api; _mods = mods; _dialogs = dialogs; _log = log;
        _ = LoadAsync(idOrSlug);
    }

    private async Task LoadAsync(string idOrSlug)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            _project = await _api.GetProjectAsync(idOrSlug, ct);
            var p = _project;
            Title = p.Title; Description = p.Description;
            DownloadsText = "⬇ " + DownloadFormat.Count(p.Downloads) + " загрузок";
            DatesText = $"Опубликован: {p.Published?.LocalDateTime:dd.MM.yyyy} · Обновлён: {p.Updated?.LocalDateTime:dd.MM.yyyy}";
            CategoriesText = string.Join(", ", p.Categories.Concat(p.AdditionalCategories).Distinct());
            var gv = p.GameVersions;
            GameVersionsText = gv.Count == 0 ? "—" : (gv.Count <= 12 ? string.Join(", ", gv) : string.Join(", ", gv.AsEnumerable().Reverse().Take(12)) + $" … (всего {gv.Count})");
            LoadersText = p.Loaders.Count == 0 ? "—" : string.Join(", ", p.Loaders);
            BodyText = MarkdownText.ToPlain(p.Body);
            OnPropertyChanged(nameof(WebUrl)); OnPropertyChanged(nameof(TargetText));
            _ = LoadIconAsync(p.IconUrl);

            var kind = ProjectKindExtensions.FromApiType(p.ProjectType) ?? ProjectKind.Mod;
            if (p.Loaders.Count > 0 && p.Loaders.All(l => FreeDomLauncher.Infrastructure.Modrinth.ModrinthFacets.PluginLoaders.Contains(l))) kind = ProjectKind.Plugin;
            CanInstall = kind.IsInstallable();
            Note = kind switch
            {
                ProjectKind.Plugin => "Это серверный плагин — он не устанавливается в клиентскую папку mods.",
                ProjectKind.Modpack => "Установка сборок модов появится в следующей версии.",
                ProjectKind.DataPack => "Датапаки ставятся в мир вручную. Откройте страницу проекта, чтобы скачать.",
                _ => null
            };
            IsLoading = false;

            var membersTask = _api.GetMembersAsync(p.Id, ct);
            var versionsTask = _api.GetVersionsAsync(p.Id, null, null, ct);
            try { Authors = "Авторы: " + string.Join(", ", (await membersTask).Select(m => m.User.Username)); } catch (ModrinthException) { Authors = ""; }
            try
            {
                var versions = (await versionsTask).OrderByDescending(v => v.DatePublished).Take(60).ToList();
                var prof = _state.ActiveProfile;
                var loaders = kind == ProjectKind.Mod && prof != null ? prof.Loader.ModLoaderNames() : null;
                foreach (var v in versions)
                    Versions.Add(new VersionItemViewModel(v, prof != null && VersionFilter.IsCompatible(v, prof.MinecraftVersion, loaders), this));
            }
            catch (ModrinthException ex) { Error = "Версии не загружены: " + ex.Message; }
        }
        catch (OperationCanceledException) { }
        catch (ModrinthException ex) { Error = ex.Message; IsLoading = false; }
        catch (Exception ex) { _log.Error("Страница проекта", ex); Error = "Не удалось загрузить проект: " + ex.Message; IsLoading = false; }
    }

    private async Task LoadIconAsync(string? url)
    {
        var bmp = await ImageCache.LoadAsync(url);
        if (bmp != null) Dispatcher.UIThread.Post(() => Icon = bmp);
    }

    [RelayCommand] private void Back() { _cts?.Cancel(); _host.CloseProject(); }
    [RelayCommand] private void OpenWeb() { if (!string.IsNullOrEmpty(WebUrl)) _dialogs.OpenUrl(WebUrl); }
    [RelayCommand] private Task InstallBest() => InstallAsync(null);

    public async Task InstallAsync(string? versionId)
    {
        if (_project == null || IsInstalling) return;
        var profile = _state.ActiveProfile;
        if (profile == null)
        {
            await _dialogs.ShowAsync("Нет сборки", "Сначала создайте и выберите сборку на вкладке «Сборки».");
            _state.Navigate(NavPage.Profiles); return;
        }
        IsInstalling = true; _state.IsBusy = true; InstallStatus = "Проверка совместимости и зависимостей…";
        try
        {
            var plan = await _mods.PlanInstallAsync(profile, _project.Id, versionId);
            if (!plan.CanInstall)
            {
                InstallStatus = "Установка невозможна.";
                await _dialogs.ShowAsync("Установка невозможна", plan.Warnings.Count > 0 ? string.Join("\n\n", plan.Warnings) : "Не найдено подходящих файлов.");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Сборка: {profile.Name} (Minecraft {profile.MinecraftVersion}, {profile.Loader.DisplayName()})\n");
            sb.AppendLine("Будет установлено:");
            foreach (var i in plan.Items)
                sb.AppendLine($"  • {i.Title} {i.Version.VersionNumber}{(i.IsDependency ? "  (зависимость)" : "")} — {DownloadFormat.Size(i.File.Size)}");
            if (plan.Unavailable.Count > 0) { sb.AppendLine("\nНе удастся установить автоматически:"); foreach (var u in plan.Unavailable) sb.AppendLine("  ⚠ " + u); }
            if (plan.Warnings.Count > 0) { sb.AppendLine("\nПредупреждения:"); foreach (var w in plan.Warnings) sb.AppendLine("  • " + w); }
            if (plan.Optional.Count > 0) sb.AppendLine("\nНеобязательные зависимости (не устанавливаются): " + string.Join(", ", plan.Optional.Distinct()));
            if (!await _dialogs.ConfirmAsync("Установить?", sb.ToString().TrimEnd(), "Установить")) { InstallStatus = ""; return; }

            var status = new Progress<string>(s => InstallStatus = s);
            var outcome = await _mods.ExecuteInstallAsync(profile, plan, status);
            var msg = new System.Text.StringBuilder();
            if (outcome.Installed.Count > 0) msg.AppendLine("Установлено: " + string.Join(", ", outcome.Installed));
            if (outcome.Failed.Count > 0) { msg.AppendLine("\nОшибки:"); foreach (var f in outcome.Failed) msg.AppendLine("  • " + f); }
            InstallStatus = outcome.Success ? "Готово: " + string.Join(", ", outcome.Installed) : "Завершено с ошибками.";
            _state.Status = InstallStatus;
            await _dialogs.ShowAsync(outcome.Success ? "Установка завершена" : "Установка завершена с ошибками", msg.ToString().Trim());
            _host.Installed.MarkDirty();

            if (plan.ManualProjects.Count > 0 &&
                await _dialogs.ConfirmAsync("Недостающие зависимости", "Эти зависимости нужно установить вручную: " + string.Join(", ", plan.ManualProjects) + ".\n\nОткрыть их страницы на Modrinth?", "Открыть"))
                foreach (var slug in plan.ManualProjects.Take(5)) _dialogs.OpenUrl("https://modrinth.com/project/" + Uri.EscapeDataString(slug));
        }
        catch (ModrinthException ex) { InstallStatus = ex.Message; await _dialogs.ShowAsync("Ошибка Modrinth", ex.Message); }
        catch (OperationCanceledException) { InstallStatus = "Отменено."; }
        catch (Exception ex)
        {
            _log.Error("Установка", ex); InstallStatus = ex.Message;
            await _dialogs.ShowAsync("Ошибка установки", ex.Message);
        }
        finally { IsInstalling = false; _state.IsBusy = false; }
    }
}
