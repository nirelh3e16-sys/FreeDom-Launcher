using System.Text.Json;
using FreeDomLauncher.Core.Modrinth;

namespace FreeDomLauncher.Infrastructure.Modrinth;

/// <summary>Строит параметры поиска Modrinth: facets (AND между группами, OR внутри группы), сортировку и т.д.</summary>
public static class ModrinthFacets
{
    public static readonly string[] ClientLoaders = { "fabric", "forge", "neoforge", "quilt" };
    public static readonly string[] PluginLoaders = { "bukkit", "spigot", "paper", "purpur", "folia", "sponge", "velocity", "bungeecord", "waterfall" };

    public static List<List<string>> Build(SearchQuery q)
    {
        var groups = new List<List<string>>();
        switch (q.Kind)
        {
            case ProjectKind.Mod:
                groups.Add(new() { "project_type:mod" });
                // исключаем плагины и датапаки: оставляем только клиентские загрузчики
                groups.Add(string.IsNullOrEmpty(q.Loader)
                    ? ClientLoaders.Select(l => "categories:" + l).ToList()
                    : new() { "categories:" + q.Loader });
                break;
            case ProjectKind.Modpack:
                groups.Add(new() { "project_type:modpack" });
                if (!string.IsNullOrEmpty(q.Loader)) groups.Add(new() { "categories:" + q.Loader });
                break;
            case ProjectKind.ResourcePack: groups.Add(new() { "project_type:resourcepack" }); break;
            case ProjectKind.Shader: groups.Add(new() { "project_type:shader" }); break;
            case ProjectKind.DataPack:
                groups.Add(new() { "project_type:datapack", "categories:datapack" });
                break;
            case ProjectKind.Plugin:
                groups.Add(new List<string> { "project_type:plugin" }.Concat(PluginLoaders.Select(l => "categories:" + l)).ToList());
                break;
        }
        if (!string.IsNullOrEmpty(q.MinecraftVersion)) groups.Add(new() { "versions:" + q.MinecraftVersion });
        if (!string.IsNullOrEmpty(q.Category)) groups.Add(new() { "categories:" + q.Category });
        return groups;
    }

    public static string BuildJson(SearchQuery q) => JsonSerializer.Serialize(Build(q));

    public static string SortIndex(SearchSort s) => s switch
    {
        SearchSort.Downloads => "downloads",
        SearchSort.Newest => "newest",
        SearchSort.Updated => "updated",
        _ => "relevance"
    };

    public static string BuildQueryString(SearchQuery q)
    {
        var limit = Math.Clamp(q.Limit, 1, 100);
        var offset = Math.Max(0, q.Offset);
        return $"search?query={Uri.EscapeDataString(q.Text ?? "")}&facets={Uri.EscapeDataString(BuildJson(q))}" +
               $"&index={SortIndex(q.Sort)}&offset={offset}&limit={limit}";
    }
}
