using FreeDomLauncher.App.Services;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;

namespace FreeDomLauncher.App.ViewModels;

public partial class AccountItemViewModel : ObservableObject
{
    private readonly AccountsViewModel _owner;
    public AccountInfo Account { get; }
    [ObservableProperty] private bool _isActive;
    public string Username => Account.Username;
    public string Uuid => Account.Uuid;
    public string Provider => Account.Provider == "microsoft" ? "Microsoft" : Account.Provider;

    public AccountItemViewModel(AccountInfo a, bool active, AccountsViewModel owner) { Account = a; _isActive = active; _owner = owner; }
    [RelayCommand] private Task Select() => _owner.SelectAsync(this);
    [RelayCommand] private Task Remove() => _owner.RemoveAsync(this);
}

public partial class AccountsViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly IAuthenticationService _auth;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _log;
    private CancellationTokenSource? _cts;

    public ObservableCollection<AccountItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private bool _hasCode;
    [ObservableProperty] private string _userCode = "";
    [ObservableProperty] private string _verificationUri = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _hasClientId;

    public AccountsViewModel(AppState state, IAuthenticationService auth, ISettingsService settings, IDialogService dialogs, IAppLogger log)
    {
        _state = state; _auth = auth; _settings = settings; _dialogs = dialogs; _log = log;
        _auth.Changed += (_, _) => Dispatcher.UIThread.Post(async () => await ReloadAsync());
        _settings.Changed += (_, _) => HasClientId = !string.IsNullOrWhiteSpace(_settings.Current.MicrosoftClientId);
        HasClientId = !string.IsNullOrWhiteSpace(_settings.Current.MicrosoftClientId);
        _ = ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        try
        {
            var all = await _auth.GetAccountsAsync();
            var active = await _auth.GetActiveAsync();
            Items.Clear();
            foreach (var a in all) Items.Add(new AccountItemViewModel(a, a.Uuid == active?.Uuid, this));
            IsEmpty = Items.Count == 0;
            HasClientId = !string.IsNullOrWhiteSpace(_settings.Current.MicrosoftClientId);
        }
        catch (Exception ex) { StatusText = "Не удалось прочитать аккаунты: " + ex.Message; }
    }

    [RelayCommand]
    private async Task AddMicrosoftAsync()
    {
        if (IsSigningIn) return;
        if (string.IsNullOrWhiteSpace(_settings.Current.MicrosoftClientId))
        {
            await _dialogs.ShowAsync("Нужен Client ID",
                "Для входа через Microsoft требуется зарегистрированное приложение Azure.\n\nВставьте его Application (client) ID в «Настройки → Вход Microsoft». Пошаговая инструкция есть в README, раздел «Авторизация».");
            _state.Navigate(NavPage.Settings);
            return;
        }
        IsSigningIn = true; HasCode = false; StatusText = "Запрашиваю код входа…";
        _cts = new CancellationTokenSource();
        try
        {
            var acc = await _auth.AddAccountAsync("microsoft", info =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UserCode = info.UserCode; VerificationUri = info.VerificationUri; HasCode = true;
                    StatusText = "Откройте страницу входа Microsoft, введите код и подтвердите вход. Ожидаю подтверждения…";
                    _dialogs.OpenUrl(info.VerificationUri);
                });
            }, _cts.Token);
            StatusText = $"Аккаунт {acc.Username} добавлен.";
            await _state.RefreshAsync();
        }
        catch (AuthException ex) { StatusText = ex.Message; await _dialogs.ShowAsync("Не удалось войти", ex.Message); }
        catch (OperationCanceledException) { StatusText = "Вход отменён."; }
        catch (PlatformNotSupportedException ex) { StatusText = ex.Message; await _dialogs.ShowAsync("Не поддерживается", ex.Message); }
        catch (Exception ex)
        {
            _log.Error("Вход Microsoft", ex);
            StatusText = "Ошибка входа: " + ex.Message;
            await _dialogs.ShowAsync("Ошибка входа", ex.Message);
        }
        finally { IsSigningIn = false; HasCode = false; _cts?.Dispose(); _cts = null; }
    }

    [RelayCommand] private void CancelSignIn() => _cts?.Cancel();
    [RelayCommand] private async Task CopyCodeAsync() { if (UserCode.Length > 0) await _dialogs.CopyToClipboardAsync(UserCode); }
    [RelayCommand] private void OpenVerification() { if (VerificationUri.Length > 0) _dialogs.OpenUrl(VerificationUri); }

    public async Task SelectAsync(AccountItemViewModel it)
    {
        try { await _auth.SetActiveAsync(it.Uuid); await _state.RefreshAsync(); }
        catch (Exception ex) { await _dialogs.ShowAsync("Ошибка", ex.Message); }
    }

    public async Task RemoveAsync(AccountItemViewModel it)
    {
        if (!await _dialogs.ConfirmAsync("Удалить аккаунт?", $"Аккаунт {it.Username} и сохранённые токены будут удалены с этого компьютера.", "Удалить")) return;
        try { await _auth.RemoveAsync(it.Uuid); await _state.RefreshAsync(); }
        catch (Exception ex) { await _dialogs.ShowAsync("Ошибка", ex.Message); }
    }
}
