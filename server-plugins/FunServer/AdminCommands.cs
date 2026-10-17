using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace FunServer;

/// <summary>
/// The commands the window sends over RCON for the selected player, with
/// the same names and targets as on the CS 1.6 server: "#userid", a part of
/// the name, or "@all". Server console only; players use the admin plugin.
/// </summary>
public sealed partial class FunServerPlugin {
    private static readonly IReadOnlyDictionary<string, Color> GlowColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase) {
        ["red"] = Color.FromArgb(255, 63, 63),
        ["green"] = Color.FromArgb(63, 255, 63),
        ["blue"] = Color.FromArgb(63, 127, 255),
        ["yellow"] = Color.FromArgb(255, 220, 0),
        ["pink"] = Color.FromArgb(255, 0, 200),
        ["white"] = Color.FromArgb(255, 255, 255),
    };

    [ConsoleCommand("fun_slay", "fun_slay <target> - kills the target")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnSlay(CCSPlayerController? caller, CommandInfo command) {
        foreach (CCSPlayerPawn pawn in AlivePawns(command.GetArg(1))) {
            pawn.CommitSuicide(false, true);
        }
    }

    [ConsoleCommand("fun_godmode", "fun_godmode <target> <1|0> - no damage")]
    [CommandHelper(minArgs: 2, usage: "<target> <1|0>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnGodmode(CCSPlayerController? caller, CommandInfo command) {
        bool isOn = command.GetArg(2) != "0";
        foreach (CCSPlayerPawn pawn in AlivePawns(command.GetArg(1))) {
            pawn.TakesDamage = !isOn;
        }
    }

    [ConsoleCommand("fun_noclip", "fun_noclip <target> <1|0> - fly through walls")]
    [CommandHelper(minArgs: 2, usage: "<target> <1|0>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnNoclip(CCSPlayerController? caller, CommandInfo command) {
        bool isOn = command.GetArg(2) != "0";
        foreach (CCSPlayerPawn pawn in AlivePawns(command.GetArg(1))) {
            pawn.MoveType = isOn ? MoveType_t.MOVETYPE_NOCLIP : MoveType_t.MOVETYPE_WALK;
            Schema.SetSchemaValue(pawn.Handle, "CBaseEntity", "m_nActualMoveType", isOn ? 8 : 2);
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
        }
    }

    /// <summary>A colored render tint for now; a real outline glow follows with the fun plugins.</summary>
    [ConsoleCommand("fun_glow", "fun_glow <target> <red|green|blue|yellow|pink|white|off>")]
    [CommandHelper(minArgs: 2, usage: "<target> <color|off>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnGlow(CCSPlayerController? caller, CommandInfo command) {
        string colorName = command.GetArg(2);
        Color color = GlowColors.TryGetValue(colorName, out Color picked) ? picked : Color.White;
        foreach (CCSPlayerPawn pawn in AlivePawns(command.GetArg(1))) {
            pawn.Render = color;
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        }
    }

    [ConsoleCommand("fun_rocket", "fun_rocket <target> - launches the target into the sky")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnRocket(CCSPlayerController? caller, CommandInfo command) {
        foreach (CCSPlayerPawn pawn in AlivePawns(command.GetArg(1))) {
            pawn.AbsVelocity.Z = 1200;
            CCSPlayerPawn launched = pawn;
            AddTimer(1.6f, () => {
                if (launched.IsValid && launched.LifeState == (byte)LifeState_t.LIFE_ALIVE) {
                    launched.CommitSuicide(true, true);
                }
            });
        }
    }

    /// <summary>"#12" is a user id, "@all" everybody alive, anything else a part of the name.</summary>
    private static IEnumerable<CCSPlayerPawn> AlivePawns(string target) {
        foreach (CCSPlayerController player in Utilities.GetPlayers()) {
            if (!player.IsValid || !player.PawnIsAlive || !Matches(player, target)) {
                continue;
            }
            CCSPlayerPawn? pawn = player.PlayerPawn.Value;
            if (pawn != null && pawn.IsValid) {
                yield return pawn;
            }
        }
    }

    private static bool Matches(CCSPlayerController player, string target) {
        if (target.Equals("@all", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }
        if (target.StartsWith('#') && int.TryParse(target[1..], out int userId)) {
            return player.UserId == userId;
        }
        return player.PlayerName.Contains(target, StringComparison.OrdinalIgnoreCase);
    }
}
