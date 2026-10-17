using System.IO;
using System.Text;
using SteamDatabase.ValvePak;

namespace Cs2ServerControl.Services;

/// <summary>
/// Reads the bots' BotProfile.db so a bot name can be mapped to its skill.
/// CS2 keeps the file (the same format as CS 1.6) in game\csgo\pak01_dir.vpk;
/// a loose game\csgo\botprofile.db of the host wins, like in the game.
/// The format: a "Default" block, "Template &lt;Name&gt;" blocks and named
/// profiles "&lt;Template&gt;[+&lt;Template&gt;...] &lt;Name&gt;", each closed by "End",
/// with "Key = Value" lines inside. Later templates and the profile itself
/// override earlier values.
/// </summary>
public static class BotProfileReader {
    private const string ProfileName = "botprofile.db";

    public static IReadOnlyDictionary<string, int> ReadSkills(PackagePaths paths) {
        try {
            string looseFile = Path.Combine(paths.GameDir, ProfileName);
            if (File.Exists(looseFile)) {
                return ParseSkills(File.ReadAllLines(looseFile));
            }
            string? packed = ReadFromGamePackage(Path.Combine(paths.GameDir, "pak01_dir.vpk"));
            return packed == null ? new Dictionary<string, int>() : ParseSkills(packed.Split('\n'));
        } catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException) {
            return new Dictionary<string, int>();
        }
    }

    private static string? ReadFromGamePackage(string packageFile) {
        if (!File.Exists(packageFile)) {
            return null;
        }
        using var package = new Package();
        package.Read(packageFile);
        PackageEntry? entry = package.FindEntry(ProfileName);
        if (entry == null) {
            return null;
        }
        package.ReadEntry(entry, out byte[] bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    internal static IReadOnlyDictionary<string, int> ParseSkills(IEnumerable<string> lines) {
        var templates = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string>? current = null;
        string? currentProfileName = null;

        foreach (string rawLine in lines) {
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0) {
                continue;
            }

            if (current != null) {
                if (line.Equals("End", StringComparison.OrdinalIgnoreCase)) {
                    if (currentProfileName != null && current.TryGetValue("Skill", out string? skillText) && int.TryParse(skillText, out int skill)) {
                        skills[currentProfileName] = skill;
                    }
                    current = null;
                    currentProfileName = null;
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals > 0) {
                    current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
                }
                continue;
            }

            string[] words = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words[0].Equals("Default", StringComparison.OrdinalIgnoreCase)) {
                current = defaults;
                continue;
            }
            if (words[0].Equals("Template", StringComparison.OrdinalIgnoreCase) && words.Length >= 2) {
                // Only the template's own values: zBot applies just what differs
                // from Default, so "Elite+Rifle" must not reset Elite's skill.
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                templates[words[1]] = current;
                continue;
            }
            if (words.Length >= 2) {
                current = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase);
                foreach (string templateName in words[0].Split('+')) {
                    if (templates.TryGetValue(templateName, out Dictionary<string, string>? template)) {
                        foreach (KeyValuePair<string, string> pair in template) {
                            current[pair.Key] = pair.Value;
                        }
                    }
                }
                currentProfileName = words[1];
            }
        }
        return skills;
    }

    private static string StripComment(string line) {
        int comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment >= 0 ? line[..comment] : line;
    }
}
