using System.IO;
using Cs2ServerControl.Services;

namespace Cs2ServerControl.Install;

/// <summary>
/// Installs a complete Counter-Strike 1.6 LAN fun server into the "server"
/// folder of the package: SteamCMD + HLDS, ReHLDS, ReGameDLL, Metamod-R,
/// AMX Mod X, the zBot bot profiles from ReGameDLL, the fun plugins and
/// configs from server-files, then the map pack.
/// Running it again is safe: downloads are cached in tools\downloads and
/// every step re-applies its changes, so it also works as a repair or update.
/// </summary>
public sealed class PackageInstaller {
    private const string NotInstalledMessage = "The server is not installed yet. Run Install or repair on the Install screen first.";

    private readonly InstallPaths _paths;
    private readonly InstallLog _log;
    private readonly ServerComponentsInstaller _components;
    private readonly FunFilesInstaller _funFiles;
    private readonly MapInstaller _maps;
    private readonly FirewallSetup _firewall;

    public PackageInstaller(PackagePaths paths, IProgress<InstallMessage> log, IInstallPrompts prompts) {
        _paths = new InstallPaths(paths);
        _log = new InstallLog(log);

        var downloads = new DownloadCache(_paths.DownloadDir, _log);
        var extractor = new ArchiveExtractor(_paths.WorkDir);
        _components = new ServerComponentsInstaller(_paths, _log, downloads, extractor);
        _funFiles = new FunFilesInstaller(_paths, _log, prompts);
        _maps = new MapInstaller(_paths, _log, downloads, extractor);
        _firewall = new FirewallSetup(_log);
    }

    /// <summary>The full install or repair.</summary>
    public async Task InstallServerAsync(CancellationToken cancellation) {
        Directory.CreateDirectory(_paths.ServerDir);
        Directory.CreateDirectory(_paths.WorkDir);
        try {
            await _components.InstallHldsAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            await _components.InstallReHldsAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            await _components.InstallReGameDllAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            await _components.InstallMetamodAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            await _components.InstallAmxModXAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            await _components.InstallZBotAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            _components.RegisterMetamodPlugins();
            await _funFiles.InstallAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            await InstallMapPartsAsync(cancellation);
            await _firewall.AddRulesAsync(cancellation);
        } finally {
            FileTree.DeleteFolderQuietly(_paths.WorkDir);
        }
    }

    /// <summary>
    /// Only the maps (map pack plus the custom-maps folder) and the map lists,
    /// also started with --update-maps.
    /// </summary>
    public async Task InstallMapsAsync(CancellationToken cancellation) {
        if (!_paths.Package.IsServerInstalled) {
            throw new InstallException(NotInstalledMessage);
        }

        Directory.CreateDirectory(_paths.WorkDir);
        try {
            await InstallMapPartsAsync(cancellation);
        } finally {
            FileTree.DeleteFolderQuietly(_paths.WorkDir);
        }
    }

    /// <summary>One map from the market, with its picture and the map lists.</summary>
    public async Task<IReadOnlyList<string>> InstallMarketMapAsync(MarketMap map, CancellationToken cancellation) {
        if (!_paths.Package.IsServerInstalled) {
            throw new InstallException(NotInstalledMessage);
        }

        Directory.CreateDirectory(_paths.WorkDir);
        try {
            return await _maps.InstallMarketMapAsync(map, cancellation);
        } finally {
            FileTree.DeleteFolderQuietly(_paths.WorkDir);
        }
    }

    public IReadOnlyList<string> RemoveMarketMap(int modId) {
        return _maps.RemoveMarketMap(modId);
    }

    /// <summary>
    /// Copies the server-files overlay, compiles the fun plugins, registers them
    /// and sets up the admin account, for a quick refresh after editing a
    /// config or plugin source.
    /// </summary>
    public async Task InstallFunFilesAsync(CancellationToken cancellation) {
        if (!_paths.Package.IsServerInstalled) {
            throw new InstallException(NotInstalledMessage);
        }

        await _funFiles.InstallAsync(cancellation);
    }

    private async Task InstallMapPartsAsync(CancellationToken cancellation) {
        await _maps.InstallQuakeSoundsAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        await _maps.InstallMapPackAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        await _maps.InstallMapPreviewsAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        await _maps.InstallOwnMapsAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        await Task.Run(_maps.UpdateMapCycles, cancellation);
    }
}
