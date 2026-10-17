using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeDomLauncher.App.Services;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Modrinth;
using FreeDomLauncher.Core.Search;
using FreeDomLauncher.Core.Util;

namespace FreeDomLauncher.App.ViewModels;

public partial class CategoryItemViewModel : ObservableObject
{
    public ProjectKind Kind { get; }
    public string Title => Kind.Title();
    [ObservableProperty] private bool _isActive;
    private readonly Action<CategoryItemViewModel> _select;
    public CategoryItemViewModel(ProjectKind kind, Action<CategoryItemViewModel> select) { Kind = kind; _select = select; }
    [RelayCommand] private void Select() => _select(this);
}

public sealed record SortOption(string Title, SearchSort Sort);

public partial class ProjectCardViewModel : ObservableObject
{
    private readonly Action<string> _open;
    public string Slug { get; }
    public string Title { get; }
    public string Description { get; }
    public string Author { get; }
    public string DownloadsText { get; }
    public string CategoriesText { get; }
    [ObservableProperty] private Bitmap? _icon;

    public ProjectCardViewModel(ModrinthSearchHit hit, Action<string> open)
    {
        _open = open; Slug = string.IsNullOrEmpty(hit.Slug) ? hit.ProjectId : hit.Slug;
        Title = hit.Title; Description = hit.Description; Author = "от " + hit.Author;
        DownloadsText = "⬇ " + DownloadFormat.Count(hit.Downloads);
        CategoriesText = string.Join(" · ", hit.DisplayCategories.Take(4));
        _ = LoadIconAsync(hit.IconUrl);
    }

    private async Task LoadIconAsync(string? url)
    {
        var bmp = await ImageCache.LoadAsync(url);
        if (bmp != null) Dispatcher.UIThread.Post(() => Icon = bmp);
    }

    [RelayCommand] private void Open() => _open(Slug);
}

public partial class CatalogViewModel : ObservableObject
{
    private const int PageSize = 20;
    private const string AnyText = "Любая";
    private const string AnyLoader = "Любой";

    private readonly AppState _state;
    private readonly IModrinthService _api;
    private readonly ISmartSearchService _smart;
    private readonly IVersionCatalogService _catalog;
    private readonly IAppLogger _log;

    private CancellationTokenSource? _cts;
    private bool _suspend;
    private string? _ignoreSuggestionFor;
    private int _offset;
    private bool _initialized;
    private SmartSuggestion? _suggestion;

    public ModsHostViewModel? Host { get; set; }

    public ObservableCollection<CategoryItemViewModel> Categories { get; } = new();
    public ObservableCollection<ProjectCardViewModel> Results { get; } = new();
    public ObservableCollection<string> McOptions { get; } = new() { AnyText };
    public IReadOnlyList<string> LoaderOptions { get; } = new[] { AnyLoader, "Fabric", "Forge", "NeoForge", "Quilt" };
    public IReadOnlyList<SortOption> SortOptions { get; } = new[]
    {
        new SortOption("По релевантности", SearchSort.Relevance), new SortOption("По популярности", SearchSort.Downloads),
        new SortOption("Новые", SearchSort.Newest), new SortOption("Недавно обновлённые", SearchSort.Updated),
    };

    [ObservableProperty] private ProjectKind _kind = ProjectKind.Mod;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedMc = AnyText;
    [ObservableProperty] private string _selectedLoader = AnyLoader;
    [ObservableProperty] private SortOption _selectedSort;
    [ObservableProperty] private bool _useActiveProfile;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string? _kindNote;
    [ObservableProperty] private bool _hasSuggestion;
    [ObservableProperty] private string _suggestionText = "";
    [ObservableProperty] private string? _activeCategory;
    [ObservableProperty] private bool _showLoaderFilter = true;

    public bool HasCategory => ActiveCategory != null;
    public string CategoryText => ActiveCategory == null ? "" : "Категория: " + ActiveCategory;
    partial void OnActiveCategoryChanged(string? value) { OnPropertyChanged(nameof(HasCategory)); OnPropertyChanged(nameof(CategoryText)); }

