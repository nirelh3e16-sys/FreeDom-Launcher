using System.Text;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Modrinth;

namespace FreeDomLauncher.Core.Search;

public enum SuggestionReason { Layout, Transliteration, Typo, Context }

public sealed class SmartSuggestion
{
    public string Original { get; init; } = "";
    /// <summary>Исправленный текстовый запрос (может быть пустым, если подсказка — только категория).</summary>
    public string SuggestedQuery { get; init; } = "";
    public string? Category { get; init; }
    public SuggestionReason Reason { get; init; }
    public double Confidence { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>Исправление неверной раскладки (RU, UK; казахская раскладка использует тот же базовый набор букв).</summary>
public static class LayoutConverter
{
    private const string RuFrom = "йцукенгшщзхъфывапролджэячсмитьбюё";
    private const string RuTo   = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
    private const string UkFrom = "йцукенгшщзхїфівапролджєячсмитьбюґ";
    private const string UkTo   = "qwertyuiop[]asdfghjkl;'zxcvbnm,.\\";

    public static bool ContainsCyrillic(string s) => s.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё' or 'і' or 'ї' or 'є' or 'ґ' or 'І' or 'Ї' or 'Є' or 'Ґ');

    public static string Convert(string text, bool ukrainian = false)
    {
        var from = ukrainian ? UkFrom : RuFrom;
        var to = ukrainian ? UkTo : RuTo;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            var i = from.IndexOf(ch);
            sb.Append(i >= 0 ? to[i] : ch);
        }
        return sb.ToString();
    }

    public static IEnumerable<string> Candidates(string text)
    {
        if (!ContainsCyrillic(text)) yield break;
        var ru = Convert(text);
        yield return ru;
        var uk = Convert(text, true);
        if (uk != ru) yield return uk;
    }
}

/// <summary>Простая фонетическая транслитерация RU → латиница с несколькими вариантами для неоднозначных букв.</summary>
public static class Transliterator
{
    private static readonly Dictionary<char, string[]> Map = new()
    {
        ['а'] = new[] { "a" }, ['б'] = new[] { "b" }, ['в'] = new[] { "v" }, ['г'] = new[] { "g" },
        ['д'] = new[] { "d" }, ['е'] = new[] { "e" }, ['ё'] = new[] { "e" }, ['ж'] = new[] { "zh" },
        ['з'] = new[] { "z" }, ['и'] = new[] { "i", "y" }, ['й'] = new[] { "y", "i" }, ['к'] = new[] { "k", "c" },
        ['л'] = new[] { "l" }, ['м'] = new[] { "m" }, ['н'] = new[] { "n" }, ['о'] = new[] { "o" },
        ['п'] = new[] { "p" }, ['р'] = new[] { "r" }, ['с'] = new[] { "s", "c" }, ['т'] = new[] { "t" },
        ['у'] = new[] { "u" }, ['ф'] = new[] { "f" }, ['х'] = new[] { "h", "x" }, ['ц'] = new[] { "c", "ts" },
        ['ч'] = new[] { "ch" }, ['ш'] = new[] { "sh" }, ['щ'] = new[] { "sh" }, ['ъ'] = new[] { "" },
        ['ы'] = new[] { "y", "i" }, ['ь'] = new[] { "" }, ['э'] = new[] { "e" }, ['ю'] = new[] { "yu", "u" },
        ['я'] = new[] { "ya", "a" }, ['і'] = new[] { "i" }, ['ї'] = new[] { "yi" }, ['є'] = new[] { "ye" }, ['ґ'] = new[] { "g" },
        // казахские буквы
        ['ә'] = new[] { "a" }, ['ғ'] = new[] { "g" }, ['қ'] = new[] { "k" }, ['ң'] = new[] { "n" },
        ['ө'] = new[] { "o" }, ['ұ'] = new[] { "u" }, ['ү'] = new[] { "u" }, ['һ'] = new[] { "h" },
    };

