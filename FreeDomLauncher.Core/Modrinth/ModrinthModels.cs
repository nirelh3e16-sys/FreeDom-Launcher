namespace FreeDomLauncher.Core.Modrinth;

public enum ProjectKind { Mod, Modpack, ResourcePack, Shader, DataPack, Plugin }

public static class ProjectKindExtensions
{
    public static string Title(this ProjectKind k) => k switch
    {
        ProjectKind.Mod => "Моды",
        ProjectKind.Modpack => "Сборки модов",
        ProjectKind.ResourcePack => "Ресурс-паки",
        ProjectKind.Shader => "Шейдеры",
        ProjectKind.DataPack => "Карты и датапаки",
        ProjectKind.Plugin => "Плагины",
        _ => k.ToString()
    };

    /// <summary>Можно ли устанавливать такой проект в папку сборки в версии 1.0.0.</summary>
    public static bool IsInstallable(this ProjectKind k) => k is ProjectKind.Mod or ProjectKind.ResourcePack or ProjectKind.Shader;

    public static bool UsesLoaderFilter(this ProjectKind k) => k is ProjectKind.Mod or ProjectKind.Modpack;

    public static string? FolderName(this ProjectKind k) => k switch
    {
        ProjectKind.Mod => "mods",
        ProjectKind.ResourcePack => "resourcepacks",
        ProjectKind.Shader => "shaderpacks",
        _ => null
    };

    public static ProjectKind? FromApiType(string? apiType) => apiType?.ToLowerInvariant() switch
    {
        "mod" => ProjectKind.Mod,
        "modpack" => ProjectKind.Modpack,
        "resourcepack" => ProjectKind.ResourcePack,
        "shader" => ProjectKind.Shader,
        "datapack" => ProjectKind.DataPack,
        "plugin" => ProjectKind.Plugin,
        _ => null
    };
}

public enum SearchSort { Relevance, Downloads, Newest, Updated }

public sealed class SearchQuery
{
    public string Text { get; set; } = "";
    public ProjectKind Kind { get; set; } = ProjectKind.Mod;
    public string? MinecraftVersion { get; set; }
    public string? Loader { get; set; }          // fabric | forge | neoforge | quilt
    public string? Category { get; set; }        // например "optimization"
    public SearchSort Sort { get; set; } = SearchSort.Relevance;
    public int Offset { get; set; }
    public int Limit { get; set; } = 20;
}

// DTO с политикой snake_case: project_id -> ProjectId и т.д.
public sealed class ModrinthSearchHit
{
    public string ProjectId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string ProjectType { get; set; } = "mod";
    public string Author { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Categories { get; set; } = new();
    public List<string> DisplayCategories { get; set; } = new();
    public long Downloads { get; set; }
    public string? IconUrl { get; set; }
    public DateTimeOffset? DateCreated { get; set; }
    public DateTimeOffset? DateModified { get; set; }
    public List<string> Versions { get; set; } = new();
}

public sealed class ModrinthSearchResult
{
    public List<ModrinthSearchHit> Hits { get; set; } = new();
    public int Offset { get; set; }
    public int Limit { get; set; }
    public int TotalHits { get; set; }
}

public sealed class ModrinthProject
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string ProjectType { get; set; } = "mod";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Body { get; set; }
    public List<string> Categories { get; set; } = new();
    public List<string> AdditionalCategories { get; set; } = new();
    public List<string> Loaders { get; set; } = new();
    public List<string> GameVersions { get; set; } = new();
    public long Downloads { get; set; }
    public string? IconUrl { get; set; }
    public DateTimeOffset? Published { get; set; }
    public DateTimeOffset? Updated { get; set; }
    public string? ClientSide { get; set; }
    public string? ServerSide { get; set; }
    public string? SourceUrl { get; set; }
    public string? IssuesUrl { get; set; }
    public string? WikiUrl { get; set; }

    public string WebUrl => $"https://modrinth.com/{(ProjectType == "mod" ? "mod" : ProjectType)}/{Slug}";
}

public sealed class ModrinthUser { public string Username { get; set; } = ""; }
public sealed class ModrinthMember { public string Role { get; set; } = ""; public ModrinthUser User { get; set; } = new(); }

public sealed class ModrinthFile
{
    public Dictionary<string, string> Hashes { get; set; } = new();
    public string Url { get; set; } = "";
    public string Filename { get; set; } = "";
    public bool Primary { get; set; }
    public long Size { get; set; }

    public string? Sha1 => Hashes.TryGetValue("sha1", out var v) ? v : null;
    public string? Sha512 => Hashes.TryGetValue("sha512", out var v) ? v : null;
}

public sealed class ModrinthDependency
{
    public string? VersionId { get; set; }
    public string? ProjectId { get; set; }
    public string? FileName { get; set; }
    public string DependencyType { get; set; } = "required";   // required | optional | incompatible | embedded
}

public sealed class ModrinthVersion
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string VersionType { get; set; } = "release";        // release | beta | alpha
    public List<string> GameVersions { get; set; } = new();
    public List<string> Loaders { get; set; } = new();
    public DateTimeOffset DatePublished { get; set; }
    public long Downloads { get; set; }
    public bool Featured { get; set; }
    public List<ModrinthFile> Files { get; set; } = new();
    public List<ModrinthDependency> Dependencies { get; set; } = new();

    public ModrinthFile? PrimaryFile => Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();
}

public sealed class ModrinthException : Exception
{
    public int? StatusCode { get; }
    public bool IsRateLimited => StatusCode == 429;
    public ModrinthException(string message, int? statusCode = null, Exception? inner = null) : base(message, inner) => StatusCode = statusCode;
}
