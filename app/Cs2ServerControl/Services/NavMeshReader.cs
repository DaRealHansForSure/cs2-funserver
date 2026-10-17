using System.IO;
using System.Numerics;

namespace Cs2ServerControl.Services;

public sealed record NavArea(uint Id, Vector3 NorthWest, Vector3 NorthEast, Vector3 SouthEast, Vector3 SouthWest) {
    public Vector3 Center => (NorthWest + SouthEast) / 2;
}

public sealed record NavMesh(
    IReadOnlyList<NavArea> Areas,
    IReadOnlyList<(uint From, uint To)> Connections,
    IReadOnlyList<Vector3> HidingSpots);

/// <summary>
/// Reads the areas, their connections and the hiding spots of a zBot
/// navigation file (maps\&lt;map&gt;.nav), for the debug view on the live map.
/// The layout follows ReGameDLL's nav_file.cpp (CNavArea::Load), versions 1 to 5.
/// </summary>
public static class NavMeshReader {
    private const uint MagicNumber = 0xFEEDFACE;
    private const uint NewestVersion = 5;
    private const int DirectionCount = 4;

    /// <summary>
    /// The size of the .bsp the mesh was built for (version 4 and newer), or
    /// null for older files and anything unreadable. A different size than
    /// the installed .bsp means the mesh belongs to another build of the map.
    /// </summary>
    public static long? ReadBspSize(string navFile) {
        try {
            using var stream = File.OpenRead(navFile);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != MagicNumber) {
                return null;
            }
            uint version = reader.ReadUInt32();
            if (version < 4 || version > NewestVersion) {
                return null;
            }
            return reader.ReadUInt32();
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    public static NavMesh Load(string navFile) {
        using var stream = File.OpenRead(navFile);
        using var reader = new BinaryReader(stream);
        return Read(reader);
    }

    internal static NavMesh Read(BinaryReader reader) {
        try {
            if (reader.ReadUInt32() != MagicNumber) {
                throw new InvalidDataException("Not a navigation file.");
            }
            uint version = reader.ReadUInt32();
            if (version == 0 || version > NewestVersion) {
                throw new InvalidDataException($"Unknown navigation file version {version}.");
            }
            if (version >= 4) {
                reader.ReadUInt32(); // Size of the bsp the mesh was built for.
            }
            if (version >= 5) {
                SkipPlaceDirectory(reader);
            }

            uint areaCount = reader.ReadUInt32();
            var areas = new List<NavArea>((int)Math.Min(areaCount, 100_000));
            var connections = new List<(uint, uint)>();
            var hidingSpots = new List<Vector3>();
            for (uint index = 0; index < areaCount; index++) {
                areas.Add(ReadArea(reader, version, connections, hidingSpots));
            }
            return new NavMesh(areas, connections, hidingSpots);
        } catch (EndOfStreamException) {
            throw new InvalidDataException("The navigation file ends too early.");
        }
    }

    private static void SkipPlaceDirectory(BinaryReader reader) {
        ushort count = reader.ReadUInt16();
        for (int index = 0; index < count; index++) {
            ushort length = reader.ReadUInt16();
            reader.ReadBytes(length);
        }
    }

    private static NavArea ReadArea(BinaryReader reader, uint version, List<(uint, uint)> connections, List<Vector3> hidingSpots) {
        uint id = reader.ReadUInt32();
        reader.ReadByte(); // Attribute flags (crouch, jump, ...).

        Vector3 low = ReadVector(reader);
        Vector3 high = ReadVector(reader);
        float northEastZ = reader.ReadSingle();
        float southWestZ = reader.ReadSingle();

        for (int direction = 0; direction < DirectionCount; direction++) {
            uint count = reader.ReadUInt32();
            for (uint index = 0; index < count; index++) {
                connections.Add((id, reader.ReadUInt32()));
            }
        }

        byte hidingSpotCount = reader.ReadByte();
        for (int index = 0; index < hidingSpotCount; index++) {
            if (version == 1) {
                hidingSpots.Add(ReadVector(reader));
                continue;
            }
            reader.ReadUInt32(); // Spot id.
            hidingSpots.Add(ReadVector(reader));
            reader.ReadByte(); // Spot flags.
        }

        // Approach areas: here, prev, how, next, how.
        byte approachCount = reader.ReadByte();
        reader.ReadBytes(approachCount * (4 + 4 + 1 + 4 + 1));

        uint encounterCount = reader.ReadUInt32();
        if (version < 3) {
            for (uint index = 0; index < encounterCount; index++) {
                reader.ReadBytes(4 + 4 + 12 + 12);
                byte spotCount = reader.ReadByte();
                reader.ReadBytes(spotCount * 16);
            }
        } else {
            for (uint index = 0; index < encounterCount; index++) {
                reader.ReadBytes(4 + 1 + 4 + 1);
                byte spotCount = reader.ReadByte();
                reader.ReadBytes(spotCount * 5);
            }
        }

        if (version >= 5) {
            reader.ReadUInt16(); // Place entry.
        }

        // The four corners: the extent gives NW and SE, the stored heights the other two.
        return new NavArea(
            id,
            new Vector3(low.X, low.Y, low.Z),
            new Vector3(high.X, low.Y, northEastZ),
            new Vector3(high.X, high.Y, high.Z),
            new Vector3(low.X, high.Y, southWestZ));
    }

    private static Vector3 ReadVector(BinaryReader reader) {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
