# Vib-launcher

A native Windows Minecraft launcher and vib-MC server control panel, built by Vib Studios.

Vib-launcher manages isolated Minecraft instances, Microsoft and offline accounts, mod loaders,
mods from Modrinth, and local [vib-MC](https://github.com/vib-studios/vib-MC) servers, in one
desktop application wearing the Vib Studios Classic theme.

It is a real WPF application. There is no browser, no WebView and no embedded web page anywhere in
it.

---

## Requirements

| | |
|---|---|
| OS | Windows 10 20H1 or newer, 64-bit |
| To run | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| To build | .NET 10 SDK |
| To play | A Java runtime. Which one depends on the Minecraft version: Java 8 for 1.16 and older, 17 for 1.18, 21 for 1.20.5 and newer. The launcher detects what is installed and says which is needed. |
| To run a server | Java 8 or newer, per vib-MC's own requirement |

Nothing from Minecraft is bundled. Client jars, libraries and assets are fetched from Mojang's own
distribution endpoints and verified against the hashes Mojang publishes with them.

## Running it

```
dotnet run --project src/VibLauncher.App
```

Or build once and run the executable:

```
dotnet build -c Release
src\VibLauncher.App\bin\Release\net10.0-windows\VibLauncher.exe
```

For a build that runs on a machine with no .NET installed:

```
dotnet publish src/VibLauncher.App -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Tests

```
dotnet test
```

208 tests covering instance and account persistence, import and export, mod compatibility and
enable/disable, download success, checksum failure, retry and cancellation, `server.properties`
round-tripping, port probing, version metadata parsing and merging, argument building, path safety
and credential redaction. They need no network: the download tests stand up a loopback HTTP server.

---

## Where things are kept

Everything lives under `%APPDATA%\VibLauncher`:

```
VibLauncher/
├── launcher-data/          settings.json, accounts.json, tokens.dat, cached metadata, logs
├── instances/<id>/         one folder per instance
│   ├── instance.json       the launcher's record
│   ├── mods.json           where each installed mod came from
│   ├── natives/            extracted per instance, so versions cannot clash
│   ├── logs/               captured stdout and stderr, one file per launch
│   └── .minecraft/         the game directory: mods, config, saves, screenshots
├── servers/<id>/           one folder per vib-MC server
│   ├── vib-launcher-server.json
│   ├── server.properties, world/, plugins/, ...
│   └── console-logs/       captured console output, one file per run
├── minecraft/              versions, libraries and assets, shared across instances
├── backups/<id>/           server world backups, deliberately outside the server folder
└── downloads/
```

A second instance on a version already installed costs almost nothing: the client jar, libraries and
several hundred megabytes of assets are shared. Only the game directory and the extracted natives are
per-instance.

No path is built by hand anywhere in the code. Everything goes through `ILauncherPaths`, so moving the
data root is a one-line change.

## Architecture

```
VibLauncher.App              WPF. Views, view models, theme. No launcher logic.
      │
      ├── VibLauncher.Core              The domain and its rules. No network, no UI, no Windows APIs.
      │     Instances, Accounts, Mods, ModLoaders, Servers, Downloads, Java, Minecraft,
      │     Configuration, Diagnostics, Common
      │
      └── VibLauncher.Infrastructure    Where Core's interfaces meet the outside world.
            Authentication, MinecraftServices, ModLoaders, Mods, VibMc, Downloads, Networking
```

Core defines the interfaces; Infrastructure implements them; the App composes them once in
`LauncherServices` and hands them to view models. Core references neither of the others, which is what
lets the rules be tested without a network or a window.

### Instance format

`instance.json`, readable and hand-editable:

```json
{
  "id": "fabric-survival",
  "name": "Fabric Survival",
  "minecraftVersion": "1.21.8",
  "loader": "fabric",
  "loaderVersion": "0.17.2",
  "launchVersionId": "fabric-loader-0.17.2-1.21.8",
  "javaPath": null,
  "minMemoryMb": 1024,
  "maxMemoryMb": 6144,
  "jvmArguments": "-XX:+UseG1GC"
}
```

`launchVersionId` is what actually gets launched. For Vanilla it equals `minecraftVersion`; installing a
loader replaces it with the loader's own profile id.

### Exporting and importing

An export is a `.vibinstance` zip holding that record plus the mods, config, resource packs, shader
packs and worlds. Assets, libraries, versions, logs and crash reports are left out: they are
redownloadable, and bundling Mojang's files into a shareable archive is not something the launcher does.

Export opens a picker first. It shows everything the archive would carry as a tree with a size against
each branch, and unticking a folder or a single file leaves it out. Nothing on disk is touched: the
instance is unchanged, only the archive is smaller. A folder with some of its files unticked shows a
half-tick, so what the archive holds is readable without opening every branch.

Import accepts three shapes, told apart by what is inside the file rather than by its extension:

| Shape | Recognised by | What happens |
|---|---|---|
| `.vibinstance` | `vibinstance.json` | The record and files are restored, and the mod loader it names is installed. |
| `.mrpack` | `modrinth.index.json` | Minecraft version and loader come from the pack's `dependencies`; `overrides/` and `client-overrides/` are unpacked; every file the index lists is downloaded from its published address and checked against its published SHA-1. Files marked `client: unsupported` are skipped. |
| `.zip` | a `.minecraft` folder, or a folder holding `mods`, `config`, `saves` or `options.txt` | The game folder is unpacked. An `instance.json` sitting beside it is honoured; without one the instance arrives with no version and the launcher asks for one rather than guessing. |

An archive re-zipped inside a wrapper folder is still read correctly: the format's root is found at
whatever depth it sits at. Every entry name is resolved under the destination before anything is
written, so an archive cannot place a file outside the instance it is being imported into, and the same
check applies to the paths in a modpack's index. An import that fails part way through, including a
modpack whose downloads do not complete, leaves nothing behind.

CurseForge packs are refused with an explanation rather than half-imported: the format names its mods
by project id, which needs an API key this launcher does not ship.

### Accounts

Offline accounts derive the same UUID a server computes for an unauthenticated player: a version 3
UUID over `OfflinePlayer:<name>`. That means a local profile keeps its player data across launches and
matches what a vib-MC server with `online-mode=false` hands out.

Microsoft accounts use the device-code flow: Microsoft identity, then Xbox Live, then XSTS, then
Minecraft services. **The launcher never sees a Microsoft password.** It shows a code, the user signs in
on Microsoft's own page, and the launcher polls until it is done.

`accounts.json` holds only names, UUIDs and expiry times. Tokens go to a separate store encrypted with
DPAPI under the current Windows user, and every log line passes through a redaction filter on the way
out, so a token cannot reach a log even inside an exception message.

Microsoft sign-in needs an Azure application (client) id. One is deliberately **not** compiled in: a
client id is public rather than secret, but it belongs to whoever ships a build. Set it in
Settings → Accounts, or in the `VIBLAUNCHER_MSA_CLIENT_ID` environment variable. Offline accounts work
without it, and the UI says so rather than offering a button that would fail.

### Mod loaders

`ILoaderProvider` resolved through `ILoaderRegistry`.

| Loader | Lists versions | Installs |
|---|---|---|
| Vanilla | n/a | n/a |
| Fabric | yes | **yes** |
| Quilt | yes | **yes** |
| NeoForge | yes | no |
| Forge | yes | no |

Fabric and Quilt install completely: their metadata services publish a ready-made version profile, which
the launcher writes into the versions folder and merges over the vanilla version at launch.

Forge and NeoForge are listed with their real published builds but cannot be installed yet, because both
install by running their own installer jar, which patches the client and writes a profile. Rather than
offer an install that would produce an instance that does not start, the Create Instance dialog shows
the real versions, explains the limitation and refuses to create the instance. Filling this in means
replacing `MavenLoaderProvider.InstallAsync` and nothing else.

**To add a loader:** implement `ILoaderProvider` and add it to the list in `LauncherServices`.

### Mods

`IModProvider`, with Modrinth implemented against its documented v2 API. No page is scraped and no key
is required.

Compatibility is a property of the query, not an afterthought: the search is filtered by the instance's
Minecraft version and loader before it leaves the launcher, so every result shown is installable. The
specific file is checked again at install time, and a mismatch is refused with both sides named:

```
"Sodium 0.5.8" cannot be installed into "Fabric Survival".

That release supports Minecraft 1.20.1 on fabric. This instance is Minecraft 1.21.8 on Fabric.
Pick a different release, or change the instance's version.
```

Required dependencies are resolved automatically, depth-limited against cycles, and anything that could
not be satisfied is reported rather than silently skipped.

Disabling a mod renames it to `.jar.disabled`, which every loader ignores. Nothing is deleted, so
re-enabling is exact.

**To add a mod provider:** implement `IModProvider` and add it to `ModProviders` in `LauncherServices`.
A provider that needs credentials reports `IsAvailable == false` with a reason until it has them, and the
UI hides it instead of failing a search.

### Servers

vib-MC servers are a separate concept from instances and never share a directory. An instance is a
client the launcher starts; a server is a Java process that listens on a port and owns its own worlds.

Server jars come from the vib-MC project's GitHub releases. The website at vib-studios.github.io is
where a person reads about the project; the launcher goes to the API for a jar and builds its own native
UI.

The server runs as a child process with its streams redirected, never inside the launcher, so a server
crash cannot take the launcher down. stdout and stderr feed the console view and a per-run log file;
typed commands go to stdin. A crash is reported with its exit code and a route to the log, and the
launcher never auto-restarts.

Ports are checked before a start, and a conflict offers the next free port. The port is never changed
automatically: a server that silently moves is a server nobody can connect to.

An update replaces only the jar. Worlds, player data, plugins and `server.properties` are never touched,
and a backup is taken first. Backups live outside the server folder, so deleting a server does not delete
them, and a restore archives the current worlds before overwriting them.

Editing `server.properties` preserves comments, key order and any key the launcher does not know about,
because that file is one a server owner is likely to have edited by hand. Settings that only take effect
on a rebind are labelled as needing a restart.

## Known limitations

Stated plainly rather than hidden behind a disabled button:

- **Forge and NeoForge cannot be installed.** Versions are listed; the install step is not implemented.
  Fabric and Quilt are complete.
- **Microsoft sign-in needs a client id supplied by whoever ships the build.** The full auth chain is
  implemented and works once one is configured.
- **No managed Java downloads.** The launcher finds installed runtimes and tells you which release a
  version needs, but does not fetch one for you.
- **CurseForge packs are not imported.** `.vibinstance`, `.mrpack` and plain zips are. A CurseForge
  pack needs the CurseForge API to resolve its mods, and the import says so instead of failing quietly.
- **CurseForge is not implemented.** The provider abstraction is there for it.
- **Server plugin management is not implemented.** Servers get a `plugins/` folder and a button to open it.
- **Player counts and server memory use are not shown**, because nothing available reports them reliably.
  The fields show what is actually known rather than invented numbers.
- Resource packs, shader packs, saves and screenshots are reachable through folder buttons rather than
  in-app managers.

## The Classic theme

The palette, type and shapes come from `vib-studios.github.io/classic`, whose tokens are authored in
oklch and converted to sRGB in `Themes/Classic.Palette.xaml` with the originals kept in comments:

| Token | oklch | sRGB | Role |
|---|---|---|---|
| `void-950` | `oklch(13% .02 40)` | `#0E0503` | app background |
| `void-900` | `oklch(17% .02 40)` | `#170C09` | card |
| `void-800` | `oklch(22% .02 40)` | `#231814` | raised surface |
| `line` | `oklch(24% .02 40)` | `#281C18` | hairline border |
| `ember-400` | `oklch(72% .17 40)` | `#FB794A` | accent |
| `ember-300` | `oklch(80% .16 45)` | `#FF9960` | hover |
| `ember-500` | `oklch(64% .19 40)` | `#E65719` | pressed |

Every WPF control is retemplated: the stock chrome is light-themed and rounded in a different idiom.
Icons are vector paths on a 16-unit grid, never an icon font and never emoji, so nothing depends on a
typeface being installed. The title bar is darkened through DWM so it matches the window.

One deliberate departure from the site: its buttons are pills, which read as a web control on the
desktop, so buttons here use the same 6px radius as the inputs beside them.

## Licence

Independently implemented. No Prism Launcher code, assets or branding. Minecraft files are obtained
through Mojang's own distribution mechanisms and none are redistributed.
