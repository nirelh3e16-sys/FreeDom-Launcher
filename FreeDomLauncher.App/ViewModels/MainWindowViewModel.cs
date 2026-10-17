using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeDomLauncher.App.Services;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;

namespace FreeDomLauncher.App.ViewModels;

public partial class NavItemViewModel : ObservableObject
{
    private readonly Action<NavPage> _select;
    public NavPage Page { get; }
    public string Title { get; }
    public Bitmap Icon { get; }
    [ObservableProperty] private bool _isActive;

    public NavItemViewModel(NavPage page, string title, string icon, Action<NavPage> select)
    { Page = page; Title = title; Icon = AppAssets.Load("icons/" + icon); _select = select; }

    [RelayCommand] private void Select() => _select(Page);
}

public partial class MainWindowViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly ProfilesViewModel _profiles;
    private readonly ModsHostViewModel _mods;
    private readonly ISmartSearchService _smart;
    private readonly HomeViewModel _home;
    private readonly AccountsViewModel _accounts;
    private readonly SettingsViewModel _settings;
    private readonly IAppLogger _log;

    public Bitmap Logo { get; } = AppAssets.Load("logo_full.png");
    public ObservableCollection<NavItemViewModel> NavItems { get; } = new();
    public string VersionText => "FreeDom Launcher v1.0.0";

    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _profileText = "Сборка не выбрана";
    [ObservableProperty] private string _accountText = "Аккаунт не выбран";

    public AppState State => _state;

    public MainWindowViewModel(AppState state, HomeViewModel home, ProfilesViewModel profiles, ModsHostViewModel mods,
        AccountsViewModel accounts, SettingsViewModel settings, ISmartSearchService smart, IAppLogger log)
    {
        _state = state; _home = home; _profiles = profiles; _mods = mods; _accounts = accounts; _settings = settings; _smart = smart; _log = log;

        NavItems.Add(new NavItemViewModel(NavPage.Home, "Главная", "Home.png", Navigate));
        NavItems.Add(new NavItemViewModel(NavPage.Profiles, "Сборки", "profiles.png", Navigate));
        NavItems.Add(new NavItemViewModel(NavPage.Mods, "Моды и проекты", "workshop.png", Navigate));
        NavItems.Add(new NavItemViewModel(NavPage.Accounts, "Аккаунты", "account.png", Navigate));
        NavItems.Add(new NavItemViewModel(NavPage.Settings, "Настройки", "settings.png", Navigate));

        _state.NavigateRequested += Navigate;
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.ActiveProfile) or nameof(AppState.ActiveAccount)) UpdateHeader();
        };
        Navigate(NavPage.Home);
    }

    public AppState Status => _state;

    private void UpdateHeader()
    {
        var p = _state.ActiveProfile;
        ProfileText = p == null ? "Сборка не выбрана" : $"{p.Name} · {p.MinecraftVersion} · {p.Loader.DisplayName()}";
        AccountText = _state.ActiveAccount?.Username ?? "Аккаунт не выбран";
    }

    public void Navigate(NavPage page)
    {
        foreach (var n in NavItems) n.IsActive = n.Page == page;
        CurrentPage = page switch
        {
            NavPage.Profiles => _profiles,
            NavPage.Mods => _mods,
            NavPage.Accounts => _accounts,
            NavPage.Settings => _settings,
            _ => _home
        };
        if (page == NavPage.Mods) _ = _mods.OnShownAsync();
        if (page == NavPage.Settings) _settings.Reload();
    }

    public async Task InitializeAsync()
    {
        await _state.RefreshAsync();
        UpdateHeader();
        await _profiles.InitializeAsync();
        _ = _smart.RefreshPopularAsync();
        _log.Info("FreeDom Launcher 1.0.0 запущен");
    }
}
