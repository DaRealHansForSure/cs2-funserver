using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace FunServer;

/// <summary>
/// Every few rounds in Classic a surprise rule for one round, like on the
/// CS 1.6 server. In the rounds that hand out the weapons (knives, AWP,
/// Deagle, HE) buying is off. The cvars a round changes go back by running
/// the mode configs again when the round ends (base.cfg resets them,
/// local.cfg keeps the host's values). Bots get their matching bot cvar, or
/// they fall back to the knife: a bot only uses an AWP with bot_allow_snipers on.
/// The window forces the next round with: fun_funround &lt;id|random|off&gt;
/// </summary>
public sealed partial class FunServerPlugin {
    private const string FunRoundMode = "classic";
    private const int JuggernautHealth = 1000;
    private const float SpeedFactor = 1.6f;
    private const int GhostAlpha = 40;
    private static readonly TimeSpan HeRefillDelay = TimeSpan.FromSeconds(1);

    private sealed record FunRound(string Id, string Title, string Description, string Cvars) {
        public bool HandsOutWeapons => Id is "knives" or "awp" or "deagle" or "hewar";
    }

    private static readonly FunRound[] FunRounds = [
        new("knives", "KNIVES ONLY", "Knives out, nothing else.", "bot_knives_only 1"),
        new("awp", "AWP ONLY", "Everybody gets an AWP.", "bot_allow_snipers 1"),
        new("lowgrav", "LOW GRAVITY", "Jump high, fall slowly.", "sv_gravity 250"),
        new("speed", "SPEED", "Everybody runs much faster.", ""),
        new("headshot", "HEADSHOTS ONLY", "Only headshots do damage.", "mp_damage_headshot_only 1"),
        new("deagle", "ONE BULLET DEAGLE", "One bullet, every kill gives a new one.", "bot_allow_pistols 1"),
        new("hewar", "HE WAR", "HE grenades only, refilled after every throw.", "bot_knives_only 1"),
        new("ghost", "GHOSTS", "Everybody is almost invisible.", ""),
        new("juggernaut", "JUGGERNAUT", "One player with 1000 HP and an M249 against everybody.", ""),
    ];

    private int _roundsPlayed;
    private string? _forcedRoundId;
    private FunRound? _currentRound;
    // True from the end of freeze time on: before that a restart does not count the round as played.
    private bool _isFunRoundPlaying;

    private void LoadFunRounds() {
        RegisterListener<Listeners.OnMapStart>(_ => {
            _roundsPlayed = 0;
            _currentRound = null;
        });
        RegisterEventHandler<EventRoundPrestart>(OnFunRoundPrestart);
        RegisterEventHandler<EventRoundStart>(OnFunRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnFunRoundFreezeEnd);
        RegisterEventHandler<EventRoundEnd>(OnFunRoundEnd);
        RegisterEventHandler<EventPlayerDeath>(OnFunRoundKill);
        RegisterEventHandler<EventGrenadeThrown>(OnFunRoundGrenade);
        RegisterListener<Listeners.OnTick>(OnFunRoundTick);
    }