    public static IReadOnlyList<string> Variants(string text, int max = 24)
    {
        var results = new List<string> { "" };
        foreach (var ch in text.ToLowerInvariant())
        {
            if (!Map.TryGetValue(ch, out var opts))
            {
                if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') opts = new[] { ch.ToString() };
                else if (ch == ' ' || ch == '-') opts = new[] { "" };
                else return Array.Empty<string>();
            }
            var next = new List<string>();
            foreach (var r in results)
                foreach (var o in opts)
                {
                    next.Add(r + o);
                    if (next.Count >= max) break;
                }
            results = next;
        }
        return results.Distinct().ToList();
    }
}

public static class FuzzyMatcher
{
    /// <summary>Расстояние Дамерау — Левенштейна (вариант Optimal String Alignment).</summary>
    public static int Distance(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>Допустимое расстояние зависит от длины слова: короткие слова почти не правим.</summary>
    public static int Threshold(int length) => length switch { < 4 => 0, <= 6 => 1, <= 10 => 2, _ => 3 };

    public static (string Key, int Distance)? Closest(string word, IEnumerable<string> keys, int maxDistance)
    {
        (string, int)? best = null;
        foreach (var k in keys)
        {
            if (Math.Abs(k.Length - word.Length) > maxDistance) continue;
            var dist = Distance(word, k);
            if (dist <= maxDistance && (best == null || dist < best.Value.Item2)) best = (k, dist);
            if (dist == 0) break;
        }
        return best;
    }
}

public sealed record ContextHint(string? Category, string? Query, string Message);

/// <summary>Словарь популярных проектов, русских алиасов и контекстных фраз.</summary>
public sealed class ProjectDictionary
{
    private static readonly (string Slug, string Title)[] Seed =
    {
        ("sodium","Sodium"),("lithium","Lithium"),("ferrite-core","FerriteCore"),("iris","Iris Shaders"),
        ("fabric-api","Fabric API"),("modmenu","Mod Menu"),("xaeros-minimap","Xaero's Minimap"),
        ("xaeros-world-map","Xaero's World Map"),("journeymap","JourneyMap"),("jei","Just Enough Items"),
        ("rei","Roughly Enough Items"),("emi","EMI"),("appleskin","AppleSkin"),("lambdynamiclights","LambDynamicLights"),
        ("entityculling","Entity Culling"),("immediatelyfast","ImmediatelyFast"),("moreculling","More Culling"),
        ("dynamic-fps","Dynamic FPS"),("starlight","Starlight"),("krypton","Krypton"),("c2me-fabric","C2ME"),
        ("noisium","Noisium"),("memoryleakfix","Memory Leak Fix"),("no-chat-reports","No Chat Reports"),
        ("sodium-extra","Sodium Extra"),("reeses-sodium-options","Reese's Sodium Options"),("indium","Indium"),
        ("continuity","Continuity"),("cloth-config","Cloth Config API"),("architectury-api","Architectury API"),
        ("geckolib","GeckoLib"),("create-fabric","Create Fabric"),("farmers-delight-fabric","Farmer's Delight"),
        ("jade","Jade"),("wthit","WTHIT"),("inventory-profiles-next","Inventory Profiles Next"),
        ("mouse-tweaks","Mouse Tweaks"),("litematica","Litematica"),("malilib","MaLiLib"),("tweakeroo","Tweakeroo"),
        ("worldedit","WorldEdit"),("simple-voice-chat","Simple Voice Chat"),("carry-on","Carry On"),
        ("sound-physics-remastered","Sound Physics Remastered"),("3dskinlayers","3D Skin Layers"),
        ("flashback","Flashback"),("chunky","Chunky"),("fresh-animations","Fresh Animations"),
        ("essential","Essential"),("lithostitched","Lithostitched"),("yacl","YetAnotherConfigLib"),
        ("fabric-language-kotlin","Fabric Language Kotlin"),("bclib","BCLib"),("terralith","Terralith"),
        ("tectonic","Tectonic"),("distanthorizons","Distant Horizons"),("bobby","Bobby"),
        ("zoomify","Zoomify"),("ok-zoomer","Ok Zoomer"),("controlling","Controlling"),("torohealth","ToroHealth"),
        ("better-ping-display-fabric","Better Ping Display"),("capes","Capes"),("replaymod","Replay Mod"),
        ("axiom","Axiom"),("shulkerboxtooltip","Shulker Box Tooltip"),("trinkets","Trinkets"),
        ("fabric-seasons","Fabric Seasons"),("lootr","Lootr"),("ledger","Ledger"),
    };

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["содиум"] = "Sodium", ["содиумэкстра"] = "Sodium Extra", ["литиум"] = "Lithium", ["лития"] = "Lithium",
        ["ферритекор"] = "FerriteCore", ["феррит"] = "FerriteCore", ["ирис"] = "Iris Shaders", ["айрис"] = "Iris Shaders",
        ["фабрикапи"] = "Fabric API", ["фабрикапиай"] = "Fabric API", ["модменю"] = "Mod Menu",
        ["ксаеро"] = "Xaero's Minimap", ["ксаерос"] = "Xaero's Minimap", ["джурнимап"] = "JourneyMap",
        ["журнимап"] = "JourneyMap", ["джей"] = "Just Enough Items", ["джеи"] = "Just Enough Items",
        ["эппледскин"] = "AppleSkin", ["эмби"] = "EMI", ["литематика"] = "Litematica", ["ворлдэдит"] = "WorldEdit",
        ["вордэдит"] = "WorldEdit", ["крейт"] = "Create Fabric", ["создать"] = "Create Fabric",
        ["симплвойс"] = "Simple Voice Chat", ["войсчат"] = "Simple Voice Chat", ["терралит"] = "Terralith",
        ["чанки"] = "Chunky", ["дистанс"] = "Distant Horizons", ["зумифай"] = "Zoomify", ["зум"] = "Zoomify",
        ["флешбек"] = "Flashback", ["реплеймод"] = "Replay Mod", ["реплей"] = "Replay Mod",
    };

