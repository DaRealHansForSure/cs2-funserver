using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace FunServer;

/// <summary>
/// Tells the players where they are and what to do: a welcome with the mode
/// and its rules when somebody joins, a big center line for every role
/// (zombie, human, trapper, runner, fun round), a rotating tip in chat every
/// few minutes and !help to read it all again. Nobody at a LAN reads a
/// readme, so everything a player needs to know is said in the game.
/// </summary>
public sealed partial class FunServerPlugin {
    private const float WelcomeDelaySeconds = 4f;
    private const float TipIntervalSeconds = 150f;
    private const int CenterSeconds = 5;

    private sealed record ModeText(string Title, string Goal, string HowTo);

    private static readonly Dictionary<string, ModeText> ModeTexts = new() {
        ["classic"] = new("Classic Fun", "Casual rules, bunny hopping and a fun round every few rounds.",
            "Fun rounds: knives only, AWP only, low gravity, Juggernaut and more."),
        ["gungame"] = new("Arms Race", "Every kill gives you the next weapon.",
            "The first one through all weapons with a knife kill wins the map."),
        ["zombie"] = new("Zombie", "Everybody starts human, after 10 seconds the first zombies turn.",
            "Humans: shoot and run, a zombie hit infects you. Zombies: knife the humans."),
        ["deathmatch"] = new("Deathmatch", "Instant respawn, the most kills win.",
            "Free weapons, no waiting: just play."),
        ["deathrun"] = new("Deathrun", "One trapper against everybody, knives only.",
            "Runners: reach the end alive. Trapper: press the trap buttons (E) at the right moment."),
    };

    private int _tipIndex;

    private void LoadAnnouncements() {
        RegisterEventHandler<EventPlayerConnectFull>(OnWelcomePlayer);
        RegisterEventHandler<EventRoundStart>(OnAnnounceRound);
        AddTimer(TipIntervalSeconds, ShowNextTip, TimerFlags.REPEAT);
    }

    private ModeText CurrentModeText() {
        if (ModeTexts.TryGetValue(_activeMode, out ModeText? text)) {
            return text;
        }
        return ModeTexts["classic"];
    }

    private HookResult OnWelcomePlayer(EventPlayerConnectFull connect, GameEventInfo info) {
        CCSPlayerController? player = connect.Userid;
        if (player == null || player.IsBot || player.IsHLTV) {
            return HookResult.Continue;
        }
        // Right after the connect the player still looks at the loading screen and the team menu.
        AddTimer(WelcomeDelaySeconds, () => {
            if (!player.IsValid) {
                return;
            }
            ModeText mode = CurrentModeText();
            string hostname = ConVar.Find("hostname")?.StringValue ?? "LAN Fun Server";
            player.PrintToCenterHtml($"<font color='#FFC800'>{hostname}</font><br>Mode: <font color='#FF3F3F'>{mode.Title}</font>", CenterSeconds);
            Chat(player, $"Welcome to {ChatColors.Gold}{hostname}{ChatColors.Default}, {player.PlayerName}! This is a fun server.");
            ShowModeRules(player);
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    /// <summary>The mode, its rules, the controls that work right now, and the commands.</summary>
    private void ShowModeRules(CCSPlayerController player) {
        ModeText mode = CurrentModeText();
        Chat(player, $"Mode: {ChatColors.Red}{mode.Title}{ChatColors.Default} - {mode.Goal}");
        Chat(player, mode.HowTo);
        string? controls = ControlsText();
        if (controls != null) {
            Chat(player, controls);
        }
        Chat(player, $"Type {ChatColors.Green}!mode{ChatColors.Default} to vote for another mode, {ChatColors.Green}!help{ChatColors.Default} to read this again.");
    }

    /// <summary>Only the toys this mode really has; null when it has none.</summary>
    private string? ControlsText() {
        List<string> parts = [];
        if (HookEnabled.Value && HookModes.Contains(_activeMode)) {
            parts.Add($"Hook: hold {ChatColors.Green}F{ChatColors.Default} at a wall");
        }
        if (ParachuteEnabled.Value) {
            parts.Add($"Parachute: hold {ChatColors.Green}E{ChatColors.Default} while falling");
        }
        if (parts.Count == 0) {
            return null;
        }
        return string.Join(". ", parts) + ".";
    }

    [ConsoleCommand("css_help", "Shows the mode, its rules and the controls")]
    public void OnHelpCommand(CCSPlayerController? player, CommandInfo command) {
        if (player == null || !player.IsValid) {
            return;
        }
        ShowModeRules(player);
    }

    /// <summary>The roles that exist from the round start; zombies and humans get theirs when the infection starts.</summary>
    private HookResult OnAnnounceRound(EventRoundStart roundStart, GameEventInfo info) {
        // A frame later the deathrun teams and the fun round are settled.
        Server.NextFrame(() => {
            if (IsDeathrunMode) {
                AnnounceDeathrunRoles();
            }
        });
        return HookResult.Continue;
    }

    private void AnnounceDeathrunRoles() {
        foreach (CCSPlayerController player in PlayingPlayers().Where(player => !player.IsBot)) {
            if (player.Slot == _trapperSlot) {
                CenterTo(player, "#FF3F3F", "You are the TRAPPER", "Press the trap buttons (E) when the runners pass.");
            } else {
                CenterTo(player, "#99CCFF", "RUN!", "Reach the end of the course, watch out for traps.");
            }
        }
    }

    /// <summary>A big two-line message in the middle of the screen of one human player.</summary>
    private static void CenterTo(CCSPlayerController player, string color, string title, string line) {
        if (player.IsBot || !player.IsValid) {
            return;
        }
        player.PrintToCenterHtml($"<font color='{color}'>{title}</font><br>{line}", CenterSeconds);
    }

    private static void CenterToAll(string color, string title, string line) {
        foreach (CCSPlayerController player in PlayingPlayers().Where(player => !player.IsBot)) {
            CenterTo(player, color, title, line);
        }
    }

    /// <summary>One line every few minutes, in turn: the mode, the controls, the vote, the streaks.</summary>
    private void ShowNextTip() {
        if (!Utilities.GetPlayers().Any(player => player.IsValid && !player.IsBot && !player.IsHLTV)) {
            return;
        }
        List<string> tips = [
            $"This is {ChatColors.Red}{CurrentModeText().Title}{ChatColors.Default}: {CurrentModeText().Goal}",
            $"Bored of the mode? Type {ChatColors.Green}!mode{ChatColors.Default} and everybody votes for the next one.",
        ];
        if (StreaksEnabled.Value) {
            tips.Add($"Every {StreakStep} kills in a row: a gold glow and +{StreakHealthBonus} HP. At {StreakBigStep} you fly.");
        }
        string? controls = ControlsText();
        if (controls != null) {
            tips.Add(controls);
        }
        _tipIndex = (_tipIndex + 1) % tips.Count;
        ChatToAll(tips[_tipIndex]);
    }
}
