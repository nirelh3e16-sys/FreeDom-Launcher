using FreeDomLauncher.App.Services;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Modrinth;
using FreeDomLauncher.Core.Mods;
using FreeDomLauncher.Infrastructure.Mods;

namespace FreeDomLauncher.App.ViewModels;

public partial class InstalledModItemViewModel : ObservableObject
{
    private readonly InstalledModsViewModel _owner;
    public InstalledModRecord Record { get; }
    public ModUpdate? Update { get; private set; }

    public InstalledModItemViewModel(InstalledModRecord r, ModUpdate? update, string? warning, InstalledModsViewModel owner)
    { Record = r; Update = update; Warning = warning; _owner = owner; }

    public string Title => string.IsNullOrWhiteSpace(Record.Title) ? Record.FileName : Record.Title;
    public string VersionText => string.IsNullOrEmpty(Record.VersionNumber) ? "версия неизвестна" : Record.VersionNumber;
    public string KindText => Record.Kind switch { ContentKind.ResourcePack => "Ресурс-пак", ContentKind.Shader => "Шейдер", _ => "Мод" };
    public string SourceText => Record.Source == "modrinth" ? "Modrinth" + (Record.IsDependency ? " · зависимость" : "") : "Установлен вручную";
    public string StatusText => Record.Enabled ? "Включён" : "Отключён";
    public string ToggleText => Record.Enabled ? "Отключить" : "Включить";
    public string FileName => Record.FileName;
    public string? Warning { get; }
    public bool HasWarning => Warning != null;
    public bool HasUpdate => Update != null;
    public string UpdateText => Update == null ? "" : "Обновить до " + Update.NewVersion.VersionNumber;
    public bool CanOpenPage => !string.IsNullOrEmpty(Record.ProjectId);

    public void RefreshState() { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(ToggleText)); }
    public void ClearUpdate() { Update = null; OnPropertyChanged(nameof(HasUpdate)); OnPropertyChanged(nameof(UpdateText)); }

    [RelayCommand] private Task Toggle() => _owner.ToggleAsync(this);
    [RelayCommand] private Task Delete() => _owner.DeleteAsync(this);
    [RelayCommand] private Task UpdateMod() => _owner.UpdateAsync(this);
    [RelayCommand] private void OpenPage() => _owner.OpenPage(this);
}

