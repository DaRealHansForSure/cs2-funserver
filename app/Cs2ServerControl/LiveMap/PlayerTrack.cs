using System.Numerics;
using Cs2ServerControl.Services;

namespace Cs2ServerControl.LiveMap;

/// <summary>What the 3D scene draws for one player in one frame.</summary>
public sealed record PlayerRenderState(
    int UserId,
    string Name,
    Team Team,
    bool IsAlive,
    Vector3 Origin,
    float Yaw,
    bool IsHooking,
    Vector3 HookPoint,
    IReadOnlyList<Vector3> Trail,
    PlayerGlow? Glow,
    string Weapon = "");

/// <summary>
/// The movement of one player between feed packets: positions are blended
/// over 200 ms from the last drawn point to the newest one, so markers glide
/// instead of jumping five times a second.
/// </summary>
public sealed class PlayerTrack {
    public static readonly TimeSpan BlendTime = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan TrailSampleInterval = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan TrailLength = TimeSpan.FromSeconds(2);

    private readonly Queue<(DateTime At, Vector3 Origin)> _trail = new();
    private Vector3 _fromOrigin;
    private float _fromYaw;
    private DateTime _receivedAt;
    private DateTime _lastTrailSample = DateTime.MinValue;

    public PlayerTrack(LivePlayer player, DateTime now) {
        Latest = player;
        _fromOrigin = player.Origin;
        _fromYaw = player.Yaw;
        _receivedAt = now;
    }

    public LivePlayer Latest { get; private set; }

    public void Update(LivePlayer player, DateTime now) {
        // Start the next blend where the marker is drawn right now.
        _fromOrigin = OriginAt(now);
        _fromYaw = YawAt(now);

        bool teleported = Vector3.Distance(player.Origin, Latest.Origin) > 400f || player.IsAlive != Latest.IsAlive;
        if (teleported) {
            // Respawns and teleports should not slide across the map.
            _fromOrigin = player.Origin;
            _fromYaw = player.Yaw;
            _trail.Clear();
        }

        Latest = player;
        _receivedAt = now;
        SampleTrail(now);
    }

    public PlayerRenderState RenderState(DateTime now) {
        Vector3 origin = OriginAt(now);
        var trail = new List<Vector3>(_trail.Count + 1);
        foreach ((DateTime at, Vector3 point) in _trail) {
            if (now - at <= TrailLength) {
                trail.Add(point);
            }
        }
        trail.Add(origin);

        return new PlayerRenderState(
            Latest.UserId,
            Latest.Name,
            Latest.Team,
            Latest.IsAlive,
            origin,
            YawAt(now),
            Latest.IsHooking,
            Latest.HookPoint,
            trail,
            Latest.Glow,
            Latest.IsAlive ? Latest.Weapon : "");
    }

    private Vector3 OriginAt(DateTime now) {
        float blend = Progress(now);
        return Vector3.Lerp(_fromOrigin, Latest.Origin, blend);
    }

    private float YawAt(DateTime now) {
        float blend = Progress(now);
        float delta = (Latest.Yaw - _fromYaw) % 360f;
        if (delta > 180f) {
            delta -= 360f;
        }
        if (delta < -180f) {
            delta += 360f;
        }
        return _fromYaw + delta * blend;
    }

    private float Progress(DateTime now) {
        double progress = (now - _receivedAt).TotalMilliseconds / BlendTime.TotalMilliseconds;
        return (float)Math.Clamp(progress, 0, 1);
    }

    private void SampleTrail(DateTime now) {
        if (!Latest.IsAlive) {
            _trail.Clear();
            return;
        }
        if (now - _lastTrailSample >= TrailSampleInterval) {
            _trail.Enqueue((now, Latest.Origin));
            _lastTrailSample = now;
        }
        while (_trail.Count > 0 && now - _trail.Peek().At > TrailLength) {
            _trail.Dequeue();
        }
    }
}
