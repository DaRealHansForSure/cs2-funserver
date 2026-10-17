using System.Globalization;

namespace Cs2ServerControl.Services;

public enum GameRuleKind {
    Number,
    Decimal,
    OnOff,
}

/// <summary>One game rule the host can set per mode: its cvar, the label, the allowed range and a hint.</summary>
public sealed record GameRule(string Cvar, string Label, GameRuleKind Kind, double Min, double Max, string Hint);

/// <summary>
/// The round and gameplay cvars the Server screen offers per mode. Every one
/// is also reset in modes\base.cfg, so a value set for one mode never leaks
/// into the next map.
/// </summary>
public static class GameRules {
    public static readonly IReadOnlyList<GameRule> All = [
        new("mp_timelimit", "Map time", GameRuleKind.Number, 0, 180, "minutes per map, 0 = no limit"),
        new("mp_roundtime", "Round time", GameRuleKind.Decimal, 1, 9, "minutes, 1 to 9"),
        new("mp_freezetime", "Freeze time", GameRuleKind.Number, 0, 60, "seconds at round start"),
        // CS2 counts the buy time in seconds (CS 1.6 in minutes).
        new("mp_buytime", "Buy time", GameRuleKind.Number, 0, 600, "seconds after the round starts"),
        new("mp_startmoney", "Start money", GameRuleKind.Number, 0, 16000, "dollars, up to 16000"),
        new("mp_maxrounds", "Max rounds", GameRuleKind.Number, 0, 100, "map change after this many rounds, 0 = off"),
        new("mp_c4timer", "C4 timer", GameRuleKind.Number, 10, 90, "seconds until the bomb explodes"),
        new("sv_gravity", "Gravity", GameRuleKind.Number, 100, 1600, "800 is normal, lower jumps higher"),
        new("mp_friendlyfire", "Friendly fire", GameRuleKind.OnOff, 0, 1, "teammates can hurt each other"),
    ];

    /// <summary>
    /// What a mode uses without a host value: modes\base.cfg, overruled by
    /// modes\&lt;mode&gt;.cfg. Map type configs (surf.cfg) can still change
    /// a value on some maps; the window shows the mode's own value.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadModeDefaults(PackagePaths paths, string modeId) {
        var baseConfig = new LocalSettings(paths.ModeConfigFile("base"));
        var modeConfig = new LocalSettings(paths.ModeConfigFile(modeId));
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameRule rule in All) {
            string? value = modeConfig.Get(rule.Cvar) ?? baseConfig.Get(rule.Cvar);
            if (value != null) {
                defaults[rule.Cvar] = value;
            }
        }
        return defaults;
    }

    /// <summary>
    /// The value as the config needs it, or an error text. Empty means "mode
    /// default". A decimal comma is accepted, German keyboards type one.
    /// </summary>
    public static bool TryNormalize(GameRule rule, string text, out string value, out string? error) {
        value = "";
        error = null;
        string trimmed = text.Trim().Replace(',', '.');
        if (trimmed.Length == 0) {
            return true;
        }
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) {
            error = $"{rule.Label}: not a number.";
            return false;
        }
        bool isWhole = Math.Abs(number - Math.Round(number)) < 0.0001;
        if (rule.Kind != GameRuleKind.Decimal && !isWhole) {
            error = $"{rule.Label}: whole numbers only.";
            return false;
        }
        if (number < rule.Min || number > rule.Max) {
            error = string.Create(CultureInfo.InvariantCulture, $"{rule.Label}: {rule.Min:0.##} to {rule.Max:0.##}.");
            return false;
        }
        value = rule.Kind == GameRuleKind.Decimal
            ? number.ToString("0.##", CultureInfo.InvariantCulture)
            : ((int)Math.Round(number)).ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
