using System.IO;
using System.Text;

namespace Cs2ServerControl.Install;

/// <summary>
/// One row of tools\maps.csv or tools\market.csv. ModId names the GameBanana
/// page and may be empty. Maps lists every map the archive installed
/// (market.csv only), so Remove knows what to delete.
/// </summary>
internal sealed record MapListEntry(string Name, string Url, string ArchiveName, string ModId, string Maps = "") {
    public IReadOnlyList<string> MapNames => Maps.Length == 0 ? [Name] : Maps.Split(';', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Reads and writes tools\maps.csv and tools\market.csv (Name,Url,ArchiveName,ModId[,Maps] with a header line).</summary>
internal static class MapList {
    public static IReadOnlyList<MapListEntry> Load(string path) {
        return Parse(File.ReadAllLines(path));
    }

    /// <summary>Like Load, but a missing file is an empty list (market.csv exists only after the first market install).</summary>
    public static IReadOnlyList<MapListEntry> LoadIfExists(string path) {
        return File.Exists(path) ? Load(path) : [];
    }

    public static void Save(string path, IEnumerable<MapListEntry> entries) {
        List<string> lines = ["Name,Url,ArchiveName,ModId,Maps"];
        lines.AddRange(entries.Select(entry => string.Join(',',
            Quote(entry.Name), Quote(entry.Url), Quote(entry.ArchiveName), Quote(entry.ModId), Quote(entry.Maps))));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // ASCII like maps.csv; map names and GameBanana URLs are plain ASCII.
        File.WriteAllLines(path, lines, Encoding.ASCII);
    }

    private static string Quote(string field) {
        bool needsQuotes = field.Contains(',') || field.Contains('"');
        return needsQuotes ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }

    public static IReadOnlyList<MapListEntry> Parse(IEnumerable<string> lines) {
        List<MapListEntry> entries = [];
        List<string>? header = null;
        foreach (string line in lines) {
            if (string.IsNullOrWhiteSpace(line)) {
                continue;
            }

            List<string> fields = SplitFields(line);
            if (header == null) {
                header = fields;
                continue;
            }

            string name = FieldOf(header, fields, "Name");
            if (name.Length == 0) {
                continue;
            }
            entries.Add(new MapListEntry(
                name,
                FieldOf(header, fields, "Url"),
                FieldOf(header, fields, "ArchiveName"),
                FieldOf(header, fields, "ModId"),
                FieldOf(header, fields, "Maps")));
        }
        return entries;
    }

    // Columns are found by their header name, like Import-Csv did, so the
    // order in the file does not matter and a missing column reads as empty.
    private static string FieldOf(List<string> header, List<string> fields, string column) {
        int index = header.FindIndex(name => name.Equals(column, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index >= fields.Count) {
            return "";
        }
        return fields[index].Trim();
    }

    /// <summary>Splits one CSV line; a quoted field may hold commas and doubled quotes.</summary>
    internal static List<string> SplitFields(string line) {
        List<string> fields = [];
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int index = 0; index < line.Length; index++) {
            char character = line[index];
            if (inQuotes) {
                bool isEscapedQuote = character == '"' && index + 1 < line.Length && line[index + 1] == '"';
                if (isEscapedQuote) {
                    current.Append('"');
                    index++;
                } else if (character == '"') {
                    inQuotes = false;
                } else {
                    current.Append(character);
                }
                continue;
            }

            if (character == '"') {
                inQuotes = true;
            } else if (character == ',') {
                fields.Add(current.ToString());
                current.Clear();
            } else {
                current.Append(character);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
