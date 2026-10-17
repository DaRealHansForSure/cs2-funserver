using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace FunServer;

/// <summary>
/// The fun modes on every map. The window writes the active mode into
/// cfg\fun\active_mode.txt (CS2 has no localinfo) and sets game_type and
/// game_mode; this part runs the package's configs in a fixed order:
///   fun/modes/base.cfg     package defaults, resets what any mode changed
///   fun/local_bots.cfg     the host's bot settings (only what differs)
///   fun/modes/&lt;mode&gt;.cfg  what makes the mode special
///   fun/local.cfg          the host's settings from the window
///   fun/local_&lt;mode&gt;.cfg  the host's game rules for this mode
/// Valve's gamemode configs run before map start, so ours win.
/// </summary>
public sealed partial class FunServerPlugin {
    private const string DefaultMode = "classic";

    /// <summary>The mode of the running map, read once at map start; the fun parts ask this.</summary>
    private string _activeMode = DefaultMode;

    private void LoadModeConfigs() {
        RegisterListener<Listeners.OnMapStart>(_ => {
            _activeMode = ReadActiveMode();
            // One frame later, so Valve's own gamemode configs have run.
            Server.NextFrame(() => {
                RunModeConfigs();
                SetNextMapFromCycle();
            });
        });
        _activeMode = ReadActiveMode();
    }

    /// <summary>
    /// CS2 knows nothing of our map lists (cfg\fun\mapcycle_&lt;mode&gt;.txt):
    /// at the end of a match it loads nextlevel. So every map start points
    /// nextlevel at the map after this one in the mode's list. The window's
    /// "Next" button overrides it with nextlevel as well.
    /// </summary>
    private void SetNextMapFromCycle() {
        List<string> maps = InstalledMapsOf(_activeMode);
        if (maps.Count == 0) {
            return;
        }
        int current = maps.FindIndex(map => map.Equals(Server.MapName, StringComparison.OrdinalIgnoreCase));
        // A map that is not in the list (a Workshop map, a pick) goes on with the first map of the list.
        string next = maps[(current + 1) % maps.Count];
        Server.ExecuteCommand($"nextlevel {next}");
    }

    /// <summary>The maps of a mode's list that are installed, in the list's order.</summary>
    private static List<string> InstalledMapsOf(string modeId) {
        string cycleFile = Path.Combine(ConfigDirectory, "fun", $"mapcycle_{modeId}.txt");
        if (!File.Exists(cycleFile)) {
            return [];
        }
        string mapsDir = Path.Combine(Server.GameDirectory, "csgo", "maps");
        return File.ReadAllLines(cycleFile)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
            .Where(map => File.Exists(Path.Combine(mapsDir, map + ".vpk")))
            .ToList();
    }

    /// <summary>Also after a fun round: running the configs again puts every rule back.</summary>
    private void RunModeConfigs() {
        string mode = _activeMode;
        foreach (string config in ConfigsFor(mode)) {
            if (File.Exists(Path.Combine(ConfigDirectory, config))) {
                Server.ExecuteCommand($"exec {config}");
            }
        }
        // The server console ends up in console.log, where the window reads
        // the active mode; Console.WriteLine only reaches stdout.
        Server.PrintToConsole($"[FunServer] Active mode: {mode}");
    }

    internal static IReadOnlyList<string> ConfigsFor(string mode) {
        return [
            "fun/modes/base.cfg",
            "fun/local_bots.cfg",
            $"fun/modes/{mode}.cfg",
            "fun/local.cfg",
            $"fun/local_{mode}.cfg",
        ];
    }

    /// <summary>The mode the window wrote, or classic when there is none; only letters, it ends up in exec.</summary>
    private static string ReadActiveMode() {
        try {
            string file = Path.Combine(ConfigDirectory, "fun", "active_mode.txt");
            if (!File.Exists(file)) {
                return DefaultMode;
            }
            string mode = File.ReadAllText(file).Trim().ToLowerInvariant();
            return mode.Length > 0 && mode.All(char.IsAsciiLetterLower) ? mode : DefaultMode;
        } catch (IOException) {
            return DefaultMode;
        }
    }

    private static string ConfigDirectory => Path.Combine(Server.GameDirectory, "csgo", "cfg");
}
