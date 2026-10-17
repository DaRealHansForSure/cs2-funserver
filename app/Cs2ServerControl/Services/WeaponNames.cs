namespace Cs2ServerControl.Services;

/// <summary>Display names for the internal weapon names the live feed sends.</summary>
public static class WeaponNames {
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase) {
        ["ak47"] = "AK-47",
        ["knife"] = "Knife",
        ["awp"] = "AWP",
        ["deagle"] = "Desert Eagle",
        ["m4a1"] = "M4A1",
        ["mp5navy"] = "MP5",
        ["famas"] = "Famas",
    };

    public static string Display(string? internalName) {
        if (string.IsNullOrWhiteSpace(internalName)) {
            return "-";
        }
        if (DisplayNames.TryGetValue(internalName, out string? displayName)) {
            return displayName;
        }
        return internalName.ToUpperInvariant();
    }

    public static bool IsKnife(string? internalName) {
        if (string.IsNullOrEmpty(internalName)) {
            return false;
        }
        // CS2: knife, knife_t, knife_karambit ... and the bayonets.
        return internalName.StartsWith("knife", StringComparison.OrdinalIgnoreCase)
            || internalName.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
    }
}
