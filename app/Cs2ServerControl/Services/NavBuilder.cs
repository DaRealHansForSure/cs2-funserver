using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Cs2ServerControl.Services;

/// <summary>
/// Builds zBot navigation files for maps the game server is not running.
/// zBot can only analyze a loaded map, so a second, hidden hlds.exe on
/// 127.0.0.1:27016 loads the map with one bot (navbuild.cfg) and without
/// Metamod plugins (no live feed, no mode configs), waits until zBot wrote
/// maps\&lt;map&gt;.nav and is stopped again. The game in progress is not touched.
/// </summary>
public sealed class NavBuilder {
    public const int HelperPort = 27016;

    // Big maps take long: de_survivor (6.4 MB .bsp) needed about 10 minutes,
    // the old 6 minute limit killed it every time.
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StableFor = TimeSpan.FromSeconds(2);


    private readonly PackagePaths _paths;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public NavBuilder(PackagePaths paths) {
        _paths = paths;
    }

    public bool IsAvailable => File.Exists(HelperConfigFile) && File.Exists(HelperPluginsFile) && _paths.IsServerInstalled;

    private string HelperConfigFile => Path.Combine(_paths.GameDir, "navbuild.cfg");
    private string HelperPluginsFile => Path.Combine(_paths.GameDir, "addons", "metamod", "plugins_navbuild.ini");

    /// <summary>
    /// Builds the .nav of one map; "replace" deletes an existing one first,
    /// because zBot only analyzes maps without a mesh. True when a new file exists.
    /// </summary>
    public async Task<bool> BuildAsync(string mapName, bool replace, CancellationToken cancellation = default) {
        // The helper would stop at an invisible engine error box and only
        // the timeout would end it.
        if (MapResources.FindMissing(_paths.MapFile(mapName), _paths.GameDir).Count > 0) {
            return false;
        }

        await _oneAtATime.WaitAsync(cancellation);
        Process? helper = null;
        try {
            string navFile = _paths.NavFile(mapName);
            if (replace && File.Exists(navFile)) {
                File.Delete(navFile);
            }
            DateTime startedAt = DateTime.UtcNow;

            // On an invisible desktop: its console window must never take the focus.
            helper = HiddenDesktopLauncher.Start(_paths.Cs2Exe,
                string.Join(' ',
                    "-console -game cstrike",
                    $"-port {HelperPort} +ip 127.0.0.1 +maxplayers 2 +sv_lan 1",
                    "+servercfgfile navbuild.cfg",
                    "+localinfo mm_pluginsfile addons/metamod/plugins_navbuild.ini",
                    $"+map {mapName}"),
                _paths.ServerDir);

            return await WaitForNavFileAsync(navFile, startedAt, helper, cancellation);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception) {
            return false;
        } finally {
            if (helper != null) {
                TryKill(helper);
            }
            _oneAtATime.Release();
        }
    }

    private static async Task<bool> WaitForNavFileAsync(string navFile, DateTime startedAt, Process helper, CancellationToken cancellation) {
        var deadline = DateTime.UtcNow + BuildTimeout;
        long lastSize = -1;
        DateTime sizeSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline) {
            await Task.Delay(PollInterval, cancellation);
            if (helper.HasExited) {
                return File.Exists(navFile) && File.GetLastWriteTimeUtc(navFile) >= startedAt;
            }
            if (!File.Exists(navFile) || File.GetLastWriteTimeUtc(navFile) < startedAt) {
                continue;
            }

            // zBot writes the file in one go; a size that holds still means it is done.
            long size = new FileInfo(navFile).Length;
            if (size != lastSize) {
                lastSize = size;
                sizeSince = DateTime.UtcNow;
                continue;
            }
            if (DateTime.UtcNow - sizeSince >= StableFor) {
                return true;
            }
        }
        return false;
    }

    private static void TryKill(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        } catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) {
            // Already gone.
        }
        process.Dispose();
    }
}
