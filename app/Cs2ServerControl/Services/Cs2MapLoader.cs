using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.RubikonPhysics;
using CollisionMesh = ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Mesh;
using CollisionHull = ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Hull;

namespace Cs2ServerControl.Services;

/// <summary>
/// Reads the wireframe of a CS2 map from its .vpk: the world collision
/// (maps\&lt;map&gt;\world_physics.vmdl_c), which is the solid geometry players
/// walk on without the decoration. Clips, sky and grenade clips have their
/// own collision groups and are left out. Of the triangle meshes only the
/// feature edges stay: borders and creases, not the diagonals across flat
/// floors. The convex hulls (crates, walls of props) bring their own edges.
/// </summary>
public static class Cs2MapLoader {
    // Two faces that meet flatter than this are one surface for the eye.
    private static readonly float CreaseCosine = MathF.Cos(20f * MathF.PI / 180f);
    // Collision vertices of neighbouring pieces do not always line up exactly.
    private const float WeldGrid = 0.25f;
    private const string WorldGroup = "default";

    private static readonly ConcurrentDictionary<string, (DateTime Timestamp, MapWireframe Wireframe)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads on a background thread, cached per file and timestamp.</summary>
    public static Task<MapWireframe> LoadAsync(string packageFile, string mapName) {
        return Task.Run(() => Load(packageFile, mapName));
    }

    /// <summary>
    /// The package is Valve's map file or a downloaded Workshop item, which
    /// carries the map package inside. Throws InvalidDataException when there
    /// is no world collision.
    /// </summary>
    public static MapWireframe Load(string packageFile, string mapName) {
        string cacheKey = packageFile + "|" + mapName;
        DateTime timestamp = File.GetLastWriteTimeUtc(packageFile);
        if (Cache.TryGetValue(cacheKey, out var cached) && cached.Timestamp == timestamp) {
            return cached.Wireframe;
        }

        PhysAggregateData physics = ReadWorldPhysics(packageFile, mapName);
        MapWireframe wireframe = BuildWireframe(mapName, physics);
        Cache[cacheKey] = (timestamp, wireframe);
        return wireframe;
    }

    private static PhysAggregateData ReadWorldPhysics(string packageFile, string mapName) {
        using var outer = new Package();
        outer.Read(packageFile);
        string physicsPath = $"maps/{mapName}/world_physics.vmdl_c";
        byte[] bytes;
        PackageEntry? entry = outer.FindEntry(physicsPath);
        if (entry != null) {
            outer.ReadEntry(entry, out bytes);
        } else {
            bytes = ReadFromInnerMapPackage(outer, mapName, physicsPath);
        }
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        if (resource.DataBlock is not Model model || model.GetEmbeddedPhys() is not PhysAggregateData physics) {
            throw new InvalidDataException($"The world collision of {mapName} could not be read.");
        }
        return physics;
    }

    /// <summary>A Workshop item packs maps\&lt;map&gt;.vpk; that inner package holds the world collision.</summary>
    private static byte[] ReadFromInnerMapPackage(Package outer, string mapName, string physicsPath) {
        PackageEntry? mapEntry = outer.FindEntry($"maps/{mapName}.vpk");
        if (mapEntry == null) {
            throw new InvalidDataException($"{mapName} has no world collision.");
        }
        outer.ReadEntry(mapEntry, out byte[] mapPackage);

        using var inner = new Package();
        inner.SetFileName(mapName + ".vpk");
        inner.Read(new MemoryStream(mapPackage));
        PackageEntry? entry = inner.FindEntry(physicsPath);
        if (entry == null) {
            throw new InvalidDataException($"{mapName} has no world collision.");
        }
        inner.ReadEntry(entry, out byte[] bytes);
        return bytes;
    }

    private static MapWireframe BuildWireframe(string mapName, PhysAggregateData physics) {
        IReadOnlySet<int> worldAttributes = FindWorldAttributes(physics);
        var segments = new List<(Vector3 Start, Vector3 End)>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (Part part in physics.Parts) {
            foreach (var mesh in part.Shape.Meshes) {
                if (worldAttributes.Contains(mesh.CollisionAttributeIndex)) {
                    AddFeatureEdges(mesh.Shape, segments);
                }
            }
            foreach (var hull in part.Shape.Hulls) {
                if (worldAttributes.Contains(hull.CollisionAttributeIndex)) {
                    AddHullEdges(hull.Shape, segments);
                }
            }
        }

        foreach ((Vector3 start, Vector3 end) in segments) {
            min = Vector3.Min(min, Vector3.Min(start, end));
            max = Vector3.Max(max, Vector3.Max(start, end));
        }
        if (segments.Count == 0) {
            throw new InvalidDataException($"The world collision of {mapName} has no solid geometry.");
        }
        return new MapWireframe(mapName, segments, min, max);
    }

