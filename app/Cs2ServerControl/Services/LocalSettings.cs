using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Cs2ServerControl.Services;

/// <summary>
/// Reads and writes server\cstrike\local.cfg, a plain list of cvar "value"
/// lines. fun_modes.amxx runs the file on every map start after the mode
/// config, so whatever is saved here wins over the mode defaults. Lines the
/// window does not know (comments, hand-written cvars) are kept as they are.
/// </summary>
public sealed partial class LocalSettings {
    private const string Header =
        "// Settings of this PC, written by the Server Control window.\r\n" +
        "// Runs after the mode config on every map. Not part of the package, never overwritten.";

    private readonly string _path;
    private readonly List<string> _lines = [];

    public LocalSettings(string path) {
        _path = path;
        if (File.Exists(path)) {
            _lines.AddRange(File.ReadAllLines(path));
        }
    }

    public string? Get(string cvar) {
        foreach (string line in _lines) {
            Match match = CvarLine().Match(line);
            if (match.Success && match.Groups["cvar"].Value.Equals(cvar, StringComparison.OrdinalIgnoreCase)) {
                return match.Groups["value"].Value;
            }
        }
        return null;
    }

    public void Set(string cvar, string value) {
        // Quotes would break the config line, the engine has no escaping.
        string newLine = $"{cvar} \"{value.Replace("\"", "")}\"";
        for (int i = 0; i < _lines.Count; i++) {
            Match match = CvarLine().Match(_lines[i]);
            if (match.Success && match.Groups["cvar"].Value.Equals(cvar, StringComparison.OrdinalIgnoreCase)) {
                _lines[i] = newLine;
                return;
            }
        }
        _lines.Add(newLine);
    }

    /// <summary>Drops the cvar line, so the mode default applies again.</summary>
    public void Remove(string cvar) {
        _lines.RemoveAll(line => {
            Match match = CvarLine().Match(line);
            return match.Success && match.Groups["cvar"].Value.Equals(cvar, StringComparison.OrdinalIgnoreCase);
        });
    }

    public void Save() {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var content = new StringBuilder();
        if (_lines.Count == 0 || !_lines[0].StartsWith("//", StringComparison.Ordinal)) {
            content.Append(Header).Append("\r\n");
        }
        foreach (string line in _lines) {
            content.Append(line).Append("\r\n");
        }
        File.WriteAllText(_path, content.ToString(), Encoding.ASCII);
    }

    // The window writes quoted values, the mode configs mostly plain ones.
    [GeneratedRegex(@"^\s*(?<cvar>[A-Za-z0-9_]+)\s+(?:""(?<value>[^""]*)""|(?<value>[^\s""/]+))")]
    private static partial Regex CvarLine();
}
