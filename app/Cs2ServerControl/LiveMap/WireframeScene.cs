using Cs2ServerControl.Services;
using HelixToolkit.SharpDX.Core;
using SharpDX;
using MediaColor = System.Windows.Media.Color;

namespace Cs2ServerControl.LiveMap;

/// <summary>
/// Turns a BSP wireframe into Helix line geometry: one geometry for all map
/// edges, colored by height so the levels read apart in a pure wireframe,
/// and one for the floor grid.
/// </summary>
public static class WireframeScene {
    public const float GridSpacing = 256f;

    public static LineGeometry3D BuildMapLines(BspWireframe wireframe, MediaColor lowColor, MediaColor highColor) {
        var positions = new Vector3Collection(wireframe.Segments.Count * 2);
        var indices = new IntCollection(wireframe.Segments.Count * 2);
        var colors = new Color4Collection(wireframe.Segments.Count * 2);

        float heightRange = Math.Max(1f, wireframe.MaxZ - wireframe.MinZ);
        Color4 low = ToColor4(lowColor, 1f);
        Color4 high = ToColor4(highColor, 1f);

        foreach ((System.Numerics.Vector3 start, System.Numerics.Vector3 end) in wireframe.Segments) {
            indices.Add(positions.Count);
            positions.Add(new Vector3(start.X, start.Y, start.Z));
            colors.Add(Color4.Lerp(low, high, (start.Z - wireframe.MinZ) / heightRange));

            indices.Add(positions.Count);
            positions.Add(new Vector3(end.X, end.Y, end.Z));
            colors.Add(Color4.Lerp(low, high, (end.Z - wireframe.MinZ) / heightRange));
        }

        return new LineGeometry3D {
            Positions = positions,
            Indices = indices,
            Colors = colors,
        };
    }

    /// <summary>A 256 unit grid over the map bounds on the lowest floor level.</summary>
    public static LineGeometry3D BuildFloorGrid(BspWireframe wireframe) {
        float minX = MathF.Floor(wireframe.BoundsMin.X / GridSpacing) * GridSpacing;
        float maxX = MathF.Ceiling(wireframe.BoundsMax.X / GridSpacing) * GridSpacing;
        float minY = MathF.Floor(wireframe.BoundsMin.Y / GridSpacing) * GridSpacing;
        float maxY = MathF.Ceiling(wireframe.BoundsMax.Y / GridSpacing) * GridSpacing;
        float z = wireframe.MinZ;

        var builder = new LineBuilder();
        for (float x = minX; x <= maxX; x += GridSpacing) {
            builder.AddLine(new Vector3(x, minY, z), new Vector3(x, maxY, z));
        }
        for (float y = minY; y <= maxY; y += GridSpacing) {
            builder.AddLine(new Vector3(minX, y, z), new Vector3(maxX, y, z));
        }
        return builder.ToLineGeometry3D();
    }

    /// <summary>
    /// The nav mesh debug view: area outlines, links between area centers and
    /// hiding spots. Lifted a little, so the outlines do not flicker inside
    /// the floor edges they lie on.
    /// </summary>
    public static (LineGeometry3D Areas, LineGeometry3D? Links, PointGeometry3D? Spots) BuildNavMesh(NavMesh mesh) {
        var lift = new Vector3(0, 0, 3);
        var areaBuilder = new LineBuilder();
        var centers = new Dictionary<uint, Vector3>();
        foreach (NavArea area in mesh.Areas) {
            Vector3 northWest = ToVector(area.NorthWest) + lift;
            Vector3 northEast = ToVector(area.NorthEast) + lift;
            Vector3 southEast = ToVector(area.SouthEast) + lift;
            Vector3 southWest = ToVector(area.SouthWest) + lift;
            areaBuilder.AddLine(northWest, northEast);
            areaBuilder.AddLine(northEast, southEast);
            areaBuilder.AddLine(southEast, southWest);
            areaBuilder.AddLine(southWest, northWest);
            centers[area.Id] = ToVector(area.Center) + lift;
        }

        // Each link once, even when both areas list each other.
        var linkBuilder = new LineBuilder();
        var drawn = new HashSet<(uint, uint)>();
        foreach ((uint from, uint to) in mesh.Connections) {
            (uint, uint) key = from < to ? (from, to) : (to, from);
            if (!drawn.Add(key) || !centers.TryGetValue(from, out Vector3 start) || !centers.TryGetValue(to, out Vector3 end)) {
                continue;
            }
            linkBuilder.AddLine(start, end);
        }

        PointGeometry3D? spots = null;
        if (mesh.HidingSpots.Count > 0) {
            var positions = new Vector3Collection(mesh.HidingSpots.Select(spot => ToVector(spot) + lift));
            spots = new PointGeometry3D { Positions = positions };
        }

        return (areaBuilder.ToLineGeometry3D(), drawn.Count > 0 ? linkBuilder.ToLineGeometry3D() : null, spots);
    }

    private static Vector3 ToVector(System.Numerics.Vector3 vector) {
        return new Vector3(vector.X, vector.Y, vector.Z);
    }

    public static Color4 ToColor4(MediaColor color, float opacity) {
        return new Color4(color.R / 255f, color.G / 255f, color.B / 255f, opacity);
    }

    /// <summary>
    /// Mixes the color into the viewport background instead of using alpha.
    /// The background is one flat color, so the result looks the same as
    /// real transparency, without depth sorting problems between lines.
    /// </summary>
    public static Color4 Blend(MediaColor color, MediaColor background, float opacity) {
        return Color4.Lerp(ToColor4(background, 1f), ToColor4(color, 1f), opacity);
    }
}
