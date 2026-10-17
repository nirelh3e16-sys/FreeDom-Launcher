using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;

namespace FreeDomLauncher.Infrastructure.Storage;

public sealed class PopularProjectsStore : IPopularProjectsStore
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _store = new();
    public PopularProjectsStore(AppPaths paths) => _paths = paths;

    public async Task<PopularCacheFile> LoadAsync(CancellationToken ct = default)
        => await _store.ReadAsync<PopularCacheFile>(_paths.PopularCacheFile, ct) ?? new PopularCacheFile();

    public Task SaveAsync(PopularCacheFile cache, CancellationToken ct = default)
        => _store.WriteAsync(_paths.PopularCacheFile, cache, ct);
}
