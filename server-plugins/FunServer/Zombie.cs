using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FunServer;

/// <summary>
/// The zombie mode, like on the CS 1.6 server and without custom models.
/// Everybody starts the round as a human on the CT side. After a countdown
/// a few random players turn into zombies (Terrorists): a red glow, lots of
/// health, faster, lower gravity, knife only. A zombie hit infects a human,
/// only the last human can be killed for real; a human a zombie kills
/// outright comes back as a zombie where they fell. Dead zombies come back
/// after a few seconds. Zombies glow red and humans blue, through walls.
/// The round ends through the game rules: the zombies win when no human is left.
/// </summary>
public sealed partial class FunServerPlugin {
    private const string ZombieMode = "zombie";
    private const int InfectionCountdownSeconds = 10;
    private const int PlayersPerFirstZombie = 6;
    private const int MinPlayersForInfection = 2;
    private const int FirstZombieHealth = 3000;
    private const int ZombieHealth = 2000;
    private const float ZombieSpeedFactor = 1.16f;
    private const float ZombieGravity = 0.7f;
    private const float ZombieRespawnSeconds = 5f;
    private const float InfectedRespawnSeconds = 0.5f;

    // The outline glows of the infection, seen through walls by everybody.
    private static readonly Color ZombieRed = Color.FromArgb(255, 255, 40, 40);
    private static readonly Color HumanBlue = Color.FromArgb(255, 60, 140, 255);

    private readonly HashSet<int> _zombies = [];
    private bool _isInfectionStarted;
    private bool _isLastHumanAnnounced;
    private Timer? _countdown;
    private int _countdownLeft;

    private bool IsZombieMode => _activeMode == ZombieMode;