    private static readonly Dictionary<string, ContextHint> Contexts = new()
    {
        ["fps"] = new("optimization", null, "Моды оптимизации — например, Sodium, Lithium, FerriteCore"),
        ["фпс"] = new("optimization", null, "Моды оптимизации — например, Sodium, Lithium, FerriteCore"),
        ["оптимизация"] = new("optimization", null, "Моды оптимизации — например, Sodium, Lithium, FerriteCore"),
        ["оптимизации"] = new("optimization", null, "Моды оптимизации — например, Sodium, Lithium, FerriteCore"),
        ["optimization"] = new("optimization", null, "Моды оптимизации — например, Sodium, Lithium, FerriteCore"),
        ["performance"] = new("optimization", null, "Моды оптимизации — например, Sodium, Lithium, FerriteCore"),
        ["лаги"] = new("optimization", null, "Моды оптимизации — например, Sodium, Lithium, FerriteCore"),
        ["производительность"] = new("optimization", null, "Моды оптимизации — например, Sodium, Lithium, FerriteCore"),
        ["миникарта"] = new(null, "minimap", "Моды мини-карты"),
        ["мини карта"] = new(null, "minimap", "Моды мини-карты"),
        ["минимап"] = new(null, "minimap", "Моды мини-карты"),
        ["карта мира"] = new(null, "world map", "Моды карты мира"),
        ["инвентарь"] = new(null, "inventory", "Моды для инвентаря"),
        ["зум"] = new(null, "zoom", "Моды приближения (зум)"),
        ["звук"] = new(null, "sound", "Моды со звуками"),
    };

    private readonly Dictionary<string, string> _keyToTitle = new(StringComparer.Ordinal);   // нормализованный ключ → название для поиска

    public ProjectDictionary(IEnumerable<PopularProject>? extra = null)
    {
        foreach (var (slug, title) in Seed) Add(slug, title);
        if (extra != null) foreach (var p in extra) Add(p.Slug, p.Title);
    }

    private void Add(string slug, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        _keyToTitle.TryAdd(NormalizeKey(title), title);
        if (!string.IsNullOrWhiteSpace(slug)) _keyToTitle.TryAdd(NormalizeKey(slug), title);
    }

