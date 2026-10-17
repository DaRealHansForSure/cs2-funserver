using System.IO;
using System.IO.Compression;

namespace Cs2ServerControl.Install;

/// <summary>
/// The engine and mod layers of the server: the CS2 dedicated server from
/// SteamCMD, Metamod:Source 2 and CounterStrikeSharp (with its .NET runtime).
/// </summary>
internal sealed class ServerComponentsInstaller {
    private readonly InstallPaths _paths;
    private readonly InstallLog _log;
    private readonly DownloadCache _downloads;
    private readonly ArchiveExtractor _extractor;

    public ServerComponentsInstaller(InstallPaths paths, InstallLog log, DownloadCache downloads, ArchiveExtractor extractor) {
        _paths = paths;
        _log = log;
        _downloads = downloads;
        _extractor = extractor;
    }

    public async Task InstallServerAsync(CancellationToken cancellation) {
        _log.Step("SteamCMD and the CS2 dedicated server (about 60 GB the first time)");

        if (!File.Exists(_paths.SteamCmdExe)) {
            string archive = await _downloads.GetAsync(InstallComponents.SteamCmdUrl, "steamcmd.zip", cancellation);
            Directory.CreateDirectory(_paths.SteamCmdDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(archive, _paths.SteamCmdDir, overwriteFiles: true), cancellation);
        }

        // The anonymous login is enough for the dedicated server. "validate"
        // repairs damaged files and resumes a download that stopped half way.
        string arguments = $"+force_install_dir \"{_paths.ServerDir}\" +login anonymous +app_update {InstallComponents.Cs2AppId} validate +quit";
        for (int attempt = 1; attempt <= InstallComponents.ServerInstallAttempts; attempt++) {
            _log.Note($"SteamCMD run {attempt} of {InstallComponents.ServerInstallAttempts} ...");
            await HiddenProcess.RunAsync(_paths.SteamCmdExe, arguments, _paths.SteamCmdDir, _log.Note, cancellation);

            if (File.Exists(_paths.Cs2Exe) && File.Exists(_paths.GameInfoFile)) {
                _log.Note("The CS2 server is installed.");
                return;
            }
        }

        throw new InstallException("The CS2 server could not be installed. Check the internet connection and the free disk space (about 60 GB), then run the installer again.");
    }

    public async Task InstallMetamodAsync(CancellationToken cancellation) {
        _log.Step("Metamod:Source (plugin loader)");

        string extracted = await DownloadAndExpandAsync(InstallComponents.MetamodUrl, "mmsource-windows.zip", cancellation);
        string? addonsFolder = FileTree.FindFirstFolder(extracted, "addons");
        if (addonsFolder == null) {
            throw new InstallException("No addons folder found in the Metamod:Source archive.");
        }
        await Task.Run(() => FileTree.CopyContents(addonsFolder, _paths.AddonsDir), cancellation);

        EnsureMetamodSearchPath(_paths, _log);
    }

    /// <summary>
    /// Writes the Metamod line into gameinfo.gi when it is missing. Every CS2
    /// update replaces that file, so the window also calls this before each
    /// server start; without the line the server starts without any plugins.
    /// </summary>
    public static bool EnsureMetamodSearchPath(InstallPaths paths, InstallLog? log) {
        if (!File.Exists(paths.GameInfoFile)) {
            return false;
        }
        List<string> lines = FileTree.ReadLines(paths.GameInfoFile);
        if (ConfigEdits.HasMetamodSearchPath(lines)) {
            return false;
        }
        FileTree.WriteLines(paths.GameInfoFile, ConfigEdits.AddMetamodSearchPath(lines));
        log?.Note("Metamod added to gameinfo.gi");
        return true;
    }

    public async Task InstallCounterStrikeSharpAsync(CancellationToken cancellation) {
        _log.Step("CounterStrikeSharp (C# plugins, with its own .NET runtime)");

        string extracted = await DownloadAndExpandAsync(InstallComponents.CounterStrikeSharpUrl, "counterstrikesharp-with-runtime-windows.zip", cancellation);
        string? addonsFolder = FileTree.FindFirstFolder(extracted, "addons");
        if (addonsFolder == null) {
            throw new InstallException("No addons folder found in the CounterStrikeSharp archive.");
        }
        await Task.Run(() => FileTree.CopyContents(addonsFolder, _paths.AddonsDir), cancellation);
    }

    private async Task<string> DownloadAndExpandAsync(string url, string fileName, CancellationToken cancellation) {
        string archive = await _downloads.GetAsync(url, fileName, cancellation);
        return await Task.Run(() => _extractor.Expand(archive), cancellation);
    }
}
