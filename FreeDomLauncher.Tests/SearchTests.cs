using FreeDomLauncher.Core.Modrinth;
using FreeDomLauncher.Core.Search;
using Xunit;

namespace FreeDomLauncher.Tests;

public class SearchTests
{
    private static SuggestionEngine Engine() => new(new ProjectDictionary());

    [Theory]
    [InlineData("ыщвшгь", "Sodium")]
    [InlineData("дшершгь", "Lithium")]   // "lithium" в русской раскладке
    public void Layout_is_corrected(string typed, string expected)
    {
        var s = Engine().Suggest(typed);
        Assert.NotNull(s);
        Assert.Equal(expected, s!.SuggestedQuery);
        Assert.Equal(SuggestionReason.Layout, s.Reason);
        Assert.Contains(expected, s.Message);
    }

    [Fact]
    public void Layout_converter_maps_russian_and_ukrainian()
    {
        Assert.Equal("sodium", LayoutConverter.Convert("ыщвшгь"));
        Assert.Equal("sodium", LayoutConverter.Convert("іщвшгь", true));
        Assert.Equal("s", LayoutConverter.Convert("і", true));
        Assert.Contains("sodium", LayoutConverter.Candidates("ыщвшгь"));
    }

    [Fact]
    public void Correct_latin_query_is_not_changed()
    {
        Assert.Null(Engine().Suggest("sodium"));
        Assert.Null(Engine().Suggest("Sodium"));
        Assert.Null(Engine().Suggest("fabric api"));
    }

    [Theory]
    [InlineData("содиум", "Sodium")]
    [InlineData("литиум", "Lithium")]
    [InlineData("ферритекор", "FerriteCore")]
    public void Transliteration_finds_known_projects(string typed, string expected)
    {
        var s = Engine().Suggest(typed);
        Assert.NotNull(s);
        Assert.Equal(expected, s!.SuggestedQuery);
        Assert.Equal(SuggestionReason.Transliteration, s.Reason);
    }

    [Fact]
    public void Transliteration_without_dictionary_match_gives_no_suggestion()
    {
        Assert.Null(Engine().Suggest("абракадабра"));
    }

    [Theory]
    [InlineData("sodum", "Sodium")]
    [InlineData("lithum", "Lithium")]
    [InlineData("ferritecor", "FerriteCore")]
    [InlineData("sdoium", "Sodium")]        // перестановка соседних букв
    public void Typos_are_corrected(string typed, string expected)
    {
        var s = Engine().Suggest(typed);
        Assert.NotNull(s);
        Assert.Equal(expected, s!.SuggestedQuery);
        Assert.Equal(SuggestionReason.Typo, s.Reason);
    }

    [Fact]
    public void Short_unknown_words_are_not_corrected()
    {
        Assert.Null(Engine().Suggest("abc"));
        Assert.Null(Engine().Suggest("zzzzzzzz"));
    }

    [Theory]
    [InlineData("fps", "optimization", null)]
    [InlineData("оптимизация", "optimization", null)]
    [InlineData("миникарта", null, "minimap")]
    public void Context_aliases(string typed, string? category, string? query)
    {
        var s = Engine().Suggest(typed);
        Assert.NotNull(s);
        Assert.Equal(SuggestionReason.Context, s!.Reason);
        Assert.Equal(category, s.Category);
        Assert.Equal(query ?? "", s.SuggestedQuery);
    }

    [Fact]
    public void Damerau_levenshtein_distance()
    {
        Assert.Equal(0, FuzzyMatcher.Distance("sodium", "sodium"));
        Assert.Equal(1, FuzzyMatcher.Distance("sodum", "sodium"));
        Assert.Equal(1, FuzzyMatcher.Distance("sdoium", "sodium"));   // транспозиция = 1
        Assert.Equal(3, FuzzyMatcher.Distance("abc", "xyz"));
    }

    [Fact]
    public void Popular_cache_extends_dictionary()
    {
        var dict = new ProjectDictionary(new[] { new FreeDomLauncher.Core.Models.PopularProject("custommod", "CustomMod") });
        var s = new SuggestionEngine(dict).Suggest("custommd");
        Assert.NotNull(s);
        Assert.Equal("CustomMod", s!.SuggestedQuery);
    }

    [Fact]
    public async Task Service_hides_suggestion_when_corrected_query_has_no_results()
    {
        var api = new FakeModrinth { SearchTotal = 0 };
        var svc = new SmartSearchService(api, new MemoryStore());
        Assert.Null(await svc.AnalyzeAsync("sodum", ProjectKind.Mod, 0));
    }

    [Fact]
    public async Task Service_returns_suggestion_when_verified_by_search()
    {
        var api = new FakeModrinth { SearchTotal = 5 };
        var svc = new SmartSearchService(api, new MemoryStore());
        var s = await svc.AnalyzeAsync("ыщвшгь", ProjectKind.Mod, 0);
        Assert.NotNull(s);
        Assert.Equal("Sodium", s!.SuggestedQuery);
        Assert.Contains("Sodium", api.SearchedTexts);
    }

    [Fact]
    public async Task Service_skips_weak_typo_when_original_has_many_results()
    {
        var api = new FakeModrinth { SearchTotal = 100 };
        var svc = new SmartSearchService(api, new MemoryStore());
        Assert.Null(await svc.AnalyzeAsync("ferritecor", ProjectKind.Mod, 500));
    }

    private sealed class MemoryStore : FreeDomLauncher.Core.IPopularProjectsStore
    {
        public FreeDomLauncher.Core.Models.PopularCacheFile Data { get; set; } = new();
        public Task<FreeDomLauncher.Core.Models.PopularCacheFile> LoadAsync(CancellationToken ct = default) => Task.FromResult(Data);
        public Task SaveAsync(FreeDomLauncher.Core.Models.PopularCacheFile cache, CancellationToken ct = default) { Data = cache; return Task.CompletedTask; }
    }
}
