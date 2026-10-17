using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Text;

namespace Cs2ServerControl.Services;

public sealed record BspWireframe(
    string MapName,
    IReadOnlyList<(Vector3 Start, Vector3 End)> Segments,
    Vector3 BoundsMin,
    Vector3 BoundsMax) {
    public Vector3 Center => (BoundsMin + BoundsMax) / 2;
    public float MinZ => BoundsMin.Z;
    public float MaxZ => BoundsMax.Z;
}

public sealed class BspFormatException(string message) : Exception(message);

/// <summary>
/// Reads the world geometry (model 0) of a GoldSrc BSP version 30 file as
/// unique line segments. Tool textures (sky, triggers, clips) are skipped,
/// because they are invisible in game and only clutter the wireframe.
/// </summary>
public static class BspWireframeLoader {
    private const int BspVersion = 30;
    private const int LumpCount = 15;

    private const int LumpTextures = 2;
    private const int LumpVertices = 3;
    private const int LumpTexInfo = 6;
    private const int LumpFaces = 7;
    private const int LumpEdges = 12;
    private const int LumpSurfEdges = 13;
    private const int LumpModels = 14;

    private const int VertexSize = 12;
    private const int EdgeSize = 4;
    private const int FaceSize = 20;
    private const int TexInfoSize = 40;
    private const int ModelSize = 64;

    private static readonly string[] SkippedTexturePrefixes = ["aaatrigger", "clip", "origin", "null", "bevel"];

    private static readonly ConcurrentDictionary<string, (DateTime Timestamp, BspWireframe Wireframe)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads on a background thread, cached per file and timestamp.</summary>
    public static Task<BspWireframe> LoadAsync(string bspFile) {
        return Task.Run(() => Load(bspFile));
    }

    public static BspWireframe Load(string bspFile) {
        DateTime timestamp = File.GetLastWriteTimeUtc(bspFile);
        if (Cache.TryGetValue(bspFile, out var cached) && cached.Timestamp == timestamp) {
            return cached.Wireframe;
        }

        BspWireframe wireframe = Parse(File.ReadAllBytes(bspFile), Path.GetFileNameWithoutExtension(bspFile));
        Cache[bspFile] = (timestamp, wireframe);
        return wireframe;
    }

    internal static BspWireframe Parse(byte[] data, string mapName) {
        if (data.Length < 4 + LumpCount * 8) {
            throw new BspFormatException("The file is too short for a BSP header.");
        }

        int version = BitConverter.ToInt32(data, 0);
        if (version != BspVersion) {
            throw new BspFormatException($"BSP version {version} is not a GoldSrc map (30 expected).");
        }

        var lumps = new (int Offset, int Length)[LumpCount];
        for (int index = 0; index < LumpCount; index++) {
            int offset = BitConverter.ToInt32(data, 4 + index * 8);
            int length = BitConverter.ToInt32(data, 8 + index * 8);
            if (offset < 0 || length < 0 || (long)offset + length > data.Length) {
                throw new BspFormatException($"Lump {index} points outside the file.");
            }
            lumps[index] = (offset, length);
        }

        Vector3[] vertices = ReadVertices(data, lumps[LumpVertices]);
        string[] textureNames = ReadTextureNames(data, lumps[LumpTextures]);
        (int FirstFace, int FaceCount) world = ReadWorldModel(data, lumps[LumpModels]);

        var edgeIndices = new HashSet<int>();
        (int surfEdgeOffset, int surfEdgeLength) = lumps[LumpSurfEdges];
        int surfEdgeCount = surfEdgeLength / 4;
        int texInfoCount = lumps[LumpTexInfo].Length / TexInfoSize;
        int faceCount = lumps[LumpFaces].Length / FaceSize;

        for (int faceIndex = world.FirstFace; faceIndex < world.FirstFace + world.FaceCount && faceIndex < faceCount; faceIndex++) {
            int faceOffset = lumps[LumpFaces].Offset + faceIndex * FaceSize;
            int firstEdge = BitConverter.ToInt32(data, faceOffset + 4);
            int edgeCount = BitConverter.ToUInt16(data, faceOffset + 8);
            int texInfo = BitConverter.ToUInt16(data, faceOffset + 10);

            if (IsToolTexture(texInfo, texInfoCount, data, lumps[LumpTexInfo].Offset, textureNames)) {
                continue;
            }

            for (int edge = firstEdge; edge < firstEdge + edgeCount && edge < surfEdgeCount; edge++) {
                int surfEdge = BitConverter.ToInt32(data, surfEdgeOffset + edge * 4);
                edgeIndices.Add(Math.Abs(surfEdge));
            }
        }

        var segments = new List<(Vector3, Vector3)>(edgeIndices.Count);
        var boundsMin = new Vector3(float.MaxValue);
        var boundsMax = new Vector3(float.MinValue);
        int edgeTotal = lumps[LumpEdges].Length / EdgeSize;

        foreach (int edgeIndex in edgeIndices) {
            if (edgeIndex >= edgeTotal) {
                continue;
            }
            int edgeOffset = lumps[LumpEdges].Offset + edgeIndex * EdgeSize;
            int first = BitConverter.ToUInt16(data, edgeOffset);
            int second = BitConverter.ToUInt16(data, edgeOffset + 2);
            if (first >= vertices.Length || second >= vertices.Length) {
                continue;
            }

            Vector3 start = vertices[first];
            Vector3 end = vertices[second];
            segments.Add((start, end));
            boundsMin = Vector3.Min(boundsMin, Vector3.Min(start, end));
            boundsMax = Vector3.Max(boundsMax, Vector3.Max(start, end));
        }

        if (segments.Count == 0) {
            throw new BspFormatException("The map has no visible world geometry.");
        }
        return new BspWireframe(mapName, segments, boundsMin, boundsMax);
    }

