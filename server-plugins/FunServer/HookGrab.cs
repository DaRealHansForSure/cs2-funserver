using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace FunServer;

/// <summary>
/// The classic toys, on keys every CS2 player already has, so nobody needs a bind:
///   Hook       hold the inspect key (F): a glowing rope to the wall you aim
///              at pulls you there. Classic and Deathmatch only, like on 1.6.
///   Grab       admins hold the use key (E) on a player to lift them and
///              left click to throw them.
///   Parachute  hold the use key (E) while falling to float down slowly.
/// </summary>
public sealed partial class FunServerPlugin {
    private const float HookStopDistance = 48f;
    private const float GrabDistance = 160f;
    private const float GrabFollowStrength = 8f;
    private const float GrabThrowSpeed = 1400f;
    private const float ParachuteFallSpeed = 100f;
    private const float BeamWidth = 1.5f;

    private static readonly string[] HookModes = ["classic", "deathmatch"];

    // Bright, like the 1.6 hook: every rope gets one of these.
    private static readonly Color[] RopeColors = [
        Color.FromArgb(255, 255, 60, 60), Color.FromArgb(255, 60, 255, 60), Color.FromArgb(255, 60, 140, 255),
        Color.FromArgb(255, 255, 220, 0), Color.FromArgb(255, 255, 0, 200), Color.FromArgb(255, 0, 230, 230),
    ];

    private sealed class Rope(Vector point, CBeam? beam) {
        public Vector Point { get; } = point;
        public CBeam? Beam { get; } = beam;
    }

    private sealed class Grab(CCSPlayerPawn target, CBeam? beam) {
        public CCSPlayerPawn Target { get; } = target;
        public CBeam? Beam { get; } = beam;
    }

    private readonly Dictionary<int, Rope> _hooks = [];
    private readonly Dictionary<int, Grab> _grabs = [];

    /// <summary>For the live feed: where a player's hook holds, null when not hooking.</summary>
    private Vector? HookPointOf(CCSPlayerController player) {
        return _hooks.TryGetValue(player.Slot, out Rope? rope) ? rope.Point : null;
    }

