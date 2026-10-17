using System.IO;
using System.IO.Compression;

namespace Cs2ServerControl.Install;

/// <summary>
/// The engine and mod layers of the server: HLDS from SteamCMD, ReHLDS,
/// ReGameDLL, Metamod-R, AMX Mod X and the zBot data files.
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

    public async Task InstallHldsAsync(CancellationToken cancellation) {
        _log.Step("SteamCMD and HLDS (Counter-Strike Dedicated Server)");

        if (!File.Exists(_paths.SteamCmdExe)) {
            string archive = await _downloads.GetAsync(InstallComponents.SteamCmdUrl, "steamcmd.zip", cancellation);
            Directory.CreateDirectory(_paths.SteamCmdDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(archive, _paths.SteamCmdDir, overwriteFiles: true), cancellation);
        }

        // ReHLDS only fits the pre-anniversary engine. The current default
        // branch ships a newer steam_api.dll whose exports the ReHLDS swds.dll
        // can not find, so the server must come from the steam_legacy branch.
        // Players with the normal, current CS 1.6 can still join it.
        //
        // App 90 is known for stopping half way on the first run, so retry until
        // the files we need are really there.
        string arguments = $"+force_install_dir \"{_paths.ServerDir}\" +login anonymous +app_set_config 90 mod cstrike +app_update 90 -beta steam_legacy validate +quit";
        for (int attempt = 1; attempt <= InstallComponents.HldsInstallAttempts; attempt++) {
            _log.Note($"SteamCMD run {attempt} of {InstallComponents.HldsInstallAttempts} (takes a few minutes the first time) ...");
            await HiddenProcess.RunAsync(_paths.SteamCmdExe, arguments, _paths.SteamCmdDir, _log.Note, cancellation);

            if (File.Exists(_paths.Cs2Exe) && File.Exists(_paths.LibListFile)) {
                _log.Note("HLDS is installed.");
                CopySteamClientFiles();
                return;
            }
        }

        throw new InstallException("HLDS could not be installed. Check the internet connection and run the installer again.");
    }

    /// <summary>
    /// Replaces the Steam client libraries of the server with the ones SteamCMD
    /// just updated itself to. The legacy HLDS build ships an old
    /// steamclient.dll that the Steam network no longer accepts, which ends in
    /// "Unable to initialize Steam". Leftovers from the current branch cause the
    /// same error. SteamCMD always carries a current 32-bit Steam client, so its
    /// copies are the right ones for hlds.exe.
    /// </summary>
    private void CopySteamClientFiles() {
        foreach (string fileName in InstallComponents.SteamClientFiles) {
            string sourceFile = Path.Combine(_paths.SteamCmdDir, fileName);
            if (!File.Exists(sourceFile)) {
                continue;
            }
            File.Copy(sourceFile, Path.Combine(_paths.ServerDir, fileName), overwrite: true);
            _log.Note($"{fileName} copied from SteamCMD");
        }
    }

    public async Task InstallReHldsAsync(CancellationToken cancellation) {
        _log.Step("ReHLDS (improved server engine)");

        string extracted = await DownloadAndExpandAsync(InstallComponents.ReHldsUrl, "rehlds.zip", cancellation);

        // The archive holds Linux and Windows builds, swds.dll marks the Windows one.
        string? engineDll = FileTree.FindFirstFile(extracted, "swds.dll");
        if (engineDll == null) {
            throw new InstallException("swds.dll not found in the ReHLDS archive.");
        }

        await Task.Run(() => FileTree.CopyContents(Path.GetDirectoryName(engineDll)!, _paths.ServerDir), cancellation);
    }

    public async Task InstallReGameDllAsync(CancellationToken cancellation) {
        _log.Step("ReGameDLL (game logic with respawn, free-for-all and more)");

        string extracted = await DownloadAndExpandAsync(InstallComponents.ReGameDllUrl, "regamedll.zip", cancellation);

        string? gameDll = FileTree.FindFirstFile(extracted, "mp.dll");
        if (gameDll == null) {
            throw new InstallException("mp.dll not found in the ReGameDLL archive.");
        }

        // mp.dll sits in <root>\cstrike\dlls, and <root> mirrors the server folder.
        string packageRootDir = Directory.GetParent(Path.GetDirectoryName(gameDll)!)!.Parent!.FullName;
        await Task.Run(() => FileTree.CopyContents(packageRootDir, _paths.ServerDir), cancellation);
    }

    public async Task InstallMetamodAsync(CancellationToken cancellation) {
        _log.Step("Metamod-R (plugin loader)");

        string extracted = await DownloadAndExpandAsync(InstallComponents.MetamodUrl, "metamod.zip", cancellation);

        string? metamodDll = FileTree.FindFirstFile(extracted, "metamod.dll");
        if (metamodDll == null) {
            throw new InstallException("metamod.dll not found in the Metamod-R archive.");
        }

        Directory.CreateDirectory(_paths.MetamodDir);
        File.Copy(metamodDll, Path.Combine(_paths.MetamodDir, "metamod.dll"), overwrite: true);

        SetGameDllToMetamod();
    }

    private void SetGameDllToMetamod() {
        string backupFile = _paths.LibListFile + ".original";
        if (!File.Exists(backupFile)) {
            File.Copy(_paths.LibListFile, backupFile);
        }

        List<string> lines = FileTree.ReadLines(_paths.LibListFile);
        FileTree.WriteLines(_paths.LibListFile, ConfigEdits.PointGameDllToMetamod(lines));
    }

    public async Task InstallAmxModXAsync(CancellationToken cancellation) {
        _log.Step("AMX Mod X (admin mod)");

        // The archives ship an empty users.ini. On a repair run that would wipe
        // the admin account, so keep the current one and put it back afterwards.
        byte[]? savedUsers = null;
        if (File.Exists(_paths.AmxUsersFile)) {
            savedUsers = await File.ReadAllBytesAsync(_paths.AmxUsersFile, cancellation);
        }

        await InstallAddonsArchiveAsync(InstallComponents.AmxBaseUrl, "amxmodx-base.zip", "AMX Mod X", cancellation);
        await InstallAddonsArchiveAsync(InstallComponents.AmxCstrikeUrl, "amxmodx-cstrike.zip", "AMX Mod X Counter-Strike", cancellation);

        if (savedUsers != null && savedUsers.Length > 0) {
            await File.WriteAllBytesAsync(_paths.AmxUsersFile, savedUsers, cancellation);
        }
    }

    private async Task InstallAddonsArchiveAsync(string url, string fileName, string title, CancellationToken cancellation) {
        string extracted = await DownloadAndExpandAsync(url, fileName, cancellation);

        string? addonsFolder = FileTree.FindFirstFolder(extracted, "addons");
        if (addonsFolder == null) {
            throw new InstallException($"No addons folder found in the {title} archive.");
        }

        await Task.Run(() => FileTree.CopyContents(addonsFolder, _paths.AddonsDir), cancellation);
    }

    public async Task InstallZBotAsync(CancellationToken cancellation) {
        _log.Step("zBot (bots built into ReGameDLL)");

        // The archive holds a cstrike folder with BotProfile.db, BotChatter.db
        // and the radio voice sounds. zBot builds a missing maps\<map>.nav on its
        // own when the first bot joins such a map, so no navigation pack is needed.
        string extracted = await DownloadAndExpandAsync(InstallComponents.ZBotProfilesUrl, "zbot-profiles.zip", cancellation);

        string? profileFile = FileTree.FindFirstFile(extracted, "BotProfile.db");
        if (profileFile == null) {
            throw new InstallException("BotProfile.db not found in the zBot profile archive.");
        }
        await Task.Run(() => FileTree.CopyContents(Path.GetDirectoryName(profileFile)!, _paths.GameDir), cancellation);

        // Has to run after the ReGameDLL step, see ConfigEdits.EnableZBot.
        List<string> gameInitLines = FileTree.ReadLines(_paths.GameInitFile);
        FileTree.WriteLines(_paths.GameInitFile, ConfigEdits.EnableZBot(gameInitLines));
    }

    public void RegisterMetamodPlugins() {
        _log.Step("Registering Metamod plugins");

        // Rewritten on every run, so a repair also drops the YaPB line of older
        // package versions. Their addons\yapb folder stays but is never loaded.
        string? amxModule = FileTree.FindFirstFile(_paths.AmxDir, "amxmodx_mm.dll");
        if (amxModule == null) {
            throw new InstallException("amxmodx_mm.dll is missing, AMX Mod X is not installed correctly.");
        }

        string pluginLine = ConfigEdits.MetamodPluginLine(_paths.GameDir, amxModule);
        _log.Note(pluginLine["win32 ".Length..]);
        FileTree.WriteLines(_paths.MetamodPluginsFile, [pluginLine]);
    }

    private async Task<string> DownloadAndExpandAsync(string url, string fileName, CancellationToken cancellation) {
        string archive = await _downloads.GetAsync(url, fileName, cancellation);
        return await Task.Run(() => _extractor.Expand(archive), cancellation);
    }
}