    [ConsoleCommand("fun_funround", "fun_funround <id|random|off> - the next round is this fun round")]
    [CommandHelper(minArgs: 1, usage: "<knives|awp|lowgrav|speed|headshot|deagle|hewar|ghost|juggernaut|random|off>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnForceFunRound(CCSPlayerController? caller, CommandInfo command) {
        string id = command.GetArg(1).ToLowerInvariant();
        if (id == "off") {
            _forcedRoundId = null;
            command.ReplyToCommand("No forced fun round.");
            return;
        }
        if (id != "random" && FunRounds.All(round => round.Id != id)) {
            command.ReplyToCommand($"Unknown fun round {id}.");
            return;
        }
        _forcedRoundId = id;
        command.ReplyToCommand($"Next round: {id}.");
    }

    /// <summary>
    /// Before anybody spawns: the round is picked and its cvars are set here,
    /// so bots can not buy before mp_buytime 0 takes. A restart (mp_restartgame)
    /// starts a round without ending the last one, so a leftover is cleaned
    /// up here as well.
    /// </summary>
    private HookResult OnFunRoundPrestart(EventRoundPrestart prestart, GameEventInfo info) {
        if (_currentRound != null) {
            EndFunRound();
        }
        if (IsWarmup()) {
            return HookResult.Continue;
        }
        _roundsPlayed++;

        FunRound? round = PickFunRound();
        if (round == null) {
            return HookResult.Continue;
        }
        _currentRound = round;
        _isFunRoundPlaying = false;
        if (round.Cvars.Length > 0) {
            Server.ExecuteCommand(round.Cvars);
        }
        // In the rounds that hand out the weapons nobody buys their way out of the rule.
        if (round.HandsOutWeapons) {
            Server.ExecuteCommand("mp_buytime 0");
        }
        return HookResult.Continue;
    }

    private HookResult OnFunRoundStart(EventRoundStart roundStart, GameEventInfo info) {
        FunRound? round = _currentRound;
        if (round == null) {
            return HookResult.Continue;
        }
        // A frame later the spawned players have their default weapons, which the round replaces.
        Server.NextFrame(() => ApplyFunRound(round));
        ChatToAll($"{ChatColors.Red}{round.Title}{ChatColors.Default} - {round.Description}");
        return HookResult.Continue;
    }

    /// <summary>Once more when the round runs: whatever was still bought in freeze time goes again.</summary>
    private HookResult OnFunRoundFreezeEnd(EventRoundFreezeEnd freezeEnd, GameEventInfo info) {
        FunRound? round = _currentRound;
        if (round == null) {
            return HookResult.Continue;
        }
        _isFunRoundPlaying = true;
        foreach (CCSPlayerController player in AlivePlayers()) {
            EquipFor(round, player);
        }
        return HookResult.Continue;
    }

    private FunRound? PickFunRound() {
        string? forced = _forcedRoundId;
        _forcedRoundId = null;
        if (forced != null) {
            return forced == "random" ? RandomFunRound() : FunRounds.First(round => round.Id == forced);
        }
        bool isDue = FunRoundsEnabled.Value && _activeMode == FunRoundMode
            && FunRoundsEvery.Value > 0 && _roundsPlayed % FunRoundsEvery.Value == 0;
        return isDue ? RandomFunRound() : null;
    }

    private static FunRound RandomFunRound() {
        return FunRounds[Random.Shared.Next(FunRounds.Length)];
    }

    private void ApplyFunRound(FunRound round) {
        List<CCSPlayerController> players = AlivePlayers().ToList();
        foreach (CCSPlayerController player in players) {
            EquipFor(round, player);
            if (round.Id == "ghost") {
                CCSPlayerPawn pawn = player.PlayerPawn.Value!;
                pawn.RenderMode = RenderMode_t.kRenderTransAlpha;
                Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_nRenderMode");
                SetRender(pawn, Color.FromArgb(GhostAlpha, pawn.Render.R, pawn.Render.G, pawn.Render.B));
            }
        }

        if (round.Id == "juggernaut" && players.Count > 1) {
            CCSPlayerController juggernaut = players[Random.Shared.Next(players.Count)];
            CCSPlayerPawn pawn = juggernaut.PlayerPawn.Value!;
            SetHealth(pawn, JuggernautHealth);
            juggernaut.GiveNamedItem("weapon_m249");
            SetRender(pawn, Color.FromArgb(255, 255, 60, 60));
            ChatToAll($"{ChatColors.Red}{juggernaut.PlayerName}{ChatColors.Default} is the Juggernaut. Get them!");
        }
    }

    /// <summary>The weapons of the rounds that hand out weapons; the other rounds keep what players have.</summary>
    private static void EquipFor(FunRound round, CCSPlayerController player) {
        switch (round.Id) {
            case "knives":
                EquipOnly(player);
                break;
            case "awp":
                EquipOnly(player, "weapon_awp");
                break;
            case "deagle":
                EquipOnly(player, "weapon_deagle");
                Server.NextFrame(() => SetActiveAmmo(player, clip: 1, reserve: 0));
                break;
            case "hewar":
                EquipOnly(player, "weapon_hegrenade");
                break;
        }
    }

    /// <summary>Takes every weapon and gives the knife plus the round's weapon.</summary>
    private static void EquipOnly(CCSPlayerController player, string? weapon = null) {
        player.RemoveWeapons();
        player.GiveNamedItem("weapon_knife");
        if (weapon != null) {
            player.GiveNamedItem(weapon);
        }
    }

    private static void SetActiveAmmo(CCSPlayerController player, int clip, int reserve) {
        CBasePlayerWeapon? weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        if (weapon == null || !weapon.IsValid) {
            return;
        }
        weapon.Clip1 = clip;
        Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_iClip1");
        weapon.ReserveAmmo[0] = reserve;
        Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_pReserveAmmo");
    }

    private HookResult OnFunRoundKill(EventPlayerDeath death, GameEventInfo info) {
        if (_currentRound?.Id != "deagle" || death.Attacker == null || !death.Attacker.PawnIsAlive) {
            return HookResult.Continue;
        }
        CBasePlayerWeapon? weapon = death.Attacker.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        if (weapon != null && weapon.IsValid && weapon.DesignerName == "weapon_deagle") {
            SetActiveAmmo(death.Attacker, weapon.Clip1 + 1, 0);
        }
        return HookResult.Continue;
    }

    private HookResult OnFunRoundGrenade(EventGrenadeThrown thrown, GameEventInfo info) {
        if (_currentRound?.Id != "hewar" || thrown.Userid == null || thrown.Weapon != "hegrenade") {
            return HookResult.Continue;
        }
        CCSPlayerController thrower = thrown.Userid;
        AddTimer((float)HeRefillDelay.TotalSeconds, () => {
            if (_currentRound?.Id == "hewar" && thrower.IsValid && thrower.PawnIsAlive) {
                thrower.GiveNamedItem("weapon_hegrenade");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    /// <summary>CS2 has no speed cvar; the velocity modifier is set again every tick, damage resets it.</summary>
    private void OnFunRoundTick() {
        if (_currentRound?.Id != "speed") {
            return;
        }
        foreach (CCSPlayerController player in AlivePlayers()) {
            CCSPlayerPawn pawn = player.PlayerPawn.Value!;
            if (pawn.VelocityModifier != SpeedFactor) {
                pawn.VelocityModifier = SpeedFactor;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
            }
        }
    }

    private HookResult OnFunRoundEnd(EventRoundEnd roundEnd, GameEventInfo info) {
        if (_currentRound != null) {
            EndFunRound();
        }
        return HookResult.Continue;
    }

    /// <summary>
    /// Every rule back: the configs reset the cvars, and every pawn, dead ones
    /// too, loses tint, transparency and speed (CS2 reuses a pawn on respawn).
    /// A round that ends before its freeze time was over was never played,
    /// a restart (mp_restartgame 1 runs a short round first): it comes again.
    /// </summary>
    private void EndFunRound() {
        if (_currentRound != null && !_isFunRoundPlaying) {
            _forcedRoundId = _currentRound.Id;
            _roundsPlayed--;
        }
        _currentRound = null;
        RunModeConfigs();
        foreach (CCSPlayerController player in Utilities.GetPlayers().Where(player => player.IsValid)) {
            CCSPlayerPawn? pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid) {
                continue;
            }
            pawn.RenderMode = RenderMode_t.kRenderNormal;
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_nRenderMode");
            SetRender(pawn, Untinted);
            pawn.VelocityModifier = 1f;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
        }
    }

    private static bool IsWarmup() {
        CCSGameRules? rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        return rules?.WarmupPeriod == true;
    }
}
