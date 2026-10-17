using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeDomLauncher.App.Services;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;

namespace FreeDomLauncher.App.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly IMinecraftLauncherService _launcher;
    private readonly IAuthenticationService _auth;
    private readonly IProfileService _profiles;
    private readonly IVersionCatalogService _catalog;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _log;
    private CancellationTokenSource? _cts;
    private string? _lastLog;

    [ObservableProperty] private string _profileName = "Сборка не выбрана";
    [ObservableProperty] private string _profileInfo = "Создайте сборку на вкладке «Сборки»";
    [ObservableProperty] private Bitmap? _profileArt;
    [ObservableProperty] private string _accountText = "Аккаунт не выбран — добавьте его на вкладке «Аккаунты»";
    [ObservableProperty] private bool _isLaunching;
    [ObservableProperty] private bool _isGameRunning;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private string _javaText = "";

    public HomeViewModel(AppState state, IMinecraftLauncherService launcher, IAuthenticationService auth, IProfileService profiles,
        IVersionCatalogService catalog, IDialogService dialogs, IAppLogger log)
    {
        _state = state; _launcher = launcher; _auth = auth; _profiles = profiles; _catalog = catalog; _dialogs = dialogs; _log = log;
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.ActiveProfile) or nameof(AppState.ActiveAccount)) Update();
        };
        _launcher.ProfileExited += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            _lastLog = e.LogPath ?? _lastLog;
            IsGameRunning = _state.ActiveProfile != null && _launcher.IsRunning(_state.ActiveProfile.Id);
            if (_state.ActiveProfile?.Id == e.ProfileId)
            {
                HasProgress = true; IsIndeterminate = false; ProgressValue = 0;
                ProgressText = e.ExitCode == 0 ? "Игра закрыта." : $"Игра завершилась с ошибкой (код {e.ExitCode}). Нажмите «Открыть лог».";
            }
            _state.Status = e.ExitCode == 0 ? "Игра закрыта" : $"Игра завершилась с кодом {e.ExitCode}";
        });
        Update();
    }

    public string PlayText => IsLaunching ? "Подготовка…" : "▶  Играть";
    partial void OnIsLaunchingChanged(bool value) => OnPropertyChanged(nameof(PlayText));
    public bool HasLog => _lastLog != null;

    private void Update()
    {
        var p = _state.ActiveProfile;
        if (p == null)
        {
            ProfileName = "Сборка не выбрана"; ProfileInfo = "Создайте сборку на вкладке «Сборки»"; ProfileArt = AppAssets.ProfileArt(null);
        }
        else
        {
            ProfileName = p.Name;
            ProfileInfo = $"Minecraft {p.MinecraftVersion} · {p.Loader.DisplayName()}{(string.IsNullOrEmpty(p.LoaderVersion) ? "" : " " + p.LoaderVersion)} · ОЗУ {p.MemoryMb} МБ";
            ProfileArt = AppAssets.ProfileArt(p.MinecraftVersion);
            JavaText = string.IsNullOrWhiteSpace(p.JavaPath) ? "Java: автоматически (лаунчер скачает нужную)" : "Java: " + p.JavaPath;
            IsGameRunning = _launcher.IsRunning(p.Id);
        }
        var a = _state.ActiveAccount;
        AccountText = a == null ? "Аккаунт не выбран — добавьте его на вкладке «Аккаунты»" : $"Аккаунт: {a.Username} (Microsoft)";
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (IsLaunching) return;
        var p = _state.ActiveProfile;
        if (p == null)
        {
            await _dialogs.ShowAsync("Нет сборки", "Сначала создайте сборку на вкладке «Сборки».");
            _state.Navigate(NavPage.Profiles); return;
        }
        var acc = _state.ActiveAccount;
        if (acc == null)
        {
            await _dialogs.ShowAsync("Нужен аккаунт", "Для запуска Minecraft требуется лицензионный аккаунт Microsoft. Добавьте его на вкладке «Аккаунты».");
            _state.Navigate(NavPage.Accounts); return;
        }
        if (!p.Loader.IsInstallSupported())
        {
            await _dialogs.ShowAsync("Загрузчик не поддерживается", $"Загрузчик {p.Loader.DisplayName()} появится в следующих версиях. Сейчас доступны Vanilla и Fabric.");
            return;
        }
        if (_launcher.IsRunning(p.Id) &&
            !await _dialogs.ConfirmAsync("Сборка уже запущена", $"«{p.Name}» уже запущена. Запуск второго экземпляра может повредить миры и конфиги. Запустить ещё один?", "Запустить"))
            return;

        IsLaunching = true; HasProgress = true; IsIndeterminate = true; ProgressValue = 0; ProgressText = "Вход в аккаунт…";
        _state.IsBusy = true; _state.Status = "Подготовка запуска…";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            AuthSession session;
            try { session = await _auth.GetSessionAsync(acc.Uuid, ct); }
            catch (AuthException ex)
            {
                ProgressText = ex.Message;
                await _dialogs.ShowAsync("Не удалось войти", ex.Message);
                _state.Navigate(NavPage.Accounts);
                return;
            }

            if (p.Loader == LoaderType.Fabric && string.IsNullOrEmpty(p.LoaderVersion))
            {
                ProgressText = "Выбор версии Fabric…";
                try
                {
                    p.LoaderVersion = await _catalog.GetLatestFabricLoaderAsync(p.MinecraftVersion, ct);
                    await _profiles.UpdateAsync(p, ct);
                }
                catch (InvalidOperationException ex) { ProgressText = ex.Message; await _dialogs.ShowAsync("Fabric недоступен", ex.Message); return; }
            }

            var progress = new Progress<LaunchProgress>(lp =>
            {
                ProgressText = lp.Stage;
                if (lp.Fraction is double f) { IsIndeterminate = false; ProgressValue = Math.Clamp(f * 100, 0, 100); }
                else IsIndeterminate = true;
                _state.Status = lp.Stage;
            });
            var result = await _launcher.LaunchAsync(p, session, progress, ct);
            _lastLog = result.LogPath ?? _lastLog;
            OnPropertyChanged(nameof(HasLog));
            if (result.Success)
            {
                IsGameRunning = true; IsIndeterminate = false; ProgressValue = 100;
                ProgressText = "Игра запущена. Лог сохраняется в папку логов лаунчера.";
                _state.Status = $"«{p.Name}» запущена";
                p.LastPlayedAt = DateTimeOffset.UtcNow;
                try { await _profiles.UpdateAsync(p); } catch (IOException) { }
            }
            else
            {
                IsIndeterminate = false; ProgressValue = 0; ProgressText = result.Error ?? "Не удалось запустить игру.";
                _state.Status = "Запуск не удался";
                if (!ct.IsCancellationRequested) await _dialogs.ShowAsync("Не удалось запустить игру", result.Error ?? "Неизвестная ошибка.");
            }
        }
        catch (OperationCanceledException) { ProgressText = "Запуск отменён."; }
        catch (Exception ex)
        {
            _log.Error("Ошибка запуска", ex);
            ProgressText = ex.Message;
            await _dialogs.ShowAsync("Ошибка", ex.Message);
        }
        finally
        {
            IsLaunching = false; _state.IsBusy = false;
            _cts?.Dispose(); _cts = null;
        }
    }

    [RelayCommand] private void CancelLaunch() => _cts?.Cancel();

    [RelayCommand]
    private void StopGame()
    {
        if (_state.ActiveProfile != null) _launcher.Kill(_state.ActiveProfile.Id);
    }

    [RelayCommand] private void OpenFolder() { if (_state.ActiveProfile != null) _dialogs.OpenFolder(_state.ActiveProfile.FolderPath); }
    [RelayCommand] private void OpenLog() { if (_lastLog != null) _dialogs.OpenFile(_lastLog); }
    [RelayCommand] private void OpenLogsFolder() => _dialogs.OpenFolder(Path.GetDirectoryName(_lastLog ?? "") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeDomLauncher", "logs"));
    [RelayCommand] private void GoMods() => _state.Navigate(NavPage.Mods);
    [RelayCommand] private void GoProfiles() => _state.Navigate(NavPage.Profiles);
    [RelayCommand] private void GoAccounts() => _state.Navigate(NavPage.Accounts);
}
