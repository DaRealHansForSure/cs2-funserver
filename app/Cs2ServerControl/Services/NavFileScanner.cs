using System.IO;
using System.Text.RegularExpressions;

namespace Cs2ServerControl.Services;

/// <summary>
/// IsOutdated: the file belongs to another build of the map (it came with a
/// map download, or the .bsp was replaced). zBot then only warns and the
/// bots walk on wrong data, so it counts as missing.
/// </summary>
public sealed record NavFileState(string MapName, bool Exists, bool IsOutdated = false) {
    public bool IsUsable => Exists && !IsOutdated;
}

/// <summary>
/// Which maps have a zBot navigation mesh (maps\&lt;map&gt;.nav), and what the
/// server console says while zBot builds one. zBot prints its steps there
/// ("  Merging navigation areas...") but never a percentage; that only goes
/// to the HUD of connected players.
/// </summary>
public static partial class NavFileScanner {
    public static IReadOnlyList<NavFileState> Scan(PackagePaths paths) {
        return MapCycleReader.ReadAllCycles(paths.CstrikeDir)
            .Select(map => Check(paths, map))
            .ToList();
    }

    public static NavFileState Check(PackagePaths paths, string mapName) {
        string navFile = paths.NavFile(mapName);
        if (!File.Exists(navFile)) {
            return new NavFileState(mapName, Exists: false);
        }
        return new NavFileState(mapName, Exists: true, IsOutdated(navFile, paths.BspFile(mapName)));
    }

    internal static bool IsOutdated(string navFile, string bspFile) {
        long? builtFor = NavMeshReader.ReadBspSize(navFile);
        if (builtFor == null || !File.Exists(bspFile)) {
            return false;
        }
        return builtFor.Value != new FileInfo(bspFile).Length;
    }

    /// <summary>The step zBot is on, for example "Merging navigation areas", or null.</summary>
    public static string? ReadStep(string consoleLine) {
        Match match = StepPattern().Match(consoleLine);
        if (!match.Success) {
            return null;
        }
        return match.Groups["step"].Value;
    }

    /// <summary>True for "Navigation file 'cstrike\maps\x.nav' saved.".</summary>
    public static bool IsDoneLine(string consoleLine) {
        return DonePattern().IsMatch(consoleLine);
    }

    [GeneratedRegex(@"^\s{2}(?<step>[A-Z][a-z]+ [A-Za-z ]+)\.\.\.\s*$")]
    private static partial Regex StepPattern();

    [GeneratedRegex(@"^Navigation file '.*\.nav' saved\.")]
    private static partial Regex DonePattern();
}
