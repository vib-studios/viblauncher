using VibLauncher.Core.ModLoaders;

namespace VibLauncher.Core.Instances;

/// <summary>What the user chose in the Create Instance dialog.</summary>
/// <param name="Name">The display name. Converted to a folder-safe id by the manager.</param>
/// <param name="MinecraftVersion">The Minecraft version id.</param>
/// <param name="Loader">The mod loader, or <see cref="LoaderKind.Vanilla"/>.</param>
/// <param name="LoaderVersion">The loader version, or <c>null</c> to take the recommended one.</param>
public sealed record InstanceCreationRequest(
    string Name,
    string MinecraftVersion,
    LoaderKind Loader = LoaderKind.Vanilla,
    string? LoaderVersion = null);

/// <summary>Owns the instance list and the folders behind it.</summary>
public interface IInstanceManager
{
    IReadOnlyList<MinecraftInstance> Instances { get; }

    /// <summary>Raised when an instance is added, removed or edited.</summary>
    event EventHandler? Changed;

    /// <summary>Reads every instance folder under the instances directory.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the paths for an instance.</summary>
    InstanceLayout Layout(MinecraftInstance instance);

    /// <summary>
    /// Creates the folder structure and record for a new instance.
    /// </summary>
    /// <remarks>
    /// This does not download Minecraft or install a loader. Those are separate
    /// steps so the instance exists, and is visible, while they run.
    /// </remarks>
    Task<MinecraftInstance> CreateAsync(InstanceCreationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Persists edits made to an instance record.</summary>
    Task SaveAsync(MinecraftInstance instance, CancellationToken cancellationToken = default);

    /// <summary>Changes the display name. The folder id is left alone.</summary>
    Task RenameAsync(MinecraftInstance instance, string newName, CancellationToken cancellationToken = default);

    /// <summary>Copies an instance, including its mods, configs and worlds.</summary>
    Task<MinecraftInstance> DuplicateAsync(MinecraftInstance instance, CancellationToken cancellationToken = default);

    /// <summary>Deletes the instance and everything in its folder. The caller confirms first.</summary>
    Task DeleteAsync(MinecraftInstance instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists what an export would carry, as a tree, so the user can turn parts
    /// of it off before the archive is written.
    /// </summary>
    /// <remarks>
    /// Files an export never carries at all, such as Mojang's assets and
    /// libraries, are already left out here. What comes back is the whole of
    /// what a full export would contain and nothing more.
    /// </remarks>
    Task<IReadOnlyList<InstanceContentNode>> ExportableContentsAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a <c>.vibinstance</c> archive containing the instance record, its
    /// mods list, configs and worlds.
    /// </summary>
    /// <param name="instance">The instance to export.</param>
    /// <param name="destinationFile">Where to write the archive. Overwritten if it exists.</param>
    /// <param name="options">What to leave out, or <c>null</c> to carry everything.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task ExportAsync(
        MinecraftInstance instance,
        string destinationFile,
        InstanceExportOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an archive into a new instance.
    /// </summary>
    /// <remarks>
    /// Three shapes are accepted, told apart by what is inside rather than by
    /// the file extension: a <c>.vibinstance</c> export, a Modrinth
    /// <c>.mrpack</c> modpack, and a plain zip with a Minecraft folder in it. A
    /// modpack's mods are fetched as part of the import, so this can take as
    /// long as any other download; a failure part-way leaves nothing behind.
    /// </remarks>
    /// <param name="archiveFile">The file to read.</param>
    /// <param name="progress">Receives a line of status text, for a progress dialog.</param>
    /// <param name="cancellationToken">Cancels the import and rolls it back.</param>
    /// <exception cref="Common.InvalidConfigurationException">
    /// The file is not one of the accepted shapes, or is malformed.
    /// </exception>
    Task<MinecraftInstance> ImportAsync(
        string archiveFile,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
