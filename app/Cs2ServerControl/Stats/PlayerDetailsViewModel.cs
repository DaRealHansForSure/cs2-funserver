using System.Globalization;
using System.Windows.Media;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;

namespace Cs2ServerControl.Stats;

public sealed record StatTile(string Label, string Value);

public sealed record WeaponRow(ImageSource? Icon, string Name, int Kills, string HeadshotText);

public sealed record OpponentRow(string Name, bool IsBot, int Kills, int Deaths) {
    public string BotText => IsBot ? " BOT" : "";
}

public sealed record MapRow(string Map, int Kills, int Deaths) {
    public string KillDeathText => Deaths == 0
        ? Kills.ToString(CultureInfo.InvariantCulture)
        : ((double)Kills / Deaths).ToString("0.00", CultureInfo.InvariantCulture);
}

public sealed record EventRow(string TimeText, bool IsKill, string Text, ImageSource? WeaponIcon, string WeaponName, bool IsHeadshot);

/// <summary>
/// Everything the logs know about one player in the chosen period: the
/// numbers, the weapons, who they hunted and who hunted them, the maps and
/// their latest kills and deaths. Opened from a leaderboard row.
/// </summary>
public sealed class PlayerDetailsViewModel : ModalViewModel {
    public PlayerDetailsViewModel(PlayerStats stats, string periodTitle, KillIcons killIcons) {
        Stats = stats;
        // Lower case behind the name: "Peter  (last 12 hours)".
        PeriodText = $"  ({periodTitle.ToLowerInvariant()})";
        Tiles = [
            new("Kills", Format(stats.Kills)),
            new("Deaths", Format(stats.Deaths)),
            new("K/D", stats.KillDeathRatio.ToString("0.00", CultureInfo.InvariantCulture)),
            new("Headshots", $"{stats.HeadshotPercent:0} %"),
            new("Best streak", Format(stats.BestStreak)),
            new("Knife kills", Format(stats.KnifeKills)),
            new("HE kills", Format(stats.GrenadeKills)),
            new("Suicides", Format(stats.Suicides)),
        ];
        Weapons = stats.Weapons
            .Select(weapon => new WeaponRow(killIcons.ForWeapon(weapon.Weapon), WeaponNames.Display(weapon.Weapon), weapon.Kills,
                $"{Percent(weapon.Headshots, weapon.Kills)} %"))
            .ToList();
        Opponents = stats.Opponents.Select(opponent => new OpponentRow(opponent.Name, opponent.IsBot, opponent.Kills, opponent.Deaths)).ToList();
        Maps = stats.Maps.Select(map => new MapRow(map.Map, map.Kills, map.Deaths)).ToList();
        RecentEvents = stats.RecentEvents
            .Select(playerEvent => new EventRow(
                playerEvent.Time.ToString("HH:mm", CultureInfo.InvariantCulture),
                playerEvent.IsKill,
                playerEvent.IsKill ? $"killed {playerEvent.Other}" : $"killed by {playerEvent.Other}",
                killIcons.ForWeapon(playerEvent.Weapon),
                WeaponNames.Display(playerEvent.Weapon),
                playerEvent.IsHeadshot))
            .ToList();

        OpponentTally? victim = stats.Opponents.Where(opponent => opponent.Kills > 0).MaxBy(opponent => opponent.Kills);
        OpponentTally? nemesis = stats.Opponents.Where(opponent => opponent.Deaths > 0).MaxBy(opponent => opponent.Deaths);
        FavoriteVictimText = victim == null ? "-" : $"{victim.Name} ({victim.Kills} kills)";
        NemesisText = nemesis == null ? "-" : $"{nemesis.Name} ({nemesis.Deaths} deaths)";
    }

    public PlayerStats Stats { get; }
    public string Name => Stats.Name;
    public string BotText => Stats.IsBot ? " BOT" : "";
    public string PeriodText { get; }
    public IReadOnlyList<StatTile> Tiles { get; }
    public IReadOnlyList<WeaponRow> Weapons { get; }
    public IReadOnlyList<OpponentRow> Opponents { get; }
    public IReadOnlyList<MapRow> Maps { get; }
    public IReadOnlyList<EventRow> RecentEvents { get; }
    public string FavoriteVictimText { get; }
    public string NemesisText { get; }

    private static string Format(int value) {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Percent(int part, int whole) {
        if (whole == 0) {
            return "0";
        }
        return Math.Round(100.0 * part / whole).ToString("0", CultureInfo.InvariantCulture);
    }
}
