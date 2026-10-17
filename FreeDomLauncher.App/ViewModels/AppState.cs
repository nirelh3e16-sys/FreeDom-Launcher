using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;

namespace FreeDomLauncher.App.ViewModels;

public enum NavPage { Home, Profiles, Mods, Accounts, Settings }

/// <summary>Общее состояние приложения: активная сборка, активный аккаунт, строка статуса, запросы навигации.</summary>
public partial class AppState : ObservableObject
{
    private readonly IProfileService _profiles;
    private readonly IAuthenticationService _auth;

    [ObservableProperty] private GameProfile? _activeProfile;
    [ObservableProperty] private AccountInfo? _activeAccount;
    [ObservableProperty] private string _status = "Готово";
    [ObservableProperty] private bool _isBusy;

    public event Action<NavPage>? NavigateRequested;

    public AppState(IProfileService profiles, IAuthenticationService auth)
    {
        _profiles = profiles; _auth = auth;
        profiles.Changed += (_, _) => Dispatcher.UIThread.Post(async () => await RefreshAsync());
        auth.Changed += (_, _) => Dispatcher.UIThread.Post(async () => await RefreshAsync());
    }

    public void Navigate(NavPage page) => NavigateRequested?.Invoke(page);

    public async Task RefreshAsync()
    {
        try
        {
            ActiveProfile = await _profiles.GetActiveAsync();
            ActiveAccount = await _auth.GetActiveAsync();
        }
        catch (Exception ex) { Status = "Ошибка чтения данных: " + ex.Message; }
    }
}
