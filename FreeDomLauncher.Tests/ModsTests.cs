using System.Net;
using System.Security.Cryptography;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Modrinth;
using FreeDomLauncher.Core.Mods;
using FreeDomLauncher.Infrastructure.Mods;
using FreeDomLauncher.Infrastructure.Net;
using Xunit;

namespace FreeDomLauncher.Tests;

public class DependencyTests
{
    private static GameProfile Profile(string dir, LoaderType loader = LoaderType.Fabric)
        => new() { Name = "t", MinecraftVersion = "1.21.1", Loader = loader, FolderPath = dir };

    private static ModrinthDependency Req(string project) => new() { ProjectId = project, DependencyType = "required" };

    [Fact]
    public async Task Required_dependencies_are_found_recursively()
    {
        var api = new FakeModrinth();
        var main = api.AddProject("main", "Main", "mod", "fabric");
        api.AddProject("depA", "Dep A", "mod", "fabric"); api.AddProject("depB", "Dep B", "mod", "fabric");
        var root = api.AddVersion("main", "vm", file: "main.jar", deps: new[] { Req("depA") });
        api.AddVersion("depA", "va", file: "a.jar", deps: new[] { Req("depB") });
        api.AddVersion("depB", "vb", file: "b.jar");

        var plan = await new DependencyResolver(api).ResolveAsync(main, root, Profile("x"), new List<InstalledModRecord>());

        Assert.Equal(new[] { "main", "depA", "depB" }, plan.Items.Select(i => i.ProjectId).ToArray());
        Assert.True(plan.Items[1].IsDependency);
        Assert.False(plan.Items[0].IsDependency);
        Assert.Equal("Dep B", plan.Items[2].Title);
        Assert.True(plan.CanInstall);
    }

    [Fact]
    public async Task Cyclic_dependencies_do_not_loop_and_projects_are_not_duplicated()
    {
        var api = new FakeModrinth();
        var main = api.AddProject("main", "Main", "mod", "fabric");
        api.AddProject("depA", "A", "mod", "fabric");
        var root = api.AddVersion("main", "vm", file: "main.jar", deps: new[] { Req("depA"), Req("depA") });
        api.AddVersion("depA", "va", file: "a.jar", deps: new[] { Req("main"), Req("depA") });

        var plan = await new DependencyResolver(api).ResolveAsync(main, root, Profile("x"), new List<InstalledModRecord>());
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(plan.Items.Select(i => i.ProjectId).Distinct().Count(), plan.Items.Count);
    }

    [Fact]
    public async Task Already_installed_dependency_is_skipped()
    {
        var api = new FakeModrinth();
        var main = api.AddProject("main", "Main", "mod", "fabric");
        var root = api.AddVersion("main", "vm", file: "main.jar", deps: new[] { Req("depA") });
        var installed = new List<InstalledModRecord> { new() { ProjectId = "depA", Title = "Dep A", FileName = "a.jar" } };

        var plan = await new DependencyResolver(api).ResolveAsync(main, root, Profile("x"), installed);
        Assert.Single(plan.Items);
        Assert.Contains(plan.Warnings, w => w.Contains("уже установлена"));
    }

    [Fact]
    public async Task Dependency_unavailable_for_game_version_is_reported_not_installed()
    {
        var api = new FakeModrinth();
        var main = api.AddProject("main", "Main", "mod", "fabric");
        api.AddProject("depA", "Dep A", "mod", "fabric");
        var root = api.AddVersion("main", "vm", file: "main.jar", deps: new[] { Req("depA") });
        api.AddVersion("depA", "old", mc: "1.19", file: "a.jar");        // нет версии для 1.21.1

        var plan = await new DependencyResolver(api).ResolveAsync(main, root, Profile("x"), new List<InstalledModRecord>());
        Assert.Single(plan.Items);
        Assert.Single(plan.Unavailable);
        Assert.Contains("недоступна", plan.Unavailable[0]);
        Assert.Contains("depA", plan.ManualProjects);
    }

