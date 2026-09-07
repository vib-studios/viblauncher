using VibLauncher.Core.Common;

namespace VibLauncher.Core.Configuration;

/// <inheritdoc cref="ISettingsService"/>
public sealed class SettingsService : ISettingsService
{
    private readonly ILauncherPaths _paths;

    public SettingsService(ILauncherPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public LauncherSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // A corrupt settings file must not stop the launcher from opening, so
        // ReadJsonAsync quarantines it and we fall back to defaults.
        Current = await AtomicFile.ReadJsonAsync<LauncherSettings>(_paths.SettingsFile, cancellationToken)
                      .ConfigureAwait(false)
                  ?? new LauncherSettings();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await AtomicFile.WriteJsonAsync(_paths.SettingsFile, Current, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        Current = new LauncherSettings();
        return SaveAsync(cancellationToken);
    }
}
