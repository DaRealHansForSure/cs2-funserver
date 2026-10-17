using System.IO;

namespace Cs2ServerControl.Install;

/// <summary>
/// The package's own part: the server-files overlay (configs in cfg\fun) and
/// our CounterStrikeSharp plugins, which ship built in plugins\ next to the
/// exe. Nothing is compiled on the server PC.
/// </summary>
internal sealed class FunFilesInstaller {
    private readonly InstallPaths _paths;
    private readonly InstallLog _log;

    public FunFilesInstaller(InstallPaths paths, InstallLog log) {
        _paths = paths;
        _log = log;
    }

    public async Task InstallAsync(CancellationToken cancellation) {
        _log.Step("Fun configs and plugins");

        if (Directory.Exists(_paths.OverlayDir)) {
            await Task.Run(() => FileTree.CopyContents(_paths.OverlayDir, _paths.GameDir), cancellation);
            _log.Note(@"Configs copied to game\csgo\cfg\fun");
        }

        if (!Directory.Exists(_paths.CounterStrikeSharpDir)) {
            throw new InstallException("CounterStrikeSharp is not installed yet. Run Install or repair first.");
        }
        foreach (string plugin in InstallComponents.FunPlugins) {
            string source = Path.Combine(_paths.PluginsSourceDir, plugin);
            if (!Directory.Exists(source)) {
                _log.Problem($@"plugins\{plugin} is missing in the package, the plugin is not installed.");
                continue;
            }
            string target = Path.Combine(_paths.CounterStrikeSharpPluginsDir, plugin);
            await Task.Run(() => FileTree.CopyContents(source, target), cancellation);
            _log.Note($"{plugin} installed");
        }

        // A CS2 update replaces gameinfo.gi; a repair run should also bring Metamod back.
        ServerComponentsInstaller.EnsureMetamodSearchPath(_paths, _log);
    }
}