    private void LoadHookGrab() {
        RegisterListener<Listeners.OnPlayerButtonsChanged>(OnButtonsChanged);
        RegisterListener<Listeners.OnTick>(OnTickHookGrab);
        RegisterEventHandler<EventPlayerDeath>((death, _) => {
            if (death.Userid != null) {
                ReleaseAll(death.Userid.Slot);
            }
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundEnd>((_, _) => {
            foreach (int slot in _hooks.Keys.Concat(_grabs.Keys).ToList()) {
                ReleaseAll(slot);
            }
            return HookResult.Continue;
        });
        RegisterListener<Listeners.OnClientDisconnect>(slot => ReleaseAll(slot));
    }

    private void OnButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released) {
        if (!player.IsValid || !player.PawnIsAlive || player.PlayerPawn.Value is not CCSPlayerPawn pawn) {
            return;
        }

        if (pressed.HasFlag(PlayerButtons.Inspect)) {
            StartHook(player, pawn);
        }
        if (released.HasFlag(PlayerButtons.Inspect)) {
            StopHook(player.Slot);
        }

        if (pressed.HasFlag(PlayerButtons.Use)) {
            StartGrab(player, pawn);
        }
        if (released.HasFlag(PlayerButtons.Use)) {
            StopGrab(player.Slot);
        }
        if (pressed.HasFlag(PlayerButtons.Attack) && _grabs.TryGetValue(player.Slot, out Grab? grab)) {
            Push(grab.Target, Scale(Forward(pawn.EyeAngles), GrabThrowSpeed));
            StopGrab(player.Slot);
        }
    }

    private void StartHook(CCSPlayerController player, CCSPlayerPawn pawn) {
        if (!HookEnabled.Value || !HookModes.Contains(_activeMode) || _hooks.ContainsKey(player.Slot)) {
            return;
        }
        if (HookAdminOnly.Value && !IsAdmin(player)) {
            return;
        }

        Vector eye = EyePosition(pawn);
        TraceResult trace = Trace.TraceShape(eye, pawn.EyeAngles, pawn, new TraceOptions { InteractsWith = Masks.Solid });
        if (!trace.DidHit()) {
            return;
        }
        Color color = RopeColors[Random.Shared.Next(RopeColors.Length)];
        _hooks[player.Slot] = new Rope(trace.EndPos, CreateBeam(eye, trace.EndPos, color));
    }

    private void StartGrab(CCSPlayerController player, CCSPlayerPawn pawn) {
        if (_grabs.ContainsKey(player.Slot) || (GrabAdminOnly.Value && !IsAdmin(player))) {
            return;
        }

        Vector eye = EyePosition(pawn);
        TraceResult trace = Trace.TraceShape(eye, pawn.EyeAngles, pawn, new TraceOptions { InteractsWith = Masks.Shot });
        CEntityInstance? hit = trace.DidHit() ? trace.HitEntity() : null;
        if (hit == null || hit.DesignerName != "player") {
            return;
        }
        var target = new CCSPlayerPawn(hit.Handle);
        if (!target.IsValid || target.LifeState != (byte)LifeState_t.LIFE_ALIVE) {
            return;
        }
        _grabs[player.Slot] = new Grab(target, CreateBeam(eye, target.AbsOrigin!, Color.FromArgb(255, 255, 255, 255)));
    }

    private void OnTickHookGrab() {
        if (_hooks.Count == 0 && _grabs.Count == 0 && !ParachuteEnabled.Value) {
            return;
        }

        foreach (CCSPlayerController player in AlivePlayers()) {
            CCSPlayerPawn pawn = player.PlayerPawn.Value!;
            if (_hooks.TryGetValue(player.Slot, out Rope? rope)) {
                PullTowards(pawn, rope);
            }
            if (_grabs.TryGetValue(player.Slot, out Grab? grab)) {
                HoldGrabbed(pawn, grab, player.Slot);
                continue;
            }
            if (ParachuteEnabled.Value && player.Buttons.HasFlag(PlayerButtons.Use)) {
                SlowFall(pawn);
            }
        }
    }

    private void PullTowards(CCSPlayerPawn pawn, Rope rope) {
        Vector origin = pawn.AbsOrigin!;
        Vector toPoint = new(rope.Point.X - origin.X, rope.Point.Y - origin.Y, rope.Point.Z - origin.Z);
        float distance = Length(toPoint);
        // At the wall the player hangs there instead of shaking against it.
        Vector velocity = distance < HookStopDistance ? new Vector(0, 0, 0) : Scale(toPoint, HookSpeed.Value / distance);
        Push(pawn, velocity);
        MoveBeam(rope.Beam, EyePosition(pawn), rope.Point);
    }

    private void HoldGrabbed(CCSPlayerPawn holder, Grab grab, int slot) {
        if (!grab.Target.IsValid || grab.Target.LifeState != (byte)LifeState_t.LIFE_ALIVE) {
            StopGrab(slot);
            return;
        }
        Vector eye = EyePosition(holder);
        Vector forward = Forward(holder.EyeAngles);
        var wanted = new Vector(eye.X + forward.X * GrabDistance, eye.Y + forward.Y * GrabDistance, eye.Z + forward.Z * GrabDistance);
        Vector origin = grab.Target.AbsOrigin!;
        // Center of the body, not the feet, sits on the aim point.
        var toWanted = new Vector(wanted.X - origin.X, wanted.Y - origin.Y, wanted.Z - 36f - origin.Z);
        Push(grab.Target, Scale(toWanted, GrabFollowStrength));
        MoveBeam(grab.Beam, eye, origin);
    }

    private static void SlowFall(CCSPlayerPawn pawn) {
        bool isOnGround = (pawn.Flags & OnGroundFlag) != 0;
        Vector velocity = pawn.AbsVelocity;
        if (isOnGround || velocity.Z >= -ParachuteFallSpeed) {
            return;
        }
        Push(pawn, new Vector(velocity.X, velocity.Y, -ParachuteFallSpeed));
    }

    private void StopHook(int slot) {
        if (_hooks.Remove(slot, out Rope? rope)) {
            RemoveBeam(rope.Beam);
        }
    }

    private void StopGrab(int slot) {
        if (_grabs.Remove(slot, out Grab? grab)) {
            RemoveBeam(grab.Beam);
        }
    }

    private void ReleaseAll(int slot) {
        StopHook(slot);
        StopGrab(slot);
        // Somebody else may be holding this player.
        foreach ((int holder, Grab grab) in _grabs.ToList()) {
            if (!grab.Target.IsValid || grab.Target.OriginalController.Value?.Slot == slot) {
                StopGrab(holder);
            }
        }
    }

    /// <summary>A laser rope; null when the engine would not make one, the toy still works.</summary>
    private static CBeam? CreateBeam(Vector start, Vector end, Color color) {
        CBeam? beam = Utilities.CreateEntityByName<CBeam>("beam");
        if (beam == null) {
            return null;
        }
        beam.Render = color;
        beam.Width = BeamWidth;
        beam.EndWidth = BeamWidth;
        beam.Teleport(start, new QAngle(0, 0, 0), new Vector(0, 0, 0));
        SetBeamEnd(beam, end);
        beam.DispatchSpawn();
        return beam;
    }

    private static void MoveBeam(CBeam? beam, Vector start, Vector end) {
        if (beam == null || !beam.IsValid) {
            return;
        }
        beam.Teleport(start, null, null);
        SetBeamEnd(beam, end);
    }

    private static void SetBeamEnd(CBeam beam, Vector end) {
        beam.EndPos.X = end.X;
        beam.EndPos.Y = end.Y;
        beam.EndPos.Z = end.Z;
        Utilities.SetStateChanged(beam, "CBeam", "m_vecEndPos");
    }

    private static void RemoveBeam(CBeam? beam) {
        if (beam != null && beam.IsValid) {
            beam.Remove();
        }
    }

    private static float Length(Vector vector) {
        return MathF.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
    }

    private static Vector Scale(Vector vector, float factor) {
        return new Vector(vector.X * factor, vector.Y * factor, vector.Z * factor);
    }
}
