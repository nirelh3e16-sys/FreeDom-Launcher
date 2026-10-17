using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;

namespace FreeDomLauncher.Infrastructure.Storage;

public sealed class SettingsService : ISettingsService
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _store = new();
    private AppSettings _current = new AppSettings().Normalize();

    public SettingsService(AppPaths paths) => _paths = paths;

    public AppSettings Current => _current;
    public string InstancesRoot => _current.InstancesRoot ?? _paths.DefaultInstancesRoot;
    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _store.ReadAsync<AppSettings>(_paths.SettingsFile, cancellationToken);
        _current = (loaded ?? new AppSettings()).Normalize();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var copy = settings.Clone().Normalize();
        await _store.WriteAsync(_paths.SettingsFile, copy, cancellationToken);
        _current = copy;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