    public IEnumerable<string> Keys => _keyToTitle.Keys;
    public bool TryGetTitle(string key, out string title) => _keyToTitle.TryGetValue(key, out title!);
    public static bool TryGetAlias(string key, out string title) => Aliases.TryGetValue(key, out title!);
    public static bool TryGetContext(string normalizedPhrase, out ContextHint hint) => Contexts.TryGetValue(normalizedPhrase, out hint!);

    /// <summary>Нижний регистр, без пробелов, дефисов и апострофов.</summary>
    public static string NormalizeKey(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(c) || (c is >= 'а' and <= 'я') || c is '[' or ']' or ';' or ',' or '.' or '`') sb.Append(c);
        return sb.ToString();
    }
}

/// <summary>Офлайн-часть умного поиска: формирует кандидатов и оценивает уверенность. Сетевая проверка — в SmartSearchService.</summary>
public sealed class SuggestionEngine
{
    private readonly ProjectDictionary _dict;
    public SuggestionEngine(ProjectDictionary dict) => _dict = dict;

    public SmartSuggestion? Suggest(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var original = query.Trim();
        var phrase = original.ToLowerInvariant();
        var key = ProjectDictionary.NormalizeKey(original);
        if (key.Length < 2) return null;

        // 1. Контекстные псевдонимы
        if (ProjectDictionary.TryGetContext(phrase, out var ctx))
            return new SmartSuggestion
            {
                Original = original, SuggestedQuery = ctx.Query ?? "", Category = ctx.Category, Reason = SuggestionReason.Context,
                Confidence = 0.9, Message = ctx.Message
            };

        // 2. Явные русские алиасы (содиум → Sodium)
        if (ProjectDictionary.TryGetAlias(key, out var aliasTitle))
            return Build(original, aliasTitle, SuggestionReason.Transliteration, 0.95);

        var candidates = new List<SmartSuggestion>();

        if (LayoutConverter.ContainsCyrillic(original))
        {
            // 3. Неверная раскладка
            foreach (var conv in LayoutConverter.Candidates(original))
            {
                var ck = ProjectDictionary.NormalizeKey(conv);
                if (_dict.TryGetTitle(ck, out var t)) candidates.Add(Build(original, t, SuggestionReason.Layout, 0.95));
                else if (ck.Length >= 4)
                {
                    var f = FuzzyMatcher.Closest(ck, _dict.Keys, Math.Min(1, FuzzyMatcher.Threshold(ck.Length)));
                    if (f != null && _dict.TryGetTitle(f.Value.Key, out var t2)) candidates.Add(Build(original, t2, SuggestionReason.Layout, 0.7));
                }
            }
            // 4. Транслитерация + нечёткое сравнение со словарём
            foreach (var v in Transliterator.Variants(original))
            {
                var vk = ProjectDictionary.NormalizeKey(v);
                if (vk.Length < 3) continue;
                if (_dict.TryGetTitle(vk, out var t)) { candidates.Add(Build(original, t, SuggestionReason.Transliteration, 0.9)); continue; }
                var f = FuzzyMatcher.Closest(vk, _dict.Keys, Math.Max(1, vk.Length / 5));
                if (f != null && _dict.TryGetTitle(f.Value.Key, out var t3))
                    candidates.Add(Build(original, t3, SuggestionReason.Transliteration, f.Value.Distance <= 1 ? 0.85 : 0.72));
            }
        }
        else
        {
            // 5. Опечатки
            if (_dict.TryGetTitle(key, out _)) return null;   // запрос уже совпадает с известным проектом
            var max = FuzzyMatcher.Threshold(key.Length);
            if (max > 0)
            {
                var f = FuzzyMatcher.Closest(key, _dict.Keys, max);
                if (f != null && f.Value.Distance > 0 && _dict.TryGetTitle(f.Value.Key, out var t))
                    candidates.Add(Build(original, t, SuggestionReason.Typo, f.Value.Distance == 1 ? 0.85 : 0.7));
            }
            // отдельные слова в многословном запросе
            var tokens = original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (candidates.Count == 0 && tokens.Length > 1)
            {
                var fixedTokens = new List<string>(); bool changed = false;
                foreach (var tok in tokens)
                {
                    var tk = ProjectDictionary.NormalizeKey(tok);
                    var m = FuzzyMatcher.Threshold(tk.Length);
                    var f = m > 0 && !_dict.TryGetTitle(tk, out _) ? FuzzyMatcher.Closest(tk, _dict.Keys, 1) : null;
                    if (f != null && f.Value.Distance == 1 && !tk.Contains(' ') && _dict.TryGetTitle(f.Value.Key, out var tt) && !tt.Contains(' '))
                    { fixedTokens.Add(tt.ToLowerInvariant()); changed = true; }
                    else fixedTokens.Add(tok);
                }
                if (changed) candidates.Add(Build(original, string.Join(' ', fixedTokens), SuggestionReason.Typo, 0.7));
            }
        }

        return candidates
            .Where(c => !string.Equals(c.SuggestedQuery, original, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Confidence)
            .FirstOrDefault();
    }

    private static SmartSuggestion Build(string original, string title, SuggestionReason reason, double confidence) => new()
    {
        Original = original, SuggestedQuery = title, Reason = reason, Confidence = confidence,
        Message = $"Возможно, вы имели в виду {title}?"
    };
}

public sealed class SmartSearchService : ISmartSearchService
{
    public const double MinConfidence = 0.65;
    private readonly IModrinthService _api;
    private readonly IPopularProjectsStore _store;
    private readonly IAppLogger? _log;
    private SuggestionEngine _engine = new(new ProjectDictionary());
    private bool _loaded;

