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

    private void LoadModeConfigs() {
        RegisterListener<Listeners.OnMapStart>(_ => {
            // One frame later, so Valve's own gamemode configs have run.
            Server.NextFrame(RunModeConfigs);
        });
    }

    private void RunModeConfigs() {
        string mode = ReadActiveMode();
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