    private static bool IsToolTexture(int texInfo, int texInfoCount, byte[] data, int texInfoLumpOffset, string[] textureNames) {
        if (texInfo >= texInfoCount) {
            return false;
        }

        // The miptex index sits after the two texture axis vectors (8 floats).
        int mipTex = BitConverter.ToInt32(data, texInfoLumpOffset + texInfo * TexInfoSize + 32);
        if (mipTex < 0 || mipTex >= textureNames.Length) {
            return false;
        }

        string name = textureNames[mipTex];
        if (name == "sky") {
            return true;
        }
        return SkippedTexturePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static Vector3[] ReadVertices(byte[] data, (int Offset, int Length) lump) {
        var vertices = new Vector3[lump.Length / VertexSize];
        for (int index = 0; index < vertices.Length; index++) {
            int offset = lump.Offset + index * VertexSize;
            vertices[index] = new Vector3(
                BitConverter.ToSingle(data, offset),
                BitConverter.ToSingle(data, offset + 4),
                BitConverter.ToSingle(data, offset + 8));
        }
        return vertices;
    }

    private static string[] ReadTextureNames(byte[] data, (int Offset, int Length) lump) {
        if (lump.Length < 4) {
            return [];
        }

        int count = BitConverter.ToInt32(data, lump.Offset);
        if (count < 0 || 4 + (long)count * 4 > lump.Length) {
            return [];
        }

        var names = new string[count];
        for (int index = 0; index < count; index++) {
            int relative = BitConverter.ToInt32(data, lump.Offset + 4 + index * 4);
            if (relative < 0 || relative + 16 > lump.Length) {
                names[index] = "";
                continue;
            }

            int nameStart = lump.Offset + relative;
            int nameLength = Array.IndexOf(data, (byte)0, nameStart, 16) - nameStart;
            if (nameLength < 0) {
                nameLength = 16;
            }
            names[index] = Encoding.ASCII.GetString(data, nameStart, nameLength).ToLowerInvariant();
        }
        return names;
    }

    private static (int FirstFace, int FaceCount) ReadWorldModel(byte[] data, (int Offset, int Length) lump) {
        if (lump.Length < ModelSize) {
            throw new BspFormatException("The map has no world model.");
        }

        // mins, maxs, origin (9 floats), headnodes (4 ints), visleafs, then the faces.
        int firstFace = BitConverter.ToInt32(data, lump.Offset + 56);
        int faceCount = BitConverter.ToInt32(data, lump.Offset + 60);
        return (firstFace, faceCount);
    }
}
