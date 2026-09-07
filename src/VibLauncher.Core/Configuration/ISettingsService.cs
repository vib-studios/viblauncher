namespace VibLauncher.Core.Configuration;

/// <summary>Loads and saves <see cref="LauncherSettings"/>.</summary>
public interface ISettingsService
{
    /// <summary>The settings currently in effect.</summary>
    LauncherSettings Current { get; }

    /// <summary>Raised after settings are replaced or saved.</summary>
    event EventHandler? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores every setting to its default and persists the result.</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
