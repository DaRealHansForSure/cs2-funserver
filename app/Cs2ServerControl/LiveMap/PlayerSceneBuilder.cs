using System.Windows;
using Cs2ServerControl.Services;
using HelixToolkit.SharpDX.Core;
using SharpDX;
using MediaColor = System.Windows.Media.Color;
using NumericsVector3 = System.Numerics.Vector3;

namespace Cs2ServerControl.LiveMap;

/// <summary>The token colors the 3D scene needs, read once from the theme.</summary>
public sealed record SceneColors(
    MediaColor Background,
    MediaColor TeamT,
    MediaColor TeamCT,
    MediaColor TeamSpectator,
    MediaColor Hook,
    MediaColor Dead,
    MediaColor SelectionRing,
    MediaColor WireLow,
    MediaColor WireHigh,
    MediaColor WireGrid,
    MediaColor TextPrimary) {
    public static SceneColors FromResources(FrameworkElement source) {
        return new SceneColors(
            Read(source, "ViewportBackgroundColor"),
            Read(source, "TeamTColor"),
            Read(source, "TeamCTColor"),
            Read(source, "TeamSpectatorColor"),
            Read(source, "HookColor"),
            Read(source, "DeadMarkerColor"),
            Read(source, "SelectionRingColor"),
            Read(source, "WireLowColor"),
            Read(source, "WireHighColor"),
            Read(source, "WireGridColor"),
            Read(source, "TextPrimaryColor"));
    }

    public MediaColor TeamColor(Team team) {
        switch (team) {
            case Team.Terrorist:
                return TeamT;
            case Team.CounterTerrorist:
                return TeamCT;
            default:
                return TeamSpectator;
        }
    }

    private static MediaColor Read(FrameworkElement source, string key) {
        return (MediaColor)source.FindResource(key);
    }
}

/// <summary>Line geometry for all players of one frame, grouped by line thickness.</summary>
public sealed record PlayerLines(LineGeometry3D Thin, LineGeometry3D Medium, LineGeometry3D Wide);

/// <summary>
/// Builds the per-frame player lines: body line, view direction, trails, hook
/// beam, dead markers and the selection ring. Opacity is mixed into the
/// colors against the flat viewport background (see WireframeScene.Blend).
/// </summary>
public static class PlayerSceneBuilder {
    private const float ViewLineLength = 140f;
    private const float DeadCrossSize = 24f;
    private const float ShotHitCrossSize = 10f;

    private const int SelectionRingSegments = 24;

    public static PlayerLines Build(
        IReadOnlyList<PlayerRenderState> players,
        int? selectedUserId,
        float? selectionRingRadius,
        bool showTrails,
        IReadOnlyList<VisibleShot> shots,
        Vector3 cameraLookDirection,
        SceneColors colors) {
        var thin = new LineSet();
        var medium = new LineSet();
        var wide = new LineSet();

        // Shots first, so the players are drawn on top of a crossfire.
        AddShots(thin, medium, shots, colors);

        foreach (PlayerRenderState player in players) {
            MediaColor teamColor = colors.TeamColor(player.Team);
            Vector3 origin = ToVector(player.Origin);
            Vector3 feet = origin + new Vector3(0, 0, LiveMapViewModel.FeetOffset);
            Vector3 head = origin + new Vector3(0, 0, LiveMapViewModel.HeadOffset);

            if (!player.IsAlive) {
                // A grey x on the floor where the player died.
                Color4 dead = WireframeScene.Blend(colors.Dead, colors.Background, 1f);
                float half = DeadCrossSize / 2;
                thin.Add(feet + new Vector3(-half, -half, 0), feet + new Vector3(half, half, 0), dead);
                thin.Add(feet + new Vector3(-half, half, 0), feet + new Vector3(half, -half, 0), dead);
                continue;
            }

            thin.Add(feet, head, WireframeScene.Blend(teamColor, colors.Background, 0.6f));

            float yaw = player.Yaw * MathF.PI / 180f;
            var viewDirection = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0);
            medium.Add(head, head + viewDirection * ViewLineLength, WireframeScene.Blend(teamColor, colors.Background, 0.95f));

            if (showTrails && player.Trail.Count >= 2) {
                Color4 trailColor = WireframeScene.Blend(teamColor, colors.Background, 0.3f);
                for (int index = 1; index < player.Trail.Count; index++) {
                    Vector3 from = ToVector(player.Trail[index - 1]) + new Vector3(0, 0, LiveMapViewModel.FeetOffset);
                    Vector3 to = ToVector(player.Trail[index]) + new Vector3(0, 0, LiveMapViewModel.FeetOffset);
                    medium.Add(from, to, trailColor);
                }
            }

            if (player.IsHooking) {
                Vector3 hookPoint = ToVector(player.HookPoint);
                wide.Add(head, hookPoint, WireframeScene.Blend(colors.Hook, colors.Background, 0.3f));
                medium.Add(head, hookPoint, WireframeScene.Blend(colors.Hook, colors.Background, 1f));
            }

            if (player.UserId == selectedUserId && selectionRingRadius != null) {
                AddCameraFacingRing(medium, head, selectionRingRadius.Value, cameraLookDirection, WireframeScene.Blend(colors.SelectionRing, colors.Background, 0.85f));
            }
        }

