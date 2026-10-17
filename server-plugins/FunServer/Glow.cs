using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace FunServer;

/// <summary>
/// A real outline glow around a player, seen through walls. CS2 draws no
/// glow on the player pawn itself, so a copy of the player's model follows
/// the pawn and carries the glow: an invisible relay prop follows the pawn
/// (FollowEntity), the glowing prop follows the relay. The live feed reports
/// the glow color, so the window's Live Map shows it as well.
/// </summary>
public sealed partial class FunServerPlugin {
    private const int GlowRange = 5000;
    private const int GlowRangeMin = 20;
    // 3: the outline that shows through walls.
    private const int GlowTypeOutline = 3;
    // Prop spawn flag: no physics, only a model that can follow something.
    private const uint StaticPropFlag = 256u;

    private sealed record PlayerGlow(Color Color, CBaseModelEntity Relay, CBaseModelEntity Outline);

    private readonly Dictionary<int, PlayerGlow> _glows = [];

    private void LoadGlow() {
        RegisterListener<Listeners.OnMapStart>(_ => _glows.Clear());
        RegisterEventHandler<EventPlayerDeath>((death, _) => {
            if (death.Userid != null) {
                RemoveGlow(death.Userid);
            }
            return HookResult.Continue;
        });
        RegisterListener<Listeners.OnClientDisconnect>(slot => {
            if (_glows.Remove(slot, out PlayerGlow? glow)) {
                RemoveEntity(glow.Relay);
                RemoveEntity(glow.Outline);
            }
        });
    }

    /// <summary>The glow color of a player for the live feed, null without a glow.</summary>
    private Color? GlowColorOf(CCSPlayerController player) {
        return _glows.TryGetValue(player.Slot, out PlayerGlow? glow) ? glow.Color : null;
    }

    private void SetGlow(CCSPlayerController player, Color color) {
        if (_glows.TryGetValue(player.Slot, out PlayerGlow? existing) && existing.Color == color && existing.Outline.IsValid) {
            return;
        }
        RemoveGlow(player);
        if (!player.PawnIsAlive || player.PlayerPawn.Value is not CCSPlayerPawn pawn || !pawn.IsValid) {
            return;
        }
        string? model = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName;
        if (string.IsNullOrEmpty(model)) {
            return;
        }

        CBaseModelEntity? relay = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
        CBaseModelEntity? outline = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
        if (relay == null || outline == null) {
            return;
        }

        relay.SetModel(model);
        relay.Spawnflags = StaticPropFlag;
        relay.RenderMode = RenderMode_t.kRenderNone;
        relay.DispatchSpawn();

        outline.SetModel(model);
        outline.Spawnflags = StaticPropFlag;
        outline.DispatchSpawn();
        outline.Glow.GlowColorOverride = color;
        outline.Glow.GlowRange = GlowRange;
        outline.Glow.GlowRangeMin = GlowRangeMin;
        // -1: every team sees it.
        outline.Glow.GlowTeam = -1;
        outline.Glow.GlowType = GlowTypeOutline;

        relay.AcceptInput("FollowEntity", pawn, relay, "!activator");
        outline.AcceptInput("FollowEntity", relay, outline, "!activator");
        _glows[player.Slot] = new PlayerGlow(color, relay, outline);
    }

    private void RemoveGlow(CCSPlayerController player) {
        if (_glows.Remove(player.Slot, out PlayerGlow? glow)) {
            RemoveEntity(glow.Relay);
            RemoveEntity(glow.Outline);
        }
    }

    private void RemoveAllGlows() {
        foreach (PlayerGlow glow in _glows.Values) {
            RemoveEntity(glow.Relay);
            RemoveEntity(glow.Outline);
        }
        _glows.Clear();
    }

    private static void RemoveEntity(CBaseEntity entity) {
        if (entity.IsValid) {
            entity.Remove();
        }
    }
}
