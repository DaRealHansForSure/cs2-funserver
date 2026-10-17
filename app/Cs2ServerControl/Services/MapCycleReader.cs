using System.IO;

namespace Cs2ServerControl.Services;

/// <summary>Reads the map names of a mapcycle_*.txt file in file order.</summary>
public static class MapCycleReader {
    public static IReadOnlyList<string> Read(string cycleFile) {
        if (!File.Exists(cycleFile)) {
            return [];
        }

        try {
            return Parse(File.ReadAllLines(cycleFile));
        } catch (IOException) {
            return [];
        }
    }

    internal static IReadOnlyList<string> Parse(IEnumerable<string> lines) {
        var maps = new List<string>();
        foreach (string line in lines) {
            string trimmed = line.Trim();
            bool isCommentOrEmpty = trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith(';');
            if (isCommentOrEmpty) {
                continue;
            }

            string mapName = trimmed.Split(' ', '\t')[0].ToLowerInvariant();
            if (!maps.Contains(mapName)) {
                maps.Add(mapName);
            }
        }
        return maps;
    }

    /// <summary>Every map of every mapcycle_*.txt, unique and sorted.</summary>
    public static IReadOnlyList<string> ReadAllCycles(string cstrikeDir) {
        if (!Directory.Exists(cstrikeDir)) {
            return [];
        }

        return Directory.EnumerateFiles(cstrikeDir, "mapcycle_*.txt")
            .SelectMany(Read)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