public partial class InstalledModsViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly IModManagementService _mods;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _log;
    private Dictionary<string, ModUpdate> _updates = new();
    private bool _dirty = true;
    private Guid? _loadedFor;

    public ModsHostViewModel? Host { get; set; }
    public ObservableCollection<InstalledModItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _headerText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _hasProfile;

    public InstalledModsViewModel(AppState state, IModManagementService mods, IDialogService dialogs, IAppLogger log)
    {
        _state = state; _mods = mods; _dialogs = dialogs; _log = log;
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.ActiveProfile) && _state.ActiveProfile?.Id != _loadedFor) { _dirty = true; if (Host?.ShowInstalled == true) _ = RefreshAsync(); }
        };
    }

    public void MarkDirty() => _dirty = true;
    public string CountText => Items.Count.ToString();

    private static string Key(InstalledModRecord r) => r.Kind + "|" + r.FileName.ToLowerInvariant();

    public async Task RefreshIfNeededAsync() { if (_dirty) await RefreshAsync(); }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var p = _state.ActiveProfile;
        HasProfile = p != null;
        _dirty = false;
        if (p == null) { Items.Clear(); IsEmpty = true; HeaderText = "Сборка не выбрана"; SummaryText = ""; return; }
        _loadedFor = p.Id;
        HeaderText = $"Сборка «{p.Name}» · Minecraft {p.MinecraftVersion} · {p.Loader.DisplayName()}";
        IsLoading = true;
        try
        {
            var list = await _mods.GetInstalledAsync(p, true);
            Rebuild(p, list);
        }
        catch (Exception ex) { _log.Error("Чтение списка модов", ex); SummaryText = "Не удалось прочитать список: " + ex.Message; }
        finally { IsLoading = false; }
    }

    private void Rebuild(GameProfile p, IReadOnlyList<InstalledModRecord> list)
    {
        Items.Clear();
        foreach (var r in list)
        {
            _updates.TryGetValue(Key(r), out var upd);
            Items.Add(new InstalledModItemViewModel(r, upd, Compat(p, r), this));
        }
        IsEmpty = Items.Count == 0;
        var off = list.Count(x => !x.Enabled);
        SummaryText = $"Всего: {list.Count} · отключено: {off}" + (_updates.Count > 0 ? $" · обновлений: {_updates.Count}" : "");
        OnPropertyChanged(nameof(CountText));
    }

    private static string? Compat(GameProfile p, InstalledModRecord r)
    {
        if (r.GameVersions.Count > 0 && !r.GameVersions.Contains(p.MinecraftVersion))
            return $"Не заявлена поддержка Minecraft {p.MinecraftVersion} (версия файла для: {string.Join(", ", r.GameVersions.Take(4))}…). Мод может не работать.";
        if (r.Kind == ContentKind.Mod && r.Loaders.Count > 0 && !r.Loaders.Intersect(p.Loader.ModLoaderNames()).Any())
            return $"Мод не заявляет поддержку {p.Loader.DisplayName()} (для: {string.Join(", ", r.Loaders)}).";
        return null;
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        var p = _state.ActiveProfile; if (p == null) return;
        IsLoading = true; _state.IsBusy = true; _state.Status = "Проверка обновлений…";
        try
        {
            var ups = await _mods.CheckUpdatesAsync(p);
            _updates = ups.ToDictionary(u => Key(u.Record), u => u);
            var list = await _mods.GetInstalledAsync(p, false);
            Rebuild(p, list);
            _state.Status = ups.Count == 0 ? "Все моды актуальны" : $"Доступно обновлений: {ups.Count}";
        }
        catch (ModrinthException ex) { await _dialogs.ShowAsync("Проверка обновлений", ex.Message); }
        catch (Exception ex) { await _dialogs.ShowAsync("Ошибка", ex.Message); }
        finally { IsLoading = false; _state.IsBusy = false; }
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        foreach (var it in Items.Where(i => i.HasUpdate).ToList()) await UpdateAsync(it, true);
        await RefreshAsync();
    }

    [RelayCommand] private void OpenFolder() { if (_state.ActiveProfile != null) _dialogs.OpenFolder(Path.Combine(_state.ActiveProfile.FolderPath, "mods")); }
    [RelayCommand] private void GoCatalog() => Host?.ShowCatalogTab();

    public async Task ToggleAsync(InstalledModItemViewModel it)
    {
        var p = _state.ActiveProfile; if (p == null) return;
        try { await _mods.SetEnabledAsync(p, it.Record, !it.Record.Enabled); it.RefreshState(); await RefreshAsync(); }
        catch (Exception ex) { await _dialogs.ShowAsync("Не удалось переключить", ex.Message); }
    }

    public async Task DeleteAsync(InstalledModItemViewModel it)
    {
        var p = _state.ActiveProfile; if (p == null) return;
        if (!await _dialogs.ConfirmAsync("Удалить файл?", $"«{it.Title}» ({it.FileName}) будет удалён из сборки «{p.Name}».", "Удалить")) return;
        try { await _mods.DeleteAsync(p, it.Record); _updates.Remove(Key(it.Record)); await RefreshAsync(); }
        catch (Exception ex) { await _dialogs.ShowAsync("Не удалось удалить", ex.Message); }
    }

    public Task UpdateAsync(InstalledModItemViewModel it) => UpdateAsync(it, false);

    private async Task UpdateAsync(InstalledModItemViewModel it, bool silent)
    {
        var p = _state.ActiveProfile; if (p == null || it.Update == null) return;
        _state.IsBusy = true; _state.Status = $"Обновление: {it.Title}…";
        try
        {
            await _mods.UpdateAsync(p, it.Update);
            _updates.Remove(Key(it.Record));
            _state.Status = $"Обновлено: {it.Title}";
            if (!silent) await RefreshAsync();
        }
        catch (Exception ex) { await _dialogs.ShowAsync("Не удалось обновить", $"{it.Title}: {ex.Message}"); }
        finally { _state.IsBusy = false; }
    }

    public void OpenPage(InstalledModItemViewModel it)
    {
        if (!string.IsNullOrEmpty(it.Record.ProjectId)) Host?.OpenProject(it.Record.ProjectId!);
    }
}
