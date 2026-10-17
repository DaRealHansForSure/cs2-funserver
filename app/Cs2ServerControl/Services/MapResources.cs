using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Cs2ServerControl.Services;

/// <summary>
/// The models and sprites a map's entities name. The server loads every one
/// of them when the map starts, and a missing one stops it with an engine
/// error (on Windows a message box, so the server just hangs). Sounds and
/// textures are only needed by the players' games, not by the server.
/// </summary>
public static partial class MapResources {
    private const int EntitiesLump = 0;

    /// <summary>Relative paths like "sprites/explode.spr", lower case with forward slashes, without duplicates.</summary>
    public static IReadOnlyList<string> ReadRequiredFiles(string bspFile) {
        string? entities = ReadEntityText(bspFile);
        if (entities == null) {
            return [];
        }
        return ParseRequiredFiles(entities);
    }

    internal static IReadOnlyList<string> ParseRequiredFiles(string entityText) {
        return ResourceValuePattern().Matches(entityText)
            .Select(match => match.Groups["path"].Value.Replace('\\', '/').TrimStart('/').ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    /// <summary>The required files neither cstrike nor valve has, the folders the engine searches.</summary>
    public static IReadOnlyList<string> FindMissing(string bspFile, string cstrikeDir) {
        string valveDir = Path.Combine(Path.GetDirectoryName(cstrikeDir)!, "valve");
        return ReadRequiredFiles(bspFile)
            .Where(relativePath => !File.Exists(Path.Combine(cstrikeDir, relativePath)) && !File.Exists(Path.Combine(valveDir, relativePath)))
            .ToList();
    }

    private static string? ReadEntityText(string bspFile) {
        try {
            using var stream = File.OpenRead(bspFile);
            using var reader = new BinaryReader(stream);
            reader.ReadInt32();
            stream.Seek(4 + EntitiesLump * 8, SeekOrigin.Begin);
            int offset = reader.ReadInt32();
            int length = reader.ReadInt32();
            if (offset <= 0 || length <= 0 || offset + (long)length > stream.Length) {
                return null;
            }
            stream.Seek(offset, SeekOrigin.Begin);
            return Encoding.ASCII.GetString(reader.ReadBytes(length));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    // A quoted value that is a model or sprite file; brush models ("*12") never match.
    [GeneratedRegex(@"""(?<path>[A-Za-z0-9_\-./\\]+\.(mdl|spr))""", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceValuePattern();
}
