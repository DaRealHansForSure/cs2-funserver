namespace Cs2ServerControl.Services;

/// <summary>Readable names for the glow colors the fun plugins use.</summary>
public static class GlowNames {
    private static readonly Dictionary<PlayerGlow, string> Names = new() {
        // amx_glow colors (fun_admincmds.sma); green is also the zombie glow.
        [new PlayerGlow(255, 0, 0)] = "red",
        [new PlayerGlow(0, 255, 0)] = "green",
        [new PlayerGlow(0, 80, 255)] = "blue",
        [new PlayerGlow(255, 255, 0)] = "yellow",
        [new PlayerGlow(255, 0, 200)] = "pink",
        [new PlayerGlow(255, 255, 255)] = "white",
        // Kill streak reward (fun_streaks.sma) and the Juggernaut (fun_rounds.sma).
        [new PlayerGlow(255, 200, 0)] = "gold (kill streak)",
        [new PlayerGlow(255, 30, 30)] = "red (Juggernaut)",
    };

    public static string Describe(PlayerGlow? glow) {
        if (glow == null) {
            return "-";
        }
        if (Names.TryGetValue(glow.Value, out string? name)) {
            return name;
        }
        return "custom";
    }
}
