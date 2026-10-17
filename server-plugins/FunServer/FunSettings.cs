using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace FunServer;

/// <summary>
/// The plugin's cvars and the small helpers every fun part shares. The cvars
/// carry the same names as on the CS 1.6 server, so the window and the configs
/// (modes\*.cfg, local.cfg) set them the same way. CounterStrikeSharp
/// registers the public FakeConVar fields when the plugin loads.
/// </summary>
public sealed partial class FunServerPlugin {
    public readonly FakeConVar<bool> HookEnabled = new("fun_hook_enabled", "1 = players may hook (hold the inspect key, F)", true);
    public readonly FakeConVar<float> HookSpeed = new("fun_hook_speed", "Hook pull speed in units per second", 750f);
    public readonly FakeConVar<bool> HookAdminOnly = new("fun_hook_admin_only", "1 = only admins may hook", false);
    public readonly FakeConVar<bool> GrabAdminOnly = new("fun_grab_admin_only", "1 = only admins may grab players (hold E on a player)", true);
    public readonly FakeConVar<bool> ParachuteEnabled = new("fun_parachute_enabled", "1 = hold E while falling to float down", true);
    public readonly FakeConVar<bool> FunRoundsEnabled = new("fun_rounds_enabled", "1 = a fun round every few rounds in Classic", true);
    public readonly FakeConVar<int> FunRoundsEvery = new("fun_rounds_every", "A fun round every this many rounds", 3);
    public readonly FakeConVar<bool> StreaksEnabled = new("fun_streaks_enabled", "1 = rewards for kills in a row", true);

    // CS2's player pawn flag for standing on something.
    private const uint OnGroundFlag = 1;
    private const string AdminPermission = "@css/generic";

    private static readonly Color Untinted = Color.FromArgb(255, 255, 255, 255);

    private static IEnumerable<CCSPlayerController> AlivePlayers() {
        return Utilities.GetPlayers().Where(player => player.IsValid && player.PawnIsAlive && player.PlayerPawn.Value?.IsValid == true);
    }

    /// <summary>
    /// An admin from CounterStrikeSharp's admins.json. Until the host has put
    /// anybody there, everybody on the LAN counts as admin, so the admin toys
    /// work out of the box; a list switches that off.
    /// </summary>
    private static bool IsAdmin(CCSPlayerController player) {
        if (!HasAnyAdmin()) {
            return true;
        }
        return AdminManager.PlayerHasPermissions(player, AdminPermission);
    }

    private static bool? _hasAnyAdmin;

    private static bool HasAnyAdmin() {
        if (_hasAnyAdmin != null) {
            return _hasAnyAdmin.Value;
        }
        string adminsFile = Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "configs", "admins.json");
        try {
            // An empty file holds "{}" or nothing; any admin entry needs a "identity" key.
            _hasAnyAdmin = File.Exists(adminsFile) && File.ReadAllText(adminsFile).Contains("\"identity\"", StringComparison.OrdinalIgnoreCase);
        } catch (IOException) {
            _hasAnyAdmin = false;
        }
        return _hasAnyAdmin.Value;
    }

    private static Vector EyePosition(CCSPlayerPawn pawn) {
        Vector origin = pawn.AbsOrigin!;
        return new Vector(origin.X, origin.Y, origin.Z + pawn.ViewOffset.Z);
    }

    private static Vector Forward(QAngle angles) {
        float pitch = angles.X * MathF.PI / 180f;
        float yaw = angles.Y * MathF.PI / 180f;
        return new Vector(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), -MathF.Sin(pitch));
    }

    /// <summary>Teleport with only a velocity: the engine's own way to push a player.</summary>
    private static void Push(CCSPlayerPawn pawn, Vector velocity) {
        pawn.Teleport(null, null, velocity);
    }

    private static void SetRender(CCSPlayerPawn pawn, Color color) {
        pawn.Render = color;
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
    }

    private static void SetHealth(CCSPlayerPawn pawn, int health) {
        if (health > pawn.MaxHealth) {
            pawn.MaxHealth = health;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
        }
        pawn.Health = health;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    private static void ChatToAll(string text) {
        Server.PrintToChatAll($" {ChatColors.Gold}[Fun]{ChatColors.Default} {text}");
    }

    private static void Chat(CCSPlayerController player, string text) {
        player.PrintToChat($" {ChatColors.Gold}[Fun]{ChatColors.Default} {text}");
    }
}