    [Fact]
    public async Task Optional_and_incompatible_dependencies_are_handled()
    {
        var api = new FakeModrinth();
        var main = api.AddProject("main", "Main", "mod", "fabric");
        api.AddProject("opt", "Optional One", "mod", "fabric");
        var root = api.AddVersion("main", "vm", file: "main.jar", deps: new[]
        {
            new ModrinthDependency { ProjectId = "opt", DependencyType = "optional" },
            new ModrinthDependency { ProjectId = "bad", DependencyType = "incompatible" },
        });
        var installed = new List<InstalledModRecord> { new() { ProjectId = "bad", Title = "Bad Mod", FileName = "bad.jar" } };

        var plan = await new DependencyResolver(api).ResolveAsync(main, root, Profile("x"), installed);
        Assert.Single(plan.Items);
        Assert.Contains("Optional One", plan.Optional);
        Assert.Contains(plan.Warnings, w => w.Contains("Bad Mod"));
    }

    [Fact]
    public void Duplicate_detection_by_project_and_file_name()
    {
        var installed = new List<InstalledModRecord> { new() { ProjectId = "p1", FileName = "Sodium.jar" } };
        Assert.NotNull(ModRules.FindDuplicate(installed, "p1", "other.jar"));
        Assert.NotNull(ModRules.FindDuplicate(installed, "zzz", "sodium.JAR"));
        Assert.Null(ModRules.FindDuplicate(installed, "zzz", "other.jar"));
    }
}

public class ModManagementTests
{
    private static (ModManagementService svc, GameProfile profile, FakeModrinth api, TempDir dir, FakeHandler handler) Make(
        Func<HttpRequestMessage, HttpResponseMessage>? download = null, LoaderType loader = LoaderType.Fabric)
    {
        var dir = new TempDir();
        var api = new FakeModrinth();
        var handler = new FakeHandler(download ?? (_ => Http.Status(HttpStatusCode.NotFound)));
        var downloader = new FileDownloader(new HttpClient(handler), new FakeSettings());
        var svc = new ModManagementService(api, downloader, new NullLogger());
        var profile = new GameProfile { Name = "t", MinecraftVersion = "1.21.1", Loader = loader, FolderPath = Path.Combine(dir.Path, "p") };
        Directory.CreateDirectory(Path.Combine(profile.FolderPath, "mods"));
        return (svc, profile, api, dir, handler);
    }

    private static string Sha512(byte[] b) => Convert.ToHexString(SHA512.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Manual_files_are_discovered_and_missing_ones_dropped()
    {
        var (svc, p, _, dir, _) = Make(); using var _d = dir;
        File.WriteAllText(Path.Combine(p.FolderPath, "mods", "hand.jar"), "x");
        var list = await svc.GetInstalledAsync(p, false);
        Assert.Single(list);
        Assert.Equal("manual", list[0].Source);
        Assert.True(list[0].Enabled);

        File.Delete(Path.Combine(p.FolderPath, "mods", "hand.jar"));
        Assert.Empty(await svc.GetInstalledAsync(p, false));
    }

    [Fact]
    public async Task Disable_moves_file_to_disabled_folder_and_enable_restores_it()
    {
        var (svc, p, _, dir, _) = Make(); using var _d = dir;
        var file = Path.Combine(p.FolderPath, "mods", "a.jar");
        File.WriteAllText(file, "data");
        var rec = (await svc.GetInstalledAsync(p, false))[0];

        await svc.SetEnabledAsync(p, rec, false);
        Assert.False(File.Exists(file));
        var disabled = Path.Combine(p.FolderPath, "mods-disabled", "a.jar");
        Assert.True(File.Exists(disabled));                       // файл не удалён, а перемещён
        Assert.False((await svc.GetInstalledAsync(p, false))[0].Enabled);

        await svc.SetEnabledAsync(p, rec, true);
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(disabled));
        Assert.Equal("data", File.ReadAllText(file));
    }

    [Fact]
    public async Task Delete_removes_file_and_record()
    {
        var (svc, p, _, dir, _) = Make(); using var _d = dir;
        var file = Path.Combine(p.FolderPath, "mods", "a.jar");
        File.WriteAllText(file, "data");
        var rec = (await svc.GetInstalledAsync(p, false))[0];
        await svc.DeleteAsync(p, rec);
        Assert.False(File.Exists(file));
        Assert.Empty(await svc.GetInstalledAsync(p, false));
    }

