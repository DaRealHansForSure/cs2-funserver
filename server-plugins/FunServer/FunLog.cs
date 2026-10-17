using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace FunServer;

/// <summary>
/// The evening's log for the window's Stats screen: map changes with their
/// mode, kills and suicides, one file per day in CounterStrikeSharp's logs
/// folder (fun-yyyyMMdd.log). The lines follow the HLDS log format the CS 1.6
/// window already reads. CS2's own game log only goes to console.log, which
/// every server start empties.
/// </summary>
public sealed partial class FunServerPlugin {
    private static readonly object LogLock = new();

    private void LoadFunLog() {
        RegisterListener<Listeners.OnMapStart>(map => {
            WriteFunLog($"-------- Mapchange to {map} --------");
            WriteFunLog($"Active mode: {_activeMode}");
        });
        RegisterEventHandler<EventPlayerDeath>((death, _) => {
            LogDeath(death);
            return HookResult.Continue;
        });
    }

    private static void LogDeath(EventPlayerDeath death) {
        CCSPlayerController? victim = death.Userid;
        if (victim == null || !victim.IsValid) {
            return;
        }
        CCSPlayerController? killer = death.Attacker;
        bool isSuicide = killer == null || !killer.IsValid || killer == victim;
        if (isSuicide) {
            WriteFunLog($"{LogName(victim)} committed suicide with \"{death.Weapon}\"");
            return;
        }
        string headshot = death.Headshot ? " (headshot)" : "";
        WriteFunLog($"{LogName(killer!)} killed {LogName(victim)} with \"{death.Weapon}\"{headshot}");
    }

    /// <summary>"Name&lt;userid&gt;&lt;auth&gt;&lt;TEAM&gt;": auth is BOT or the 64 bit SteamID, like the HLDS log.</summary>
    private static string LogName(CCSPlayerController player) {
        string name = player.PlayerName.Replace("\"", "'");
        string auth = player.IsBot ? "BOT" : player.SteamID.ToString(CultureInfo.InvariantCulture);
        string team = player.TeamNum switch {
            TeamTerrorist => "TERRORIST",
            TeamCounterTerrorist => "CT",
            _ => "SPECTATOR",
        };
        return $"\"{name}<{player.UserId ?? 0}><{auth}><{team}>\"";
    }

    private static void WriteFunLog(string text) {
        DateTime now = DateTime.Now;
        string folder = Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "logs");
        string line = string.Create(CultureInfo.InvariantCulture, $"L {now:MM/dd/yyyy - HH:mm:ss}: {text}{Environment.NewLine}");
        try {
            lock (LogLock) {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, $"fun-{now:yyyyMMdd}.log"), line);
            }
        } catch (IOException) {
            // A full disk only costs the statistics, never the game.
        }
    }
}
