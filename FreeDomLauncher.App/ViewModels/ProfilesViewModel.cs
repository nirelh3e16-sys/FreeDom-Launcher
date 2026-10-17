using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeDomLauncher.App.Services;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Infrastructure.Minecraft;

namespace FreeDomLauncher.App.ViewModels;

public partial class ProfileItemViewModel : ObservableObject
{
    private readonly ProfilesViewModel _owner;
    public GameProfile Profile { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private double _editMemory;
    [ObservableProperty] private string _editJava = "";
    [ObservableProperty] private string _editMemoryText = "";

    public Bitmap Art { get; }
    public string Info { get; }
    public string Details { get; }

    public ProfileItemViewModel(GameProfile p, bool active, ProfilesViewModel owner)
    {
        Profile = p; _owner = owner; _name = p.Name; _isActive = active;
        Art = AppAssets.ProfileArt(p.MinecraftVersion);
        Info = $"Minecraft {p.MinecraftVersion} · {p.Loader.DisplayName()}{(string.IsNullOrEmpty(p.LoaderVersion) ? "" : " " + p.LoaderVersion)}";
        Details = $"ОЗУ {p.MemoryMb} МБ · Java: {(string.IsNullOrWhiteSpace(p.JavaPath) ? "авто" : p.JavaPath)} · создана {p.CreatedAt.LocalDateTime:dd.MM.yyyy}";
    }

    partial void OnEditMemoryChanged(double value) => EditMemoryText = $"{(int)value} МБ";

    [RelayCommand] private Task Select() => _owner.SelectAsync(this);
    [RelayCommand] private void Edit()
    {
        EditName = Profile.Name; EditMemory = Profile.MemoryMb; EditJava = Profile.JavaPath ?? ""; IsEditing = !IsEditing;
    }
    [RelayCommand] private void CancelEdit() => IsEditing = false;
    [RelayCommand] private Task Save() => _owner.SaveAsync(this);
    [RelayCommand] private Task Duplicate() => _owner.DuplicateAsync(this);
    [RelayCommand] private Task Delete() => _owner.DeleteAsync(this);
    [RelayCommand] private void OpenFolder() => _owner.OpenFolder(this);
    [RelayCommand] private Task BrowseJava() => _owner.BrowseJavaAsync(this);
    [RelayCommand] private Task DetectJava() => _owner.DetectJavaAsync(this);
    [RelayCommand] private void ClearJava() => EditJava = "";
    [RelayCommand] private async Task Mods() { await _owner.SelectAsync(this); _owner.GoMods(); }
}

public partial class ProfilesViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;
    private readonly IVersionCatalogService _catalog;
    private readonly IDialogService _dialogs;
    private readonly IMinecraftLauncherService _launcher;
    private readonly IAppLogger _log;

    public ObservableCollection<ProfileItemViewModel> Items { get; } = new();
    public ObservableCollection<string> McVersions { get; } = new();
    public IReadOnlyList<LoaderType> LoaderOptions { get; } = new[] { LoaderType.Vanilla, LoaderType.Fabric };

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string? _selectedMc;
    [ObservableProperty] private LoaderType _selectedLoader = LoaderType.Fabric;
    [ObservableProperty] private bool _showSnapshots;
    [ObservableProperty] private double _newMemory = 4096;
    [ObservableProperty] private string _newMemoryText = "4096 МБ";
    [ObservableProperty] private string _createStatus = "";
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private bool _versionsLoading;
    [ObservableProperty] private bool _isEmpty = true;

    public double MaxMemoryMb { get; }

    public ProfilesViewModel(AppState state, IProfileService profiles, ISettingsService settings, IVersionCatalogService catalog,
        IDialogService dialogs, IMinecraftLauncherService launcher, IAppLogger log)
    {
        _state = state; _profiles = profiles; _settings = settings; _catalog = catalog; _dialogs = dialogs; _launcher = launcher; _log = log;
        var totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576.0;
        MaxMemoryMb = Math.Clamp(Math.Floor(totalMb / 512) * 512, 2048, 32768);
        _profiles.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(async () => await ReloadAsync());
    }

    partial void OnNewMemoryChanged(double value) => NewMemoryText = $"{(int)value} МБ";
    partial void OnShowSnapshotsChanged(bool value) => _ = LoadVersionsAsync();

    public async Task InitializeAsync()
    {
        NewMemory = Math.Min(_settings.Current.DefaultMemoryMb, MaxMemoryMb);
        ShowSnapshots = _settings.Current.IncludeSnapshots;
        await ReloadAsync();
        await LoadVersionsAsync();
    }

    private async Task LoadVersionsAsync()
    {
        VersionsLoading = true;
        try
        {
            var prev = SelectedMc;
            var list = await _catalog.GetMinecraftVersionsAsync(ShowSnapshots);
            McVersions.Clear();
            foreach (var v in list) McVersions.Add(v.Id);
            SelectedMc = prev != null && McVersions.Contains(prev) ? prev : McVersions.FirstOrDefault();
        }
        catch (Exception ex) { CreateStatus = ex.Message; }
        finally { VersionsLoading = false; }
    }

