using System.Numerics;

namespace Cs2ServerControl.Services;

/// <summary>A map as unique line segments, what the Live Map and the overview draw.</summary>
public sealed record MapWireframe(
    string MapName,
    IReadOnlyList<(Vector3 Start, Vector3 End)> Segments,
    Vector3 BoundsMin,
    Vector3 BoundsMax) {
    public Vector3 Center => (BoundsMin + BoundsMax) / 2;
    public float MinZ => BoundsMin.Z;
    public float MaxZ => BoundsMax.Z;
}
