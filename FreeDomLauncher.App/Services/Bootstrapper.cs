using FreeDomLauncher.App.ViewModels;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Search;
using FreeDomLauncher.Infrastructure.Logging;
using FreeDomLauncher.Infrastructure.Minecraft;
using FreeDomLauncher.Infrastructure.Modrinth;
using FreeDomLauncher.Infrastructure.Mods;
using FreeDomLauncher.Infrastructure.Net;
using FreeDomLauncher.Infrastructure.Security;
using FreeDomLauncher.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace FreeDomLauncher.App.Services;

public static class Bootstrapper
{
    public static IServiceProvider Build()
    {
        var s = new ServiceCollection();

        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        s.AddSingleton(paths);

        s.AddSingleton<ISettingsService, SettingsService>();
        s.AddSingleton<IAppLogger, FileLogger>();
        s.AddSingleton<IProfileService, ProfileService>();
        s.AddSingleton<IPopularProjectsStore, PopularProjectsStore>();
        s.AddSingleton<ISecretStore, DpapiSecretStore>();

        // HTTP-клиенты: общий таймаут для API, отдельный клиент для скачивания больших файлов
        s.AddSingleton<IModrinthService>(sp => new ModrinthClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, sp.GetRequiredService<IAppLogger>()));
        s.AddSingleton(sp => new FileDownloader(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, sp.GetRequiredService<ISettingsService>()));
        s.AddSingleton<IVersionCatalogService>(sp => new VersionCatalogService(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<IAppLogger>()));

        s.AddSingleton<IAuthProvider>(sp => new MicrosoftAuthProvider(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            () => sp.GetRequiredService<ISettingsService>().Current.MicrosoftClientId, sp.GetRequiredService<IAppLogger>()));
        s.AddSingleton<IAuthenticationService>(sp => new AuthenticationService(sp.GetRequiredService<AppPaths>(),
            sp.GetRequiredService<ISecretStore>(), sp.GetServices<IAuthProvider>(), sp.GetRequiredService<IAppLogger>()));

        s.AddSingleton<IMinecraftLauncherService, MinecraftLauncherService>();
        s.AddSingleton<IModManagementService, ModManagementService>();
        s.AddSingleton<ISmartSearchService, SmartSearchService>();
        s.AddSingleton<IDialogService, DialogService>();

        s.AddSingleton<AppState>();
        s.AddSingleton<HomeViewModel>();
        s.AddSingleton<ProfilesViewModel>();
        s.AddSingleton<CatalogViewModel>();
        s.AddSingleton<InstalledModsViewModel>();
        s.AddSingleton<ModsHostViewModel>();
        s.AddSingleton<AccountsViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<MainWindowViewModel>();
        return s.BuildServiceProvider();
    }
}
