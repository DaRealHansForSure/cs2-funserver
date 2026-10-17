namespace Cs2ServerControl.Services;

/// <summary>Display names for the internal weapon names the live feed sends.</summary>
public static class WeaponNames {
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase) {
        ["ak47"] = "AK-47",
        ["knife"] = "Knife",
        ["knife_t"] = "Knife",
        ["awp"] = "AWP",
        ["deagle"] = "Desert Eagle",
        ["m4a1"] = "M4A4",
        ["m4a1_silencer"] = "M4A1-S",
        ["m4a1_silencer_off"] = "M4A1-S",
        ["mp5navy"] = "MP5",
        ["mp5sd"] = "MP5-SD",
        ["famas"] = "Famas",
        ["galilar"] = "Galil AR",
        ["aug"] = "AUG",
        ["sg556"] = "SG 553",
        ["ssg08"] = "SSG 08",
        ["g3sg1"] = "G3SG1",
        ["scar20"] = "SCAR-20",
        ["usp_silencer"] = "USP-S",
        ["usp_silencer_off"] = "USP-S",
        ["hkp2000"] = "P2000",
        ["glock"] = "Glock-18",
        ["p250"] = "P250",
        ["fiveseven"] = "Five-SeveN",
        ["tec9"] = "Tec-9",
        ["cz75a"] = "CZ75-Auto",
        ["elite"] = "Dual Berettas",
        ["revolver"] = "R8 Revolver",
        ["mac10"] = "MAC-10",
        ["mp9"] = "MP9",
        ["mp7"] = "MP7",
        ["ump45"] = "UMP-45",
        ["p90"] = "P90",
        ["bizon"] = "PP-Bizon",
        ["nova"] = "Nova",
        ["xm1014"] = "XM1014",
        ["sawedoff"] = "Sawed-Off",
        ["mag7"] = "MAG-7",
        ["m249"] = "M249",
        ["negev"] = "Negev",
        ["hegrenade"] = "HE grenade",
        ["inferno"] = "Molotov",
        ["taser"] = "Zeus x27",
        ["world"] = "World",
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