    public CatalogViewModel(AppState state, IModrinthService api, ISmartSearchService smart, IVersionCatalogService catalog, IAppLogger log)
    {
        _state = state; _api = api; _smart = smart; _catalog = catalog; _log = log;
        _selectedSort = SortOptions[0];
        foreach (var k in new[] { ProjectKind.Mod, ProjectKind.Modpack, ProjectKind.ResourcePack, ProjectKind.Shader, ProjectKind.DataPack, ProjectKind.Plugin })
            Categories.Add(new CategoryItemViewModel(k, SelectCategory));
        Categories[0].IsActive = true;
        UpdateKindInfo();
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            foreach (var v in await _catalog.GetMinecraftVersionsAsync(false)) McOptions.Add(v.Id);
        }
        catch (Exception ex) { _log.Warn("Версии Minecraft для фильтра не загружены: " + ex.Message); }
        Schedule(true, 0);
    }

    private void SelectCategory(CategoryItemViewModel c)
    {
        foreach (var x in Categories) x.IsActive = x == c;
        Kind = c.Kind;
    }

    partial void OnKindChanged(ProjectKind value)
    {
        UpdateKindInfo();
        if (!value.UsesLoaderFilter()) { _suspend = true; SelectedLoader = AnyLoader; _suspend = false; }
        _ignoreSuggestionFor = null;
        Schedule(true, 0);
    }

    private void UpdateKindInfo()
    {
        ShowLoaderFilter = Kind.UsesLoaderFilter();
        KindNote = Kind switch
        {
            ProjectKind.Plugin => "⚠ Серверные плагины не устанавливаются в клиентскую папку mods. Раздел предназначен только для просмотра.",
            ProjectKind.DataPack => "ℹ На Modrinth нет отдельного типа «карты»: показаны датапаки. Они ставятся в мир вручную; в лаунчере — только просмотр.",
            ProjectKind.Modpack => "ℹ Установка сборок модов (.mrpack) появится в следующей версии: сейчас можно смотреть описание и открывать страницу.",
            _ => null
        };
    }

    partial void OnSearchTextChanged(string value) { if (!_suspend) Schedule(true, 350); }
    partial void OnSelectedMcChanged(string value) { if (!_suspend) Schedule(true, 0); }
    partial void OnSelectedLoaderChanged(string value) { if (!_suspend) Schedule(true, 0); }
    partial void OnSelectedSortChanged(SortOption value) { if (!_suspend) Schedule(true, 0); }

    partial void OnUseActiveProfileChanged(bool value)
    {
        var p = _state.ActiveProfile;
        if (!value || p == null) return;
        _suspend = true;
        SelectedMc = McOptions.Contains(p.MinecraftVersion) ? p.MinecraftVersion : AnyText;
        var loader = p.Loader.DisplayName();
        SelectedLoader = Kind.UsesLoaderFilter() && LoaderOptions.Contains(loader) ? loader : AnyLoader;
        _suspend = false;
        Schedule(true, 0);
    }

    private void Schedule(bool reset, int delayMs)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = RunAsync(reset, delayMs, _cts.Token);
    }

    private SearchQuery BuildQuery(int offset) => new()
    {
        Text = SearchText.Trim(), Kind = Kind,
        MinecraftVersion = SelectedMc == AnyText ? null : SelectedMc,
        Loader = SelectedLoader == AnyLoader || !Kind.UsesLoaderFilter() ? null : SelectedLoader.ToLowerInvariant(),
        Category = ActiveCategory, Sort = SelectedSort.Sort, Offset = offset, Limit = PageSize
    };

    private async Task RunAsync(bool reset, int delayMs, CancellationToken ct)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, ct);        // debounce
            IsLoading = true; ErrorText = null;
            if (reset) { _offset = 0; HasSuggestion = false; _suggestion = null; }
            var query = BuildQuery(_offset);
            var res = await _api.SearchAsync(query, ct);
            ct.ThrowIfCancellationRequested();

            if (reset) Results.Clear();
            foreach (var h in res.Hits) Results.Add(new ProjectCardViewModel(h, OpenProject));
            _offset += res.Hits.Count;
            HasMore = _offset < res.TotalHits && res.Hits.Count > 0;
            IsEmpty = Results.Count == 0;
            TotalText = res.TotalHits > 0 ? $"Найдено: {res.TotalHits}" : "";

            if (reset && !string.IsNullOrWhiteSpace(query.Text) && !string.Equals(_ignoreSuggestionFor, query.Text, StringComparison.OrdinalIgnoreCase))
            {
                var s = await _smart.AnalyzeAsync(query.Text, Kind, res.TotalHits, ct);
                ct.ThrowIfCancellationRequested();
                if (s != null) { _suggestion = s; SuggestionText = s.Message; HasSuggestion = true; }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (ModrinthException ex) { ErrorText = ex.Message; if (reset) { Results.Clear(); IsEmpty = false; HasMore = false; } }
        catch (Exception ex) { _log.Error("Поиск", ex); ErrorText = "Не удалось выполнить поиск: " + ex.Message; }
        finally { if (!ct.IsCancellationRequested) IsLoading = false; }
    }

    [RelayCommand] private void LoadMore() { if (!IsLoading && HasMore) Schedule(false, 0); }
    [RelayCommand] private void Retry() => Schedule(true, 0);

    [RelayCommand]
    private void SearchOriginal()
    {
        _ignoreSuggestionFor = SearchText.Trim();
        HasSuggestion = false;
    }

    [RelayCommand]
    private void SearchSuggested()
    {
        var s = _suggestion; if (s == null) return;
        HasSuggestion = false;
        _suspend = true;
        ActiveCategory = s.Category;
        SearchText = s.SuggestedQuery;
        _ignoreSuggestionFor = s.SuggestedQuery;
        _suspend = false;
        Schedule(true, 0);
    }

    [RelayCommand]
    private void ClearCategory() { ActiveCategory = null; Schedule(true, 0); }

    private void OpenProject(string slug) => Host?.OpenProject(slug);
}