    [Fact]
    public async Task Plan_is_blocked_for_vanilla_profile_and_for_plugins()
    {
        var (svc, p, api, dir, _) = Make(loader: LoaderType.Vanilla); using var _d = dir;
        api.AddProject("m", "M", "mod", "fabric"); api.AddVersion("m", "v");
        var plan = await svc.PlanInstallAsync(p, "m", null);
        Assert.True(plan.IsBlocked);
        Assert.Contains(plan.Warnings, w => w.Contains("загрузчик"));

        var (svc2, p2, api2, dir2, _) = Make(); using var _d2 = dir2;
        api2.AddProject("pl", "Plugin", "mod", "paper", "spigot"); api2.AddVersion("pl", "v", loader: "paper");
        var plan2 = await svc2.PlanInstallAsync(p2, "pl", null);
        Assert.True(plan2.IsBlocked);
        Assert.Contains(plan2.Warnings, w => w.Contains("плагин"));
    }

    [Fact]
    public async Task Plan_is_blocked_when_loader_does_not_match()
    {
        var (svc, p, api, dir, _) = Make(); using var _d = dir;
        api.AddProject("m", "M", "mod", "forge"); api.AddVersion("m", "v", loader: "forge");
        var plan = await svc.PlanInstallAsync(p, "m", null);
        Assert.True(plan.IsBlocked);
        Assert.Contains(plan.Warnings, w => w.Contains("не поддерживает Fabric"));
    }

    [Fact]
    public async Task Plan_reports_missing_version_for_game()
    {
        var (svc, p, api, dir, _) = Make(); using var _d = dir;
        api.AddProject("m", "M", "mod", "fabric"); api.AddVersion("m", "v", mc: "1.19");
        var plan = await svc.PlanInstallAsync(p, "m", null);
        Assert.True(plan.IsBlocked);
        Assert.Contains(plan.Warnings, w => w.Contains("Нет версии"));
    }

    [Fact]
    public async Task Install_downloads_verifies_hash_and_writes_index()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var (svc, p, api, dir, _) = Make(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); using var _d = dir;
        api.AddProject("m", "M", "mod", "fabric");
        var v = api.AddVersion("m", "v", file: "m.jar");
        v.Files[0].Hashes["sha512"] = Sha512(bytes);

        var plan = await svc.PlanInstallAsync(p, "m", null);
        Assert.True(plan.CanInstall);
        var outcome = await svc.ExecuteInstallAsync(p, plan, null);

        Assert.True(outcome.Success);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(p.FolderPath, "mods", "m.jar")));
        var rec = (await svc.GetInstalledAsync(p, false)).Single();
        Assert.Equal("modrinth", rec.Source);
        Assert.Equal("m", rec.ProjectId);

        // повторная установка того же проекта блокируется как дубликат
        var again = await svc.PlanInstallAsync(p, "m", null);
        Assert.True(again.IsBlocked);
        Assert.Contains(again.Warnings, w => w.Contains("уже установлен"));
    }

    [Fact]
    public async Task Install_with_wrong_hash_fails_and_leaves_no_file()
    {
        var (svc, p, api, dir, _) = Make(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) }); using var _d = dir;
        api.AddProject("m", "M", "mod", "fabric");
        var v = api.AddVersion("m", "v", file: "m.jar");
        v.Files[0].Hashes["sha512"] = new string('0', 128);

        var plan = await svc.PlanInstallAsync(p, "m", null);
        var outcome = await svc.ExecuteInstallAsync(p, plan, null);

        Assert.False(outcome.Success);
        Assert.Single(outcome.Failed);
        Assert.Contains("SHA-512", outcome.Failed[0]);
        Assert.Empty(Directory.GetFiles(Path.Combine(p.FolderPath, "mods")));      // нет ни jar, ни .part
    }

    [Fact]
    public async Task Install_rejects_path_traversal_in_file_name()
    {
        var (svc, p, api, dir, _) = Make(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) }); using var _d = dir;
        api.AddProject("m", "M", "mod", "fabric");
        api.AddVersion("m", "v", file: "..\\..\\evil.jar");
        var plan = await svc.PlanInstallAsync(p, "m", null);
        var outcome = await svc.ExecuteInstallAsync(p, plan, null);
        // имя сводится к «evil.jar» внутри mods; за пределы папки сборки ничего не пишется
        Assert.False(File.Exists(Path.Combine(dir.Path, "evil.jar")));
        Assert.False(File.Exists(Path.Combine(p.FolderPath, "..", "evil.jar")));
        Assert.True(File.Exists(Path.Combine(p.FolderPath, "mods", "evil.jar")) || outcome.Failed.Count > 0);
    }
}
