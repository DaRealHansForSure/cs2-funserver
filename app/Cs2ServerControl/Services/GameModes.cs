namespace Cs2ServerControl.Services;

public sealed record GameMode(string Id, string Title, string Description);

public static class GameModes {
    public static readonly IReadOnlyList<GameMode> All = [
        new("classic", "Classic Fun", "Hook, grab, parachute, bunny hop"),
        new("gungame", "GunGame", "24 weapon levels, free for all"),
        new("zombie", "Zombie", "Humans against zombies, infection"),
        new("deathmatch", "Deathmatch", "Instant respawn, random weapons"),
        new("deathrun", "Deathrun", "One trapper, everyone else runs the course"),
    ];

    public static string TitleOf(string? modeId) {
        GameMode? mode = All.FirstOrDefault(candidate => candidate.Id == modeId);
        if (mode != null) {
            return mode.Title;
        }
        if (string.IsNullOrEmpty(modeId)) {
            return "-";
        }
        // A mode id the window does not know (a hand-made mode config) still gets a readable name.
        return char.ToUpperInvariant(modeId[0]) + modeId[1..];
    }
}
