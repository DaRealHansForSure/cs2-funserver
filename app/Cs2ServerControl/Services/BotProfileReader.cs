using System.IO;

namespace Cs2ServerControl.Services;

/// <summary>
/// Reads zBot's BotProfile.db so a bot name can be mapped to its skill.
/// The format: a "Default" block, "Template &lt;Name&gt;" blocks and named
/// profiles "&lt;Template&gt;[+&lt;Template&gt;...] &lt;Name&gt;", each closed by "End",
/// with "Key = Value" lines inside. Later templates and the profile itself
/// override earlier values.
/// </summary>
public static class BotProfileReader {
    public static IReadOnlyDictionary<string, int> ReadSkills(string profileFile) {
        if (!File.Exists(profileFile)) {
            return new Dictionary<string, int>();
        }

        try {
            return ParseSkills(File.ReadAllLines(profileFile));
        } catch (IOException) {
            return new Dictionary<string, int>();
        }
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
