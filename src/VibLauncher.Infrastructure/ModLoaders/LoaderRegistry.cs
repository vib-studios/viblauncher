using VibLauncher.Core.ModLoaders;

namespace VibLauncher.Infrastructure.ModLoaders;

/// <inheritdoc cref="ILoaderRegistry"/>
/// <remarks>
/// The one place loaders are wired up. Adding a loader means writing an
/// <see cref="ILoaderProvider"/> and adding it to the list passed in here.
/// </remarks>
public sealed class LoaderRegistry : ILoaderRegistry
{
    private readonly Dictionary<LoaderKind, ILoaderProvider> _byKind;

    public LoaderRegistry(IEnumerable<ILoaderProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        Providers = [.. providers];
        _byKind = Providers.ToDictionary(p => p.Kind);
    }

    public IReadOnlyList<ILoaderProvider> Providers { get; }

    public ILoaderProvider? Find(LoaderKind kind) =>
        _byKind.TryGetValue(kind, out var provider) ? provider : null;
}
