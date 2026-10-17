using System.IO;

namespace Cs2ServerControl.Install;

/// <summary>
/// The package's own part: the server-files overlay, the fun plugins compiled
/// from source, the AMX Mod X plugin list and the admin account.
/// </summary>
internal sealed class FunFilesInstaller {
    private readonly InstallPaths _paths;
    private readonly InstallLog _log;
    private readonly IInstallPrompts _prompts;

    public FunFilesInstaller(InstallPaths paths, InstallLog log, IInstallPrompts prompts) {
        _paths = paths;
        _log = log;
        _prompts = prompts;
    }

    public async Task InstallAsync(CancellationToken cancellation) {
        _log.Step("Fun configs and plugins");

        await Task.Run(() => FileTree.CopyContents(_paths.OverlayDir, _paths.GameDir), cancellation);

        await BuildFunPluginsAsync(cancellation);
        UpdateAmxPluginList();
        await SetAdminAccountAsync(cancellation);

        // mapchooser prefers maps.ini over the map cycle. Without it, each mode's
        // own mapcycle file decides which maps are up for the vote.
        if (File.Exists(_paths.AmxMapsFile)) {
            File.Move(_paths.AmxMapsFile, _paths.AmxMapsFile + ".disabled", overwrite: true);
        }
    }

    private async Task BuildFunPluginsAsync(CancellationToken cancellation) {
        if (!File.Exists(_paths.AmxCompilerExe)) {
            throw new InstallException("amxxpc.exe is missing, AMX Mod X is not installed correctly.");
        }

        IEnumerable<string> sources = Directory.EnumerateFiles(_paths.AmxScriptingDir, "fun_*.sma")
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sources) {
            string pluginName = Path.GetFileNameWithoutExtension(source);
            string outputFile = Path.Combine(_paths.AmxPluginsDir, pluginName + ".amxx");
            List<string> compilerOutput = [];

            // The compiler finds its include folder relative to the working directory.
            int exitCode = await HiddenProcess.RunAsync(_paths.AmxCompilerExe, $"{Path.GetFileName(source)} \"-o{outputFile}\"",
                _paths.AmxScriptingDir, line => {
                    // stdout and stderr lines arrive on different threads.
                    lock (compilerOutput) {
                        compilerOutput.Add(line);
                    }
                }, cancellation);

            if (exitCode != 0 || !File.Exists(outputFile)) {
                foreach (string line in compilerOutput) {
                    _log.Note(line);
                }
                throw new InstallException($"Plugin {Path.GetFileName(source)} failed to compile.");
            }
            _log.Note($"{pluginName}.amxx compiled");
        }
    }

    private void UpdateAmxPluginList() {
        List<string> lines = FileTree.ReadLines(_paths.AmxPluginListFile);
        FileTree.WriteLines(_paths.AmxPluginListFile, ConfigEdits.MergeAmxPluginList(lines, InstallComponents.FunPlugins));
    }

    private async Task SetAdminAccountAsync(CancellationToken cancellation) {
        if (ConfigEdits.HasPackageAdmin(FileTree.ReadLines(_paths.AmxUsersFile))) {
            _log.Note(@"Admin is already set up (addons\amxmodx\configs\users.ini).");
            return;
        }

        AdminAccount? account = await _prompts.AskAdminAccountAsync(cancellation);
        if (account == null || string.IsNullOrWhiteSpace(account.Name)) {
            _log.Note("No admin created.");
            return;
        }

        string? problem = ConfigEdits.AdminAccountProblem(account);
        if (problem != null) {
            _log.Problem($"{problem} No admin created, run the installer again to add one.");
            return;
        }

        List<string> lines = FileTree.ReadLines(_paths.AmxUsersFile);
        FileTree.WriteLines(_paths.AmxUsersFile, ConfigEdits.AddAdminAccount(lines, account));

        _log.Note($"Admin {account.Name} created. Before joining, type in the game console: setinfo _pw \"{account.Password}\"");
    }
}
