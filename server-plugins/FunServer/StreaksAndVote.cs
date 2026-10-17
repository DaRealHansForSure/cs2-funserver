using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace FunServer;

/// <summary>
/// Kill streaks and the mode vote.
///   Streaks: every 5 kills without dying a short gold glow and +50 HP (at
///   most 255); every 10 kills on top of that a rocket jump and a message to
///   everybody. The glow puts back whatever the player looked like before.
///   Mode vote: "!mode" in chat opens a vote for everybody; after 20 seconds
///   the mode with the most votes runs on a random map of its map list. The
///   window switches directly with "fun_mode &lt;id&gt;" over RCON.
/// </summary>
public sealed partial class FunServerPlugin {
    private const int StreakStep = 5;
    private const int StreakBigStep = 10;
    private const int StreakHealthBonus = 50;
    private const int StreakMaxHealth = 255;
    private const float StreakGlowSeconds = 3f;
    private const float StreakRocketSpeed = 800f;
    private const float VoteSeconds = 20f;

    private static readonly Color StreakGold = Color.FromArgb(255, 255, 200, 0);

    private sealed record ModeChoice(string Id, string Title, int GameType, int GameMode);

    // Same ids and Valve game types as the window's mode tiles.
    private static readonly ModeChoice[] Modes = [
        new("classic", "Classic Fun", 0, 0),
        new("gungame", "Arms Race", 1, 0),
        new("zombie", "Zombie", 0, 0),
        new("deathmatch", "Deathmatch", 1, 2),
        new("deathrun", "Deathrun", 0, 0),
    ];

    private readonly Dictionary<int, int> _streaks = [];
    private readonly Dictionary<int, int> _votes = [];
    private bool _isVoteRunning;

    private void LoadStreaksAndVote() {
        RegisterEventHandler<EventPlayerDeath>(OnStreakDeath);
        RegisterListener<Listeners.OnMapStart>(_ => {
            _streaks.Clear();
            _votes.Clear();
            _isVoteRunning = false;
        });
    }

    private HookResult OnStreakDeath(EventPlayerDeath death, GameEventInfo info) {
        if (death.Userid != null) {
            _streaks.Remove(death.Userid.Slot);
        }
        CCSPlayerController? killer = death.Attacker;
        bool isRealKill = killer != null && killer.IsValid && killer != death.Userid;
        if (!StreaksEnabled.Value || !isRealKill || !killer!.PawnIsAlive) {
            return HookResult.Continue;
        }

        int streak = _streaks.GetValueOrDefault(killer.Slot) + 1;
        _streaks[killer.Slot] = streak;
        if (streak % StreakStep != 0) {
            return HookResult.Continue;
        }

        CCSPlayerPawn pawn = killer.PlayerPawn.Value!;
        SetHealth(pawn, Math.Min(StreakMaxHealth, pawn.Health + StreakHealthBonus));
        GlowFor(killer, StreakGold, StreakGlowSeconds);
        if (streak % StreakBigStep == 0) {
            Push(pawn, new Vector(pawn.AbsVelocity.X, pawn.AbsVelocity.Y, StreakRocketSpeed));
            ChatToAll($"{ChatColors.Gold}{killer.PlayerName}{ChatColors.Default} is unstoppable: {streak} kills in a row!");
        } else {
            Chat(killer, $"{streak} kills in a row: +{StreakHealthBonus} HP.");
        }
        return HookResult.Continue;
    }

    /// <summary>A short glow that puts the old one back: an admin glow or the zombie colors stay.</summary>
    private void GlowFor(CCSPlayerController player, Color color, float seconds) {
        Color? before = GlowColorOf(player);
        SetGlow(player, color);
        AddTimer(seconds, () => {
            // Only when nothing else changed the glow in between, and the player still lives.
            if (!player.IsValid || GlowColorOf(player) != color) {
                return;
            }
            if (before == null) {
                RemoveGlow(player);
            } else {
                SetGlow(player, before.Value);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    [ConsoleCommand("css_mode", "Starts a vote for the next fun mode")]
    public void OnModeVote(CCSPlayerController? caller, CommandInfo command) {
        if (_isVoteRunning) {
            command.ReplyToCommand("A mode vote is already running.");
            return;
        }
        _isVoteRunning = true;
        _votes.Clear();

        var menu = new ChatMenu("Vote for the next mode");
        foreach (ModeChoice mode in Modes) {
            ModeChoice choice = mode;
            menu.AddMenuOption(choice.Title, (voter, _) => {
                if (!_isVoteRunning) {
                    return;
                }
                _votes[voter.Slot] = Array.IndexOf(Modes, choice);
                Chat(voter, $"You voted for {choice.Title}.");
            });
        }
        foreach (CCSPlayerController player in Utilities.GetPlayers().Where(player => player.IsValid && !player.IsBot)) {
            MenuManager.OpenChatMenu(player, menu);
        }
        ChatToAll($"Mode vote: type the number in chat. {VoteSeconds:0} seconds.");
        AddTimer(VoteSeconds, FinishModeVote, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void FinishModeVote() {
        _isVoteRunning = false;
        if (_votes.Count == 0) {
            ChatToAll("Nobody voted, the mode stays.");
            return;
        }
        // The most votes win; a tie goes to the earlier mode in the list.
        int winnerIndex = _votes.Values.GroupBy(index => index)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First().Key;
        ModeChoice winner = Modes[winnerIndex];
        if (winner.Id == _activeMode) {
            ChatToAll($"{winner.Title} won, the mode stays.");
            return;
        }
        ChatToAll($"{winner.Title} won. Switching in 5 seconds.");
        AddTimer(5f, () => SwitchMode(winner), TimerFlags.STOP_ON_MAPCHANGE);
    }

    [ConsoleCommand("fun_mode", "fun_mode <id> - switches to a fun mode on a random map of its list")]
    [CommandHelper(minArgs: 1, usage: "<classic|gungame|zombie|deathmatch|deathrun>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnSwitchMode(CCSPlayerController? caller, CommandInfo command) {
        ModeChoice? mode = Modes.FirstOrDefault(candidate => candidate.Id == command.GetArg(1).ToLowerInvariant());
        if (mode == null) {
            command.ReplyToCommand($"Unknown mode {command.GetArg(1)}.");
            return;
        }
        SwitchMode(mode);
    }

    /// <summary>
    /// Writes the mode where the next map start reads it (the same file the
    /// window writes), sets Valve's game type and loads a map of the mode.
    /// </summary>
    private void SwitchMode(ModeChoice mode) {
        string? map = RandomMapOf(mode.Id);
        if (map == null) {
            ChatToAll($"{mode.Title} has no installed map in its map list.");
            return;
        }
        File.WriteAllText(Path.Combine(ConfigDirectory, "fun", "active_mode.txt"), mode.Id);
        Server.ExecuteCommand($"game_type {mode.GameType}; game_mode {mode.GameMode}");
        Server.ExecuteCommand($"changelevel {map}");
    }

    private static string? RandomMapOf(string modeId) {
        List<string> maps = InstalledMapsOf(modeId);
        if (maps.Count == 0) {
            return null;
        }
        // Not the map that runs now, when there is a choice.
        List<string> others = maps.Where(map => !map.Equals(Server.MapName, StringComparison.OrdinalIgnoreCase)).ToList();
        List<string> pool = others.Count > 0 ? others : maps;
        return pool[Random.Shared.Next(pool.Count)];
    }
}