    public SmartSearchService(IModrinthService api, IPopularProjectsStore store, IAppLogger? log = null)
    { _api = api; _store = store; _log = log; }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        try
        {
            var cache = await _store.LoadAsync(ct);
            _engine = new SuggestionEngine(new ProjectDictionary(cache.Items));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log?.Warn("Не удалось прочитать кэш популярных проектов: " + ex.Message); }
        _loaded = true;
    }

    public async Task<SmartSuggestion?> AnalyzeAsync(string query, ProjectKind kind, int originalTotalHits, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var s = _engine.Suggest(query);
        if (s == null || s.Confidence < MinConfidence) return null;

        // Если исходный запрос и так дал много результатов, слабые догадки не показываем.
        if (s.Reason != SuggestionReason.Context && s.Confidence < 0.9 && originalTotalHits >= 30) return null;
        if (s.Reason == SuggestionReason.Context) return s;

        // Проверяем реальным поиском, что у исправленного запроса есть результаты.
        try
        {
            var probe = await _api.SearchAsync(new SearchQuery { Text = s.SuggestedQuery, Kind = kind, Limit = 1 }, ct);
            return probe.TotalHits > 0 ? s : null;
        }
        catch (ModrinthException ex) { _log?.Warn("Проверка подсказки не удалась: " + ex.Message); return null; }
    }

    public async Task RefreshPopularAsync(CancellationToken ct = default)
    {
        try
        {
            var cache = await _store.LoadAsync(ct);
            if (cache.Items.Count > 0 && DateTimeOffset.UtcNow - cache.UpdatedAt < TimeSpan.FromDays(7)) return;
            var items = new List<PopularProject>();
            for (int offset = 0; offset < 200; offset += 100)
            {
                var r = await _api.SearchAsync(new SearchQuery { Kind = ProjectKind.Mod, Sort = SearchSort.Downloads, Offset = offset, Limit = 100 }, ct);
                items.AddRange(r.Hits.Select(h => new PopularProject(h.Slug, h.Title)));
                if (r.Hits.Count < 100) break;
            }
            if (items.Count == 0) return;
            await _store.SaveAsync(new PopularCacheFile { UpdatedAt = DateTimeOffset.UtcNow, Items = items }, ct);
            _engine = new SuggestionEngine(new ProjectDictionary(items));
            _loaded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn("Не удалось обновить кэш популярных проектов: " + ex.Message);
        }
    }
}
