using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace FunServer;

/// <summary>
/// The deathrun mode, like on the CS 1.6 server: each round one player is
/// the Terrorist who triggers the traps, everybody else runs the course as
/// CT. Humans are preferred as trapper (bots do not press trap buttons) and
/// nobody is trapper twice in a row while somebody else can be. Everybody
/// starts with a knife; weapons the map hands out at the end of the course
/// can still be picked up. Team balance, hook, parachute and bots are off in
/// modes\deathrun.cfg.
/// </summary>
public sealed partial class FunServerPlugin {
    private const string DeathrunMode = "deathrun";
    private const int NoPlayer = -1;

    private int _trapperSlot = NoPlayer;
    private int _lastTrapperSlot = NoPlayer;

    private bool IsDeathrunMode => _activeMode == DeathrunMode;

    private void LoadDeathrun() {
        RegisterListener<Listeners.OnMapStart>(_ => {
            _trapperSlot = NoPlayer;
            _lastTrapperSlot = NoPlayer;
        });
        RegisterEventHandler<EventRoundPrestart>(OnDeathrunRoundPrestart);
        RegisterEventHandler<EventRoundStart>(OnDeathrunRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnDeathrunSpawn);
    }

    /// <summary>Before the spawns: the trapper goes to T, everybody else to CT.</summary>
    private HookResult OnDeathrunRoundPrestart(EventRoundPrestart prestart, GameEventInfo info) {
        if (!IsDeathrunMode) {
            return HookResult.Continue;
        }
        List<CCSPlayerController> players = PlayingPlayers().ToList();
        CCSPlayerController? trapper = PickTrapper(players);
        _lastTrapperSlot = _trapperSlot;
        _trapperSlot = trapper?.Slot ?? NoPlayer;
        foreach (CCSPlayerController player in players) {
            CsTeam team = player == trapper ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
            if (player.Team != team) {
                player.SwitchTeam(team);
            }
        }
        return HookResult.Continue;
    }

    /// <summary>
    /// A human who was not trapper last round, else any human, else a bot.
    /// With one player alone there is no trapper: they run the course.
    /// </summary>
    private CCSPlayerController? PickTrapper(List<CCSPlayerController> players) {
        if (players.Count < 2) {
            return null;
        }
        List<CCSPlayerController> humans = players.Where(player => !player.IsBot).ToList();
        List<CCSPlayerController> pool = humans.Count > 0 ? humans : players;
        List<CCSPlayerController> fresh = pool.Where(player => player.Slot != _trapperSlot).ToList();
        if (fresh.Count > 0) {
            pool = fresh;
        }
        return pool[Random.Shared.Next(pool.Count)];
    }

    private HookResult OnDeathrunRoundStart(EventRoundStart roundStart, GameEventInfo info) {
        if (!IsDeathrunMode) {
            return HookResult.Continue;
        }
        CCSPlayerController? trapper = _trapperSlot == NoPlayer ? null : Utilities.GetPlayerFromSlot(_trapperSlot);
        if (trapper != null && trapper.IsValid) {
            ChatToAll($"{ChatColors.Red}{trapper.PlayerName}{ChatColors.Default} sets the traps. Everybody else: run!");
        }
        return HookResult.Continue;
    }

    /// <summary>A frame after the spawn the default weapons are there; they go, the knife stays.</summary>
    private HookResult OnDeathrunSpawn(EventPlayerSpawn spawn, GameEventInfo info) {
        CCSPlayerController? player = spawn.Userid;
        if (!IsDeathrunMode || player == null) {
            return HookResult.Continue;
        }
        Server.NextFrame(() => {
            if (player.IsValid && player.PawnIsAlive) {
                EquipOnly(player);
            }
        });
        return HookResult.Continue;
    }
}
