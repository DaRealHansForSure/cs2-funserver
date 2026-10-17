namespace Cs2ServerControl.Services;

/// <summary>
/// One fun mode. CS2 picks its rules from game_type and game_mode; the fun
/// modes without a Valve counterpart (Classic Fun, Zombie, Deathrun) run on
/// Casual and get their rules from our plugins.
/// </summary>
public sealed record GameMode(string Id, string Title, string Description, int GameType, int GameModeNumber);

public static class GameModes {
    public static readonly IReadOnlyList<GameMode> All = [
        new("classic", "Classic Fun", "Hook, grab, parachute, bunny hop", GameType: 0, GameModeNumber: 0),
        new("gungame", "Arms Race", "Valve's GunGame: a new weapon every kill", GameType: 1, GameModeNumber: 0),
        new("zombie", "Zombie", "Humans against zombies, infection", GameType: 0, GameModeNumber: 0),
        new("deathmatch", "Deathmatch", "Instant respawn, random weapons", GameType: 1, GameModeNumber: 2),
        new("deathrun", "Deathrun", "One trapper, everyone else runs the course", GameType: 0, GameModeNumber: 0),
    ];

    public static GameMode? Find(string? modeId) {
        return All.FirstOrDefault(candidate => candidate.Id == modeId);
    }

    public static string TitleOf(string? modeId) {
        GameMode? mode = Find(modeId);
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