    /// <summary>
    /// The collision groups of plain solid world: group "Default" that does
    /// not only act as a clip or as sky. Valve's maps name these in
    /// m_InteractAsStrings (playerclip, sky, passbullets, csgo_grenadeclip).
    /// </summary>
    private static IReadOnlySet<int> FindWorldAttributes(PhysAggregateData physics) {
        var indices = new HashSet<int>();
        for (int index = 0; index < physics.CollisionAttributes.Count; index++) {
            var attribute = physics.CollisionAttributes[index];
            string group = attribute.Children.FirstOrDefault(child => child.Key == "m_CollisionGroupString").Value?.ToString() ?? "";
            bool hasSpecialRole = attribute.Children
                .Where(child => child.Key is "m_InteractAsStrings" or "m_InteractExcludeStrings")
                .Any(child => child.Value.Children.Any());
            if (string.Equals(group, WorldGroup, StringComparison.OrdinalIgnoreCase) && !hasSpecialRole) {
                indices.Add(index);
            }
        }
        return indices;
    }

    private static void AddFeatureEdges(CollisionMesh mesh, List<(Vector3 Start, Vector3 End)> segments) {
        ReadOnlySpan<Vector3> vertices = mesh.GetVertices();
        int[] weldedIndex = WeldVertices(vertices, out List<Vector3> welded);

        // Per edge: the normal of the first triangle and whether it is a crease or border.
        var edges = new Dictionary<(int Low, int High), (Vector3 Normal, int Count, bool IsFeature)>();
        foreach (var triangle in mesh.GetTriangles()) {
            int a = weldedIndex[triangle.X];
            int b = weldedIndex[triangle.Y];
            int c = weldedIndex[triangle.Z];
            if (a == b || b == c || a == c) {
                continue;
            }
            Vector3 normal = Vector3.Cross(welded[b] - welded[a], welded[c] - welded[a]);
            if (normal.LengthSquared() < 1e-6f) {
                continue;
            }
            normal = Vector3.Normalize(normal);
            AddTriangleEdge(edges, a, b, normal);
            AddTriangleEdge(edges, b, c, normal);
            AddTriangleEdge(edges, c, a, normal);
        }

        foreach (((int low, int high), (Vector3 _, int count, bool isFeature)) in edges) {
            bool isBorder = count == 1;
            if (isFeature || isBorder) {
                segments.Add((welded[low], welded[high]));
            }
        }
    }

    private static void AddTriangleEdge(Dictionary<(int Low, int High), (Vector3 Normal, int Count, bool IsFeature)> edges, int from, int to, Vector3 normal) {
        // A tuple key, not a packed long: long hashes as low ^ high, and neighbouring
        // vertex indices would then pile up in a few buckets.
        (int Low, int High) key = from < to ? (from, to) : (to, from);
        if (!edges.TryGetValue(key, out var edge)) {
            edges[key] = (normal, 1, false);
            return;
        }
        // Normals of both orientations count as flat: a thin wall seen from both sides is not a crease.
        bool isCrease = MathF.Abs(Vector3.Dot(edge.Normal, normal)) < CreaseCosine;
        edges[key] = (edge.Normal, edge.Count + 1, edge.IsFeature || isCrease);
    }

    private static int[] WeldVertices(ReadOnlySpan<Vector3> vertices, out List<Vector3> welded) {
        var indexByCell = new Dictionary<(int, int, int), int>();
        var remap = new int[vertices.Length];
        welded = new List<Vector3>(vertices.Length);
        for (int index = 0; index < vertices.Length; index++) {
            Vector3 vertex = vertices[index];
            var cell = ((int)MathF.Round(vertex.X / WeldGrid), (int)MathF.Round(vertex.Y / WeldGrid), (int)MathF.Round(vertex.Z / WeldGrid));
            if (!indexByCell.TryGetValue(cell, out int weldedIndex)) {
                weldedIndex = welded.Count;
                welded.Add(vertex);
                indexByCell[cell] = weldedIndex;
            }
            remap[index] = weldedIndex;
        }
        return remap;
    }

    /// <summary>Every edge of a convex hull once: a half-edge and its twin are the same line.</summary>
    private static void AddHullEdges(CollisionHull hull, List<(Vector3 Start, Vector3 End)> segments) {
        ReadOnlySpan<Vector3> positions = hull.GetVertexPositions();
        ReadOnlySpan<CollisionHull.HalfEdge> halfEdges = hull.GetEdges();
        for (int index = 0; index < halfEdges.Length; index++) {
            CollisionHull.HalfEdge edge = halfEdges[index];
            if (edge.Twin < index) {
                continue;
            }
            CollisionHull.HalfEdge twin = halfEdges[edge.Twin];
            segments.Add((positions[edge.Origin], positions[twin.Origin]));
        }
    }
}