        return new PlayerLines(thin.ToGeometry(), medium.ToGeometry(), wide.ToGeometry());
    }

    /// <summary>
    /// Bullet paths in the shooter's team color, fading with age. A bullet that
    /// hit a player is drawn thicker and ends in a small cross.
    /// </summary>
    private static void AddShots(LineSet thin, LineSet medium, IReadOnlyList<VisibleShot> shots, SceneColors colors) {
        foreach (VisibleShot shot in shots) {
            MediaColor teamColor = colors.TeamColor(shot.Team);
            Vector3 start = ToVector(shot.Start);
            Vector3 end = ToVector(shot.End);
            if (!shot.HitPlayer) {
                thin.Add(start, end, WireframeScene.Blend(teamColor, colors.Background, 0.55f * shot.Strength));
                continue;
            }

            Color4 hitColor = WireframeScene.Blend(teamColor, colors.Background, 0.9f * shot.Strength);
            medium.Add(start, end, hitColor);
            float half = ShotHitCrossSize / 2;
            thin.Add(end + new Vector3(-half, 0, -half), end + new Vector3(half, 0, half), hitColor);
            thin.Add(end + new Vector3(0, -half, -half), end + new Vector3(0, half, half), hitColor);
            thin.Add(end + new Vector3(-half, -half, 0), end + new Vector3(half, half, 0), hitColor);
        }
    }

    /// <summary>A white circle around the head marker that always faces the camera.</summary>
    private static void AddCameraFacingRing(LineSet lines, Vector3 center, float radius, Vector3 lookDirection, Color4 color) {
        Vector3 forward = Vector3.Normalize(lookDirection);
        Vector3 helper = MathF.Abs(forward.Z) > 0.9f ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, helper));
        Vector3 up = Vector3.Cross(right, forward);

        Vector3 previous = center + right * radius;
        for (int segment = 1; segment <= SelectionRingSegments; segment++) {
            float angle = segment * 2 * MathF.PI / SelectionRingSegments;
            Vector3 next = center + (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * radius;
            lines.Add(previous, next, color);
            previous = next;
        }
    }

    public static Vector3 ToVector(NumericsVector3 vector) {
        return new Vector3(vector.X, vector.Y, vector.Z);
    }

    private sealed class LineSet {
        private readonly Vector3Collection _positions = [];
        private readonly IntCollection _indices = [];
        private readonly Color4Collection _colors = [];

        public void Add(Vector3 from, Vector3 to, Color4 color) {
            _indices.Add(_positions.Count);
            _positions.Add(from);
            _colors.Add(color);
            _indices.Add(_positions.Count);
            _positions.Add(to);
            _colors.Add(color);
        }

        public LineGeometry3D ToGeometry() {
            return new LineGeometry3D {
                Positions = _positions,
                Indices = _indices,
                Colors = _colors,
            };
        }
    }
}