    public async Task ReloadAsync()
    {
        var all = await _profiles.GetAllAsync();
        var active = await _profiles.GetActiveAsync();
        Items.Clear();
        foreach (var p in all.OrderByDescending(x => x.CreatedAt)) Items.Add(new ProfileItemViewModel(p, active?.Id == p.Id, this));
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (IsCreating) return;
        if (string.IsNullOrWhiteSpace(NewName)) { CreateStatus = "Введите название сборки."; return; }
        if (SelectedMc == null) { CreateStatus = "Выберите версию Minecraft."; return; }
        IsCreating = true; CreateStatus = "Создание сборки…";
        try
        {
            string? loaderVersion = null;
            if (SelectedLoader == LoaderType.Fabric)
            {
                CreateStatus = "Поиск версии Fabric Loader…";
                loaderVersion = await _catalog.GetLatestFabricLoaderAsync(SelectedMc);
                if (loaderVersion == null) { CreateStatus = $"Для Minecraft {SelectedMc} нет Fabric Loader."; return; }
            }
            var p = await _profiles.CreateAsync(new ProfileCreateRequest(NewName.Trim(), SelectedMc, SelectedLoader, loaderVersion, (int)NewMemory, _settings.Current.JavaPath));
            CreateStatus = $"Сборка «{p.Name}» создана. Файлы игры скачаются при первом запуске.";
            NewName = "";
            await _state.RefreshAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or HttpRequestException)
        { CreateStatus = ex.Message; _log.Warn("Создание сборки: " + ex.Message); }
        finally { IsCreating = false; }
    }

    public async Task SelectAsync(ProfileItemViewModel item)
    {
        try { await _profiles.SetActiveAsync(item.Profile.Id); await ReloadAsync(); await _state.RefreshAsync(); }
        catch (Exception ex) { await _dialogs.ShowAsync("Ошибка", ex.Message); }
    }

    public async Task SaveAsync(ProfileItemViewModel item)
    {
        var name = item.EditName.Trim();
        if (name.Length == 0) { await _dialogs.ShowAsync("Название", "Название не может быть пустым."); return; }
        var java = item.EditJava.Trim();
        if (java.Length > 0)
        {
            var ver = await JavaLocator.GetVersionAsync(java);
            if (ver == null)
            {
                await _dialogs.ShowAsync("Java не найдена", "По указанному пути нет рабочей Java. Выберите java.exe/javaw.exe или очистите поле для автоматического режима.");
                return;
            }
        }
        try
        {
            var p = item.Profile.Clone();
            p.Name = name; p.MemoryMb = (int)item.EditMemory; p.JavaPath = java.Length == 0 ? null : java;
            await _profiles.UpdateAsync(p);
            await ReloadAsync(); await _state.RefreshAsync();
        }
        catch (Exception ex) { await _dialogs.ShowAsync("Не удалось сохранить", ex.Message); }
    }

    public async Task DuplicateAsync(ProfileItemViewModel item)
    {
        _state.IsBusy = true; _state.Status = "Копирование сборки…";
        try { await _profiles.DuplicateAsync(item.Profile.Id); _state.Status = "Сборка скопирована"; }
        catch (Exception ex) { await _dialogs.ShowAsync("Не удалось скопировать", ex.Message); }
        finally { _state.IsBusy = false; }
    }

    public async Task DeleteAsync(ProfileItemViewModel item)
    {
        if (_launcher.IsRunning(item.Profile.Id)) { await _dialogs.ShowAsync("Сборка запущена", "Закройте игру перед удалением сборки."); return; }
        if (!await _dialogs.ConfirmAsync("Удалить сборку?",
                $"Сборка «{item.Profile.Name}» и вся её папка (моды, миры, настройки) будут удалены без возможности восстановления.\n\n{item.Profile.FolderPath}", "Удалить"))
            return;
        try { await _profiles.DeleteAsync(item.Profile.Id); await _state.RefreshAsync(); }
        catch (Exception ex) { await _dialogs.ShowAsync("Ошибка удаления", ex.Message); await ReloadAsync(); }
    }

    public void OpenFolder(ProfileItemViewModel item) => _dialogs.OpenFolder(item.Profile.FolderPath);
    public void GoMods() => _state.Navigate(NavPage.Mods);

    public async Task BrowseJavaAsync(ProfileItemViewModel item)
    {
        var f = await _dialogs.PickFileAsync("Выберите java.exe или javaw.exe", "java.exe", "javaw.exe", "java");
        if (f != null) item.EditJava = f;
    }

    public async Task DetectJavaAsync(ProfileItemViewModel item)
    {
        _state.Status = "Поиск Java…";
        var found = await JavaLocator.FindInstalledAsync();
        _state.Status = "Готово";
        if (found.Count == 0)
        {
            await _dialogs.ShowAsync("Java не найдена", "Установленная Java не обнаружена. Оставьте поле пустым — лаунчер скачает нужную Java автоматически.");
            return;
        }
        var best = found.OrderByDescending(j => JavaLocator.MajorVersion(j.Version) ?? 0).First();
        item.EditJava = best.Path;
        _state.Status = $"Найдена Java {best.Version}";
    }
}
