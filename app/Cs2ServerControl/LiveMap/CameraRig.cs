using System.Numerics;

namespace Cs2ServerControl.LiveMap;

public readonly record struct CameraPose(Vector3 Position, Vector3 LookDirection, Vector3 UpDirection);

/// <summary>
/// An orbit camera around a target point: yaw, pitch and a zoom factor of
/// the distance that fits the whole map. Toolbar actions animate over 200 ms,
/// mouse and keyboard input applies at once. Z is up, like in the Source engine.
/// </summary>
public sealed class CameraRig {
    public const float DefaultYaw = 35f;
    public const float DefaultPitch = 50f;
    public const float MinPitch = 10f;
    public const float MaxPitch = 90f;
    public const float MinZoom = 0.15f;
    public const float MaxZoom = 2.5f;
    public const float FieldOfViewDegrees = 45f;

    private const float AnimationSeconds = 0.2f;

    // Straight down the whole map has to fit the viewport height, which is
    // the short side, so the top view starts a little further out.
    private const float TopViewZoom = 1.2f;

    private State _current = new(Vector3.Zero, DefaultYaw, DefaultPitch, 1f);
    private State _from;
    private State _to;
    private float _animationProgress = 1f;
    private bool _changed = true;

    public CameraRig() {
        _from = _current;
        _to = _current;
    }

    public float FitDistance { get; private set; } = 4000f;
    public Vector3 MapCenter { get; private set; }
    public float MapRadius { get; private set; } = 2000f;

    public Vector3 Target => _current.Target;
    public float Yaw => _current.Yaw;
    public float Pitch => _current.Pitch;
    public float Zoom => _current.Zoom;
    public float Distance => FitDistance * _current.Zoom;
    public bool IsTopView => Goal.Pitch >= MaxPitch - 0.01f;

    /// <summary>Points the camera at the map's bounding box from the default angle.</summary>
    public void Fit(Vector3 boundsMin, Vector3 boundsMax, bool topView) {
        MapCenter = (boundsMin + boundsMax) / 2;
        MapRadius = Math.Max(256f, Vector3.Distance(boundsMin, boundsMax) / 2);
        float halfFov = FieldOfViewDegrees / 2 * MathF.PI / 180f;
        // A bounding sphere fit is very conservative for a flat, wide map seen
        // from above; 0.75 of it fills the viewport without cutting corners off.
        FitDistance = MapRadius / MathF.Sin(halfFov) * 0.75f;

        _current = topView
            ? new State(MapCenter, 0f, MaxPitch, TopViewZoom)
            : new State(MapCenter, DefaultYaw, DefaultPitch, 1f);
        _to = _current;
        _animationProgress = 1f;
        _changed = true;
    }

    public void RotateBy(float degrees, bool animate) {
        Apply(Goal with { Yaw = NormalizeYaw(Goal.Yaw + degrees) }, animate);
    }

    public void PitchBy(float degrees, bool animate) {
        Apply(Goal with { Pitch = Math.Clamp(Goal.Pitch + degrees, MinPitch, MaxPitch) }, animate);
    }

    public void ZoomBy(float delta, bool animate) {
        Apply(Goal with { Zoom = Math.Clamp(Goal.Zoom + delta, MinZoom, MaxZoom) }, animate);
    }

    /// <summary>Multiplies the zoom, which feels even on the mouse wheel at any distance.</summary>
    public void ZoomScale(float factor) {
        Apply(Goal with { Zoom = Math.Clamp(Goal.Zoom * factor, MinZoom, MaxZoom) }, animate: false);
    }

