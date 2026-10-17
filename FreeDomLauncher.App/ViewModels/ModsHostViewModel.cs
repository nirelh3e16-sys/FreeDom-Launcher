using FreeDomLauncher.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeDomLauncher.Core;

namespace FreeDomLauncher.App.ViewModels;

/// <summary>Страница «Моды и проекты»: каталог Modrinth, страница проекта и установленные моды активной сборки.</summary>
public partial class ModsHostViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly IModrinthService _api;
    private readonly IModManagementService _mods;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _log;

    public CatalogViewModel Catalog { get; }
    public InstalledModsViewModel Installed { get; }

    [ObservableProperty] private ProjectViewModel? _project;
    [ObservableProperty] private int _tab;   // 0 — каталог, 1 — установленные

    public bool ShowProject => Project != null;
    public bool ShowCatalog => Project == null && Tab == 0;
    public bool ShowInstalled => Project == null && Tab == 1;
    public bool CatalogActive => Tab == 0;
    public bool InstalledActive => Tab == 1;
    public string InstalledTabText => $"Установленные ({Installed.CountText})";

    public ModsHostViewModel(AppState state, CatalogViewModel catalog, InstalledModsViewModel installed, IModrinthService api,
        IModManagementService mods, IDialogService dialogs, IAppLogger log)
    {
        _state = state; Catalog = catalog; Installed = installed; _api = api; _mods = mods; _dialogs = dialogs; _log = log;
        Catalog.Host = this; Installed.Host = this;
        Installed.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(InstalledModsViewModel.IsEmpty) || e.PropertyName == nameof(InstalledModsViewModel.SummaryText)) OnPropertyChanged(nameof(InstalledTabText)); };
    }

    partial void OnProjectChanged(ProjectViewModel? value) => RaiseVisibility();
    partial void OnTabChanged(int value) => RaiseVisibility();

    private void RaiseVisibility()
    {
        OnPropertyChanged(nameof(ShowProject)); OnPropertyChanged(nameof(ShowCatalog)); OnPropertyChanged(nameof(ShowInstalled));
        OnPropertyChanged(nameof(CatalogActive)); OnPropertyChanged(nameof(InstalledActive));
    }

    public async Task OnShownAsync()
    {
        await Catalog.InitializeAsync();
        if (ShowInstalled) await Installed.RefreshIfNeededAsync();
    }

    [RelayCommand] public void ShowCatalogTab() { Project = null; Tab = 0; }
    [RelayCommand]
    private async Task ShowInstalledTab()
    {
        Project = null; Tab = 1;
        await Installed.RefreshIfNeededAsync();
    }

    public void OpenProject(string idOrSlug) => Project = new ProjectViewModel(idOrSlug, this, _state, _api, _mods, _dialogs, _log);
    public void CloseProject() => Project = null;
}
