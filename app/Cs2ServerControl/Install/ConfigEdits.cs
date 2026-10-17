using System.Text.RegularExpressions;

namespace Cs2ServerControl.Install;

/// <summary>
/// The line edits the installer makes in the server's files. Pure functions
/// on lists of lines, so the rules can be tested without a server.
/// </summary>
internal static partial class ConfigEdits {
    public const string MetamodSearchPath = "csgo/addons/metamod";

    [GeneratedRegex(@"^(?<indent>\s*)Game\s+csgo\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GameCsgoPattern();

    [GeneratedRegex(@"^\s*Game\s+csgo/addons/metamod\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MetamodLinePattern();

    /// <summary>
    /// Metamod:Source loads through a search path in gameinfo.gi: the line
    /// "Game csgo/addons/metamod" right above the first "Game csgo". Every CS2
    /// update replaces gameinfo.gi, so this runs on each install and before
    /// each server start; a second run changes nothing.
    /// </summary>
    public static List<string> AddMetamodSearchPath(IEnumerable<string> gameInfoLines) {
        List<string> lines = [.. gameInfoLines];
        if (lines.Any(line => MetamodLinePattern().IsMatch(line))) {
            return lines;
        }

        int index = lines.FindIndex(line => GameCsgoPattern().IsMatch(line));
        if (index < 0) {
            throw new InstallException("gameinfo.gi has no \"Game csgo\" search path, the CS2 install looks broken.");
        }
        string indent = GameCsgoPattern().Match(lines[index]).Groups["indent"].Value;
        lines.Insert(index, $"{indent}Game\t{MetamodSearchPath}");
        return lines;
    }

    public static bool HasMetamodSearchPath(IEnumerable<string> gameInfoLines) {
        return gameInfoLines.Any(line => MetamodLinePattern().IsMatch(line));
    }
}