    private void LoadZombie() {
        RegisterListener<Listeners.OnMapStart>(_ => ResetZombies());
        RegisterEventHandler<EventRoundPrestart>(OnZombieRoundPrestart);
        RegisterEventHandler<EventRoundStart>(OnZombieRoundStart);
        RegisterEventHandler<EventRoundEnd>((_, _) => {
            ResetZombies();
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerHurt>(OnZombieHurt);
        RegisterEventHandler<EventPlayerDeath>(OnZombieDeath);
        RegisterEventHandler<EventPlayerSpawn>(OnZombieSpawn);
        RegisterEventHandler<EventItemPickup>(OnZombiePickup);
        RegisterListener<Listeners.OnTick>(OnZombieTick);
    }

    /// <summary>Before the spawns: every player goes to the human side.</summary>
    private HookResult OnZombieRoundPrestart(EventRoundPrestart prestart, GameEventInfo info) {
        if (!IsZombieMode) {
            return HookResult.Continue;
        }
        ResetZombies();
        foreach (CCSPlayerController player in PlayingPlayers()) {
            if (player.Team != CsTeam.CounterTerrorist) {
                player.SwitchTeam(CsTeam.CounterTerrorist);
            }
        }
        return HookResult.Continue;
    }

    private HookResult OnZombieRoundStart(EventRoundStart roundStart, GameEventInfo info) {
        if (!IsZombieMode) {
            return HookResult.Continue;
        }
        _countdownLeft = InfectionCountdownSeconds;
        _countdown = AddTimer(1f, CountDown, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private void CountDown() {
        if (_countdownLeft > 0) {
            foreach (CCSPlayerController player in PlayingPlayers().Where(player => !player.IsBot)) {
                player.PrintToCenterHtml($"<font color='#FF3F3F'>ZOMBIE MODE</font><br>The infection starts in {_countdownLeft}. Find a good spot!", 2);
            }
            _countdownLeft--;
            return;
        }
        _countdown?.Kill();
        _countdown = null;
        StartInfection();
    }

    private void StartInfection() {
        List<CCSPlayerController> humans = AliveHumans().ToList();
        if (humans.Count < MinPlayersForInfection) {
            ChatToAll("Not enough players for the infection.");
            return;
        }
        _isInfectionStarted = true;
        int firstZombies = Math.Max(1, humans.Count / PlayersPerFirstZombie);
        foreach (CCSPlayerController player in humans.OrderBy(_ => Random.Shared.Next()).Take(firstZombies)) {
            Infect(player, FirstZombieHealth);
            ChatToAll($"{ChatColors.Red}{player.PlayerName}{ChatColors.Default} is the first zombie. Run!");
        }
        foreach (CCSPlayerController human in AliveHumans()) {
            SetGlow(human, HumanBlue);
            CenterTo(human, "#99CCFF", "RUN!", "The zombies are out. A zombie hit infects you.");
        }
    }

    /// <summary>Once per round, when only one human is left: everybody hunts them now.</summary>
    private void AnnounceLastHuman() {
        List<CCSPlayerController> humans = AliveHumans().ToList();
        if (_isLastHumanAnnounced || humans.Count != 1) {
            return;
        }
        _isLastHumanAnnounced = true;
        ChatToAll($"{ChatColors.LightBlue}{humans[0].PlayerName}{ChatColors.Default} is the last human!");
        CenterTo(humans[0], "#99CCFF", "LAST HUMAN", "Survive until the round ends!");
    }

    /// <summary>Hit by a zombie: the human turns, unless they are the last one.</summary>
    private HookResult OnZombieHurt(EventPlayerHurt hurt, GameEventInfo info) {
        if (!IsZombieMode || !_isInfectionStarted) {
            return HookResult.Continue;
        }
        CCSPlayerController? attacker = hurt.Attacker;
        CCSPlayerController? victim = hurt.Userid;
        bool isZombieHit = attacker != null && victim != null && _zombies.Contains(attacker.Slot) && !_zombies.Contains(victim.Slot);
        if (!isZombieHit || !victim!.PawnIsAlive || AliveHumans().Count() <= 1) {
            return HookResult.Continue;
        }
        Infect(victim, ZombieHealth);
        ChatToAll($"{ChatColors.Red}{victim.PlayerName}{ChatColors.Default} was infected by {attacker!.PlayerName}.");
        AnnounceLastHuman();
        return HookResult.Continue;
    }

    private HookResult OnZombieDeath(EventPlayerDeath death, GameEventInfo info) {
        if (!IsZombieMode || !_isInfectionStarted || death.Userid == null) {
            return HookResult.Continue;
        }
        CCSPlayerController victim = death.Userid;
        if (_zombies.Contains(victim.Slot)) {
            RespawnAsZombie(victim, ZombieRespawnSeconds, where: null);
            return HookResult.Continue;
        }

        // A knife can kill a human in one hit; that still counts as an infection while other humans live.
        bool byZombie = death.Attacker != null && _zombies.Contains(death.Attacker.Slot);
        if (byZombie && AliveHumans().Any()) {
            Vector? where = victim.PlayerPawn.Value?.AbsOrigin;
            Vector? spot = where == null ? null : new Vector(where.X, where.Y, where.Z);
            _zombies.Add(victim.Slot);
            victim.SwitchTeam(CsTeam.Terrorist);
            RespawnAsZombie(victim, InfectedRespawnSeconds, spot);
            AnnounceLastHuman();
        }
        return HookResult.Continue;
    }

    private void RespawnAsZombie(CCSPlayerController player, float delay, Vector? where) {
        AddTimer(delay, () => {
            if (!IsZombieMode || !_isInfectionStarted || !player.IsValid || player.PawnIsAlive) {
                return;
            }
            player.Respawn();
            Server.NextFrame(() => {
                if (!player.IsValid || player.PlayerPawn.Value is not CCSPlayerPawn pawn) {
                    return;
                }
                Infect(player, ZombieHealth);
                if (where != null) {
                    pawn.Teleport(where, null, null);
                }
            });
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>A zombie that spawns (respawn, late join) gets its zombie looks again.</summary>
    private HookResult OnZombieSpawn(EventPlayerSpawn spawn, GameEventInfo info) {
        CCSPlayerController? player = spawn.Userid;
        if (!IsZombieMode || !_isInfectionStarted || player == null || !_zombies.Contains(player.Slot)) {
            return HookResult.Continue;
        }
        Server.NextFrame(() => {
            if (player.IsValid && player.PawnIsAlive) {
                Infect(player, ZombieHealth);
            }
        });
        return HookResult.Continue;
    }

    private HookResult OnZombiePickup(EventItemPickup pickup, GameEventInfo info) {
        CCSPlayerController? player = pickup.Userid;
        bool isZombieWithGun = IsZombieMode && player != null && _zombies.Contains(player.Slot) && !pickup.Item.StartsWith("knife", StringComparison.Ordinal);
        if (isZombieWithGun) {
            // Next frame: the item is in the hands only after the event.
            Server.NextFrame(() => {
                if (player!.IsValid && player.PawnIsAlive) {
                    EquipOnly(player);
                }
            });
        }
        return HookResult.Continue;
    }

    private void Infect(CCSPlayerController player, int health) {
        _zombies.Add(player.Slot);
        if (player.Team != CsTeam.Terrorist) {
            player.SwitchTeam(CsTeam.Terrorist);
        }
        if (player.PlayerPawn.Value is not CCSPlayerPawn pawn || !pawn.IsValid) {
            return;
        }
        EquipOnly(player);
        SetHealth(pawn, health);
        pawn.GravityScale = ZombieGravity;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_flGravityScale");
        SetGlow(player, ZombieRed);
        CenterTo(player, "#FF3F3F", "You are a ZOMBIE", "Knife the humans to infect them.");
    }

    /// <summary>Speed has no cvar in CS2; the velocity modifier is set every tick, damage resets it.</summary>
    private void OnZombieTick() {
        if (_zombies.Count == 0) {
            return;
        }
        foreach (CCSPlayerController player in AlivePlayers().Where(player => _zombies.Contains(player.Slot))) {
            CCSPlayerPawn pawn = player.PlayerPawn.Value!;
            if (pawn.VelocityModifier != ZombieSpeedFactor) {
                pawn.VelocityModifier = ZombieSpeedFactor;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
            }
            if (player.IsBot) {
                MakeHunter(pawn.Bot);
            }
        }
    }

    /// <summary>
    /// zBot retreats when it is outnumbered, which a first zombie always is,
    /// and with only a knife it also runs off to guns on the ground. A rogue
    /// bot never retreats and hunts instead of playing the map's objective,
    /// so a zombie bot stays a rogue and never starts its weapon search. The
    /// bot rolls rogue again whenever its timer runs out, so both timers are
    /// pushed ahead on every tick.
    /// </summary>
    private static void MakeHunter(CCSBot? bot) {
        if (bot == null) {
            return;
        }
        float later = Server.CurrentTime + 60f;
        bot.IsRogue = true;
        bot.RogueTimer.Timestamp = later;
        bot.LookForWeaponsOnGroundTimer.Timestamp = later;
    }

    /// <summary>New round, new map: nobody is a zombie, and the pawns lose the zombie looks and glows.</summary>
    private void ResetZombies() {
        _countdown?.Kill();
        _countdown = null;
        bool hadInfection = _isInfectionStarted;
        _isInfectionStarted = false;
        _isLastHumanAnnounced = false;
        if (hadInfection) {
            // The red and blue glows belong to the infection.
            RemoveAllGlows();
        }
        if (_zombies.Count == 0) {
            return;
        }
        foreach (CCSPlayerController player in Utilities.GetPlayers().Where(player => player.IsValid && _zombies.Contains(player.Slot))) {
            if (player.PlayerPawn.Value is not CCSPlayerPawn pawn || !pawn.IsValid) {
                continue;
            }
            pawn.GravityScale = 1f;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_flGravityScale");
            pawn.VelocityModifier = 1f;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
        }
        _zombies.Clear();
    }

    private IEnumerable<CCSPlayerController> AliveHumans() {
        return AlivePlayers().Where(player => !_zombies.Contains(player.Slot) && player.Team == CsTeam.CounterTerrorist);
    }

    /// <summary>Everybody on a team, not the spectators and not CSTV.</summary>
    private static IEnumerable<CCSPlayerController> PlayingPlayers() {
        return Utilities.GetPlayers().Where(player => player.IsValid && !player.IsHLTV
            && (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist));
    }
}
