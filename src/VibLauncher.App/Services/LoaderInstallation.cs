using VibLauncher.App.Views.Dialogs;
using VibLauncher.Core.Instances;
using VibLauncher.Core.ModLoaders;

namespace VibLauncher.App.Services;

/// <summary>
/// Installs the mod loader an instance says it wants, behind a progress dialog.
/// </summary>
/// <remarks>
/// Two flows need this: creating an instance, where the loader comes from the
/// dialog, and importing a modpack, where it comes from the pack's own index.
/// Keeping it here rather than on either of them means both get the same
/// behaviour when a loader cannot be installed, which for Forge and NeoForge is
/// still the usual case.
/// </remarks>
public static class LoaderInstallation
{
    /// <summary>
    /// Installs <paramref name="loader"/> into an instance and records the
    /// profile id it produced.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the loader has no working installer, or the install was
    /// cancelled or failed. The instance is left as it was.
    /// </returns>
    public static async Task<bool> RunAsync(
        LauncherServices services,
        MinecraftInstance instance,
        LoaderKind loader,
        string? loaderVersion)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(instance);

        var provider = services.Loaders.Find(loader);
        if (provider is null || !provider.CanInstall)
        {
            return false;
        }

        return await ProgressDialog.RunAsync(
            $"Installing {loader.DisplayName()}",
            async (status, token) =>
            {
                var result = await provider
                    .InstallAsync(instance, loaderVersion ?? string.Empty, status, token)
                    .ConfigureAwait(false);

                instance.LoaderVersion = result.LoaderVersion;
                instance.LaunchVersionId = result.VersionId;

                await services.Instances.SaveAsync(instance, token).ConfigureAwait(false);
            }).ConfigureAwait(true);
    }
}
