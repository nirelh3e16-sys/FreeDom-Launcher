using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Modrinth;

namespace FreeDomLauncher.Core.Mods;

public sealed class PlannedItem
{
    public string ProjectId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public ProjectKind Kind { get; set; } = ProjectKind.Mod;
    public ModrinthVersion Version { get; set; } = new();
    public ModrinthFile File { get; set; } = new();
    public bool IsDependency { get; set; }
}

public sealed class InstallPlan
{
    public List<PlannedItem> Items { get; } = new();
    /// <summary>Предупреждения (несовместимость, уже установлено и т.п.).</summary>
    public List<string> Warnings { get; } = new();
    /// <summary>Зависимости, которые нельзя установить автоматически.</summary>
    public List<string> Unavailable { get; } = new();
    /// <summary>Необязательные зависимости (не устанавливаются автоматически).</summary>
    public List<string> Optional { get; } = new();
    /// <summary>Проекты, которые нужно открыть вручную (Slug или id).</summary>
    public List<string> ManualProjects { get; } = new();
    public bool IsBlocked { get; set; }
    public bool CanInstall => !IsBlocked && Items.Count > 0;
}

public sealed class InstallOutcome
{
    public List<string> Installed { get; } = new();
    public List<string> Failed { get; } = new();
    public bool Success => Failed.Count == 0 && Installed.Count > 0;
}

public sealed class ModUpdate
{
    public InstalledModRecord Record { get; set; } = new();
    public ModrinthVersion NewVersion { get; set; } = new();
}

public static class VersionFilter
{
    private static int TypeRank(string t) => t switch { "release" => 0, "beta" => 1, "alpha" => 2, _ => 3 };

    /// <summary>
    /// Фильтрует версии проекта по версии Minecraft и загрузчику.
    /// loaders == null/пусто — загрузчик не проверяется (ресурс-паки, шейдеры).
    /// </summary>
    public static List<ModrinthVersion> Filter(IEnumerable<ModrinthVersion> versions, string? minecraftVersion, IReadOnlyCollection<string>? loaders)
    {
        return versions
            .Where(v => string.IsNullOrEmpty(minecraftVersion) || v.GameVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase))
            .Where(v => loaders == null || loaders.Count == 0 ||
                        v.Loaders.Any(l => loaders.Contains(l, StringComparer.OrdinalIgnoreCase)))
            .Where(v => v.Files.Count > 0)
            .OrderBy(v => TypeRank(v.VersionType))
            .ThenByDescending(v => v.DatePublished)
            .ToList();
    }

    public static ModrinthVersion? PickBest(IEnumerable<ModrinthVersion> versions, string? minecraftVersion, IReadOnlyCollection<string>? loaders)
        => Filter(versions, minecraftVersion, loaders).FirstOrDefault();

    public static bool IsCompatible(ModrinthVersion v, string? minecraftVersion, IReadOnlyCollection<string>? loaders)
        => Filter(new[] { v }, minecraftVersion, loaders).Count == 1;
}

public static class ModRules
{
    /// <summary>Проект уже установлен (по ProjectId) или файл с таким именем уже есть в индексе.</summary>
    public static InstalledModRecord? FindDuplicate(IEnumerable<InstalledModRecord> installed, string? projectId, string? fileName)
    {
        foreach (var m in installed)
        {
            if (!string.IsNullOrEmpty(projectId) && string.Equals(m.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)) return m;
            if (!string.IsNullOrEmpty(fileName) && string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase)) return m;
        }
        return null;
    }
}

/// <summary>
/// Строит план установки: подбирает совместимую версию, находит обязательные зависимости рекурсивно
/// (с защитой от циклов и повторов), отмечает уже установленные и недоступные.
/// </summary>
public sealed class DependencyResolver
{
    private const int MaxItems = 64;
    private readonly IModrinthService _api;

    public DependencyResolver(IModrinthService api) => _api = api;

    public async Task<InstallPlan> ResolveAsync(ModrinthProject project, ModrinthVersion rootVersion, GameProfile profile,
        IReadOnlyCollection<InstalledModRecord> installed, CancellationToken ct = default)
    {
        var plan = new InstallPlan();
        var kind = ProjectKindExtensions.FromApiType(project.ProjectType) ?? ProjectKind.Mod;
        var loaders = kind == ProjectKind.Mod ? profile.Loader.ModLoaderNames() : null;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.Id };
        var titles = new Dictionary<string, (string Title, string Slug, ProjectKind Kind)>(StringComparer.OrdinalIgnoreCase);
        titles[project.Id] = (project.Title, project.Slug, kind);

        var file = rootVersion.PrimaryFile;
        if (file == null)
        {
            plan.IsBlocked = true;
            plan.Warnings.Add("У выбранной версии нет файлов для скачивания.");
            return plan;
        }
        plan.Items.Add(new PlannedItem { ProjectId = project.Id, Slug = project.Slug, Title = project.Title, Kind = kind, Version = rootVersion, File = file });

