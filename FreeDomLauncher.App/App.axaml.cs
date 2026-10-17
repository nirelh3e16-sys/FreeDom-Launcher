using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FreeDomLauncher.App.Services;
using FreeDomLauncher.App.ViewModels;
using FreeDomLauncher.App.Views;
using FreeDomLauncher.Core;
using Microsoft.Extensions.DependencyInjection;

namespace FreeDomLauncher.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write((Exception)e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) => { CrashLog.Write(e.Exception); e.SetObserved(); };

            Services = Bootstrapper.Build();
            var settings = Services.GetRequiredService<ISettingsService>();
            settings.LoadAsync().GetAwaiter().GetResult();   // маленький файл — читаем до показа окна, чтобы сразу применить тему

            var window = new MainWindow { DataContext = Services.GetRequiredService<MainWindowViewModel>() };
            desktop.MainWindow = window;
            ThemeService.Apply(settings.Current);
            window.Opened += async (_, _) =>
            {
                try { await ((MainWindowViewModel)window.DataContext!).InitializeAsync(); }
                catch (Exception ex) { Services.GetRequiredService<IAppLogger>().Error("Ошибка инициализации", ex); }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