    /// <summary>
    /// Moves the target over the ground: right and forward relative to the
    /// view direction, in world units. Height stays, so the camera glides.
    /// </summary>
    public void Move(float right, float forward, bool animate) {
        float yaw = Goal.Yaw * MathF.PI / 180f;
        var rightAxis = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0);
        var forwardAxis = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0);
        Vector3 target = Goal.Target + rightAxis * right + forwardAxis * forward;
        Apply(Goal with { Target = ClampToMap(target) }, animate);
    }

    /// <summary>Moves by screen pixels, so a drag keeps the map under the mouse.</summary>
    public void MoveByPixels(double deltaX, double deltaY, double viewportHeight) {
        if (viewportHeight <= 0) {
            return;
        }
        float unitsPerPixel = 2 * Distance * MathF.Tan(FieldOfViewDegrees / 2 * MathF.PI / 180f) / (float)viewportHeight;
        // Looking straight down, screen up is forward; at a flat angle a vertical
        // drag covers more ground, which sin(pitch) compensates.
        float pitchFactor = MathF.Max(0.3f, MathF.Sin(Pitch * MathF.PI / 180f));
        Move((float)-deltaX * unitsPerPixel, (float)deltaY * unitsPerPixel / pitchFactor, animate: false);
    }

    public void OrbitByPixels(double deltaX, double deltaY) {
        const float DegreesPerPixel = 0.3f;
        Apply(Goal with {
            Yaw = NormalizeYaw(Goal.Yaw - (float)deltaX * DegreesPerPixel),
            Pitch = Math.Clamp(Goal.Pitch + (float)deltaY * DegreesPerPixel, MinPitch, MaxPitch),
        }, animate: false);
    }

    public void SetTopView(bool animate) {
        Apply(Goal with { Yaw = 0f, Pitch = MaxPitch }, animate);
    }

    public void SetPerspective(bool animate) {
        Apply(Goal with { Yaw = DefaultYaw, Pitch = DefaultPitch }, animate);
    }

    public void Reset(bool topView) {
        var reset = topView
            ? new State(MapCenter, 0f, MaxPitch, TopViewZoom)
            : new State(MapCenter, DefaultYaw, DefaultPitch, 1f);
        Apply(reset, animate: true);
    }

    /// <summary>Follow mode: glides the target to the player, keeps angle and distance.</summary>
    public void FollowTowards(Vector3 point, float deltaSeconds) {
        float blend = 1f - MathF.Exp(-deltaSeconds * 8f);
        Vector3 target = Vector3.Lerp(_current.Target, point, blend);
        _current = _current with { Target = target };
        _to = _to with { Target = target };
        _changed = true;
    }

    /// <summary>
    /// Takes over a camera that something else moved, the ViewCube for
    /// example, so the next rig action starts from what is on screen.
    /// </summary>
    public void Adopt(Vector3 position, Vector3 lookDirection) {
        float distance = lookDirection.Length();
        if (distance < 1f || FitDistance <= 0) {
            return;
        }

        Vector3 toCamera = -lookDirection / distance;
        float pitch = MathF.Asin(Math.Clamp(toCamera.Z, -1f, 1f)) * 180f / MathF.PI;
        float yaw = MathF.Atan2(toCamera.X, -toCamera.Y) * 180f / MathF.PI;
        bool isStraightDown = MathF.Abs(toCamera.X) < 1e-4f && MathF.Abs(toCamera.Y) < 1e-4f;
        if (isStraightDown) {
            yaw = _current.Yaw;
        }

        _current = new State(position + lookDirection, NormalizeYaw(yaw), Math.Clamp(pitch, MinPitch, MaxPitch),
            Math.Clamp(distance / FitDistance, MinZoom, MaxZoom));
        _to = _current;
        _animationProgress = 1f;
        _changed = true;
    }

    /// <summary>Advances the animation. True when the pose changed since the last call.</summary>
    public bool Update(float deltaSeconds) {
        if (_animationProgress < 1f) {
            _animationProgress = Math.Min(1f, _animationProgress + deltaSeconds / AnimationSeconds);
            float eased = EaseOut(_animationProgress);
            _current = new State(
                Vector3.Lerp(_from.Target, _to.Target, eased),
                NormalizeYaw(_from.Yaw + ShortestYawDelta(_from.Yaw, _to.Yaw) * eased),
                _from.Pitch + (_to.Pitch - _from.Pitch) * eased,
                _from.Zoom + (_to.Zoom - _from.Zoom) * eased);
            _changed = true;
        }

        bool changed = _changed;
        _changed = false;
        return changed;
    }

    public CameraPose GetPose() {
        float yaw = _current.Yaw * MathF.PI / 180f;
        float pitch = _current.Pitch * MathF.PI / 180f;
        var toCamera = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch));
        Vector3 position = _current.Target + toCamera * Distance;
        Vector3 look = _current.Target - position;

        // Straight down, Z up is parallel to the view and the camera would
        // flip; the horizontal view direction is a stable "up" instead.
        Vector3 up = _current.Pitch >= MaxPitch - 0.5f
            ? new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0)
            : Vector3.UnitZ;
        return new CameraPose(position, look, up);
    }

    private State Goal => _animationProgress < 1f ? _to : _current;

    private void Apply(State goal, bool animate) {
        if (animate) {
            _from = _current;
            _to = goal;
            _animationProgress = 0f;
        } else {
            _current = goal;
            _to = goal;
            _animationProgress = 1f;
        }
        _changed = true;
    }

    private Vector3 ClampToMap(Vector3 target) {
        // Free movement, but never so far away that the map is lost.
        Vector3 offset = target - MapCenter;
        float limit = MapRadius * 1.5f;
        if (offset.Length() <= limit) {
            return target;
        }
        return MapCenter + Vector3.Normalize(offset) * limit;
    }

    private static float EaseOut(float progress) {
        return 1f - (1f - progress) * (1f - progress);
    }

    private static float NormalizeYaw(float yaw) {
        yaw %= 360f;
        if (yaw < 0) {
            yaw += 360f;
        }
        return yaw;
    }

    private static float ShortestYawDelta(float from, float to) {
        float delta = (to - from) % 360f;
        if (delta > 180f) {
            delta -= 360f;
        }
        if (delta < -180f) {
            delta += 360f;
        }
        return delta;
    }

    private readonly record struct State(Vector3 Target, float Yaw, float Pitch, float Zoom);
}