        if (kind != ProjectKind.Mod) return plan;   // у ресурс-паков/шейдеров зависимости не обрабатываем

        var queue = new Queue<ModrinthVersion>();
        queue.Enqueue(rootVersion);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            foreach (var dep in current.Dependencies)
            {
                var type = dep.DependencyType?.ToLowerInvariant();
                var depProjectId = dep.ProjectId;

                if (string.IsNullOrEmpty(depProjectId) && !string.IsNullOrEmpty(dep.VersionId))
                {
                    try { depProjectId = (await _api.GetVersionAsync(dep.VersionId!, ct)).ProjectId; }
                    catch (ModrinthException) { depProjectId = null; }
                }
                if (string.IsNullOrEmpty(depProjectId))
                {
                    if (type == "required") plan.Unavailable.Add($"Обязательная зависимость «{dep.FileName ?? "неизвестный проект"}» не опознана — установите её вручную.");
                    continue;
                }

                if (type == "incompatible")
                {
                    var inst = installed.FirstOrDefault(m => string.Equals(m.ProjectId, depProjectId, StringComparison.OrdinalIgnoreCase));
                    if (inst != null) plan.Warnings.Add($"Несовместим с уже установленным модом «{inst.Title}».");
                    continue;
                }
                if (type == "optional") { plan.Optional.Add(depProjectId!); continue; }
                if (type != "required") continue;

                if (!visited.Add(depProjectId!)) continue;                       // цикл или повтор
                var already = installed.FirstOrDefault(m => string.Equals(m.ProjectId, depProjectId, StringComparison.OrdinalIgnoreCase));
                if (already != null) { plan.Warnings.Add($"Зависимость «{already.Title}» уже установлена."); continue; }

                if (plan.Items.Count >= MaxItems)
                {
                    plan.Warnings.Add("Слишком много зависимостей — остановлено на лимите.");
                    return await FinalizeAsync(plan, titles, ct);
                }

                ModrinthVersion? chosen = null;
                try
                {
                    var all = await _api.GetVersionsAsync(depProjectId!, loaders, new[] { profile.MinecraftVersion }, ct);
                    if (!string.IsNullOrEmpty(dep.VersionId))
                        chosen = VersionFilter.Filter(all.Where(v => v.Id == dep.VersionId), profile.MinecraftVersion, loaders).FirstOrDefault();
                    chosen ??= VersionFilter.PickBest(all, profile.MinecraftVersion, loaders);
                }
                catch (ModrinthException ex)
                {
                    plan.Unavailable.Add($"Не удалось получить версии зависимости ({depProjectId}): {ex.Message}");
                    plan.ManualProjects.Add(depProjectId!);
                    continue;
                }

                if (chosen?.PrimaryFile == null)
                {
                    plan.Unavailable.Add($"Зависимость ({depProjectId}) недоступна для Minecraft {profile.MinecraftVersion} и {profile.Loader.DisplayName()}.");
                    plan.ManualProjects.Add(depProjectId!);
                    continue;
                }

                plan.Items.Add(new PlannedItem { ProjectId = depProjectId!, Version = chosen, File = chosen.PrimaryFile, Kind = ProjectKind.Mod, IsDependency = true });
                queue.Enqueue(chosen);
            }
        }
        return await FinalizeAsync(plan, titles, ct);
    }

    private async Task<InstallPlan> FinalizeAsync(InstallPlan plan, Dictionary<string, (string Title, string Slug, ProjectKind Kind)> titles, CancellationToken ct)
    {
        var need = plan.Items.Where(i => string.IsNullOrEmpty(i.Title)).Select(i => i.ProjectId).Distinct().ToList();
        need.AddRange(plan.Optional);
        need.AddRange(plan.ManualProjects);
        need = need.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (need.Count > 0)
        {
            try
            {
                foreach (var p in await _api.GetProjectsAsync(need, ct))
                    titles[p.Id] = (p.Title, p.Slug, ProjectKindExtensions.FromApiType(p.ProjectType) ?? ProjectKind.Mod);
            }
            catch (ModrinthException) { /* названия — косметика */ }
        }
        foreach (var i in plan.Items.Where(i => string.IsNullOrEmpty(i.Title)))
        {
            if (titles.TryGetValue(i.ProjectId, out var t)) { i.Title = t.Title; i.Slug = t.Slug; }
            else i.Title = i.ProjectId;
        }
        for (int k = 0; k < plan.Optional.Count; k++)
            if (titles.TryGetValue(plan.Optional[k], out var t)) plan.Optional[k] = t.Title;
        for (int k = 0; k < plan.ManualProjects.Count; k++)
            if (titles.TryGetValue(plan.ManualProjects[k], out var t)) plan.ManualProjects[k] = t.Slug;
        return plan;
    }
}
