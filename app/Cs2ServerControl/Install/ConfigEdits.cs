using System.Text;
using System.Text.RegularExpressions;

namespace Cs2ServerControl.Install;

/// <summary>
/// The line edits the installer makes in the server's config files. Pure
/// functions on lists of lines, so the rules can be tested without a server.
/// </summary>
internal static partial class ConfigEdits {
    public const string GameDllLine = "gamedll \"addons\\metamod\\metamod.dll\"";
    public const string BotEnableLine = "bot_enable \"1\"";

    // Flag "a" on the account kicks anyone who takes the name without the password.
    private const string AdminFlags = "\"abcdefghijklmnopqrstu\" \"a\"";

    [GeneratedRegex(@"^\s*gamedll\s", RegexOptions.IgnoreCase)]
    private static partial Regex GameDllPattern();

    [GeneratedRegex(@"^\s*bot_enable\s", RegexOptions.IgnoreCase)]
    private static partial Regex BotEnablePattern();

    [GeneratedRegex(@"^;miscstats\.amxx", RegexOptions.IgnoreCase)]
    private static partial Regex DisabledMiscStatsPattern();

    /// <summary>Points liblist.gam at Metamod, which then loads the real game dll.</summary>
    public static List<string> PointGameDllToMetamod(IEnumerable<string> libListLines) {
        List<string> updated = [];
        foreach (string line in libListLines) {
            if (GameDllPattern().IsMatch(line)) {
                updated.Add(GameDllLine);
            } else {
                updated.Add(line);
            }
        }
        return updated;
    }

    /// <summary>
    /// Sets bot_enable 1 in game_init.cfg, which ReGameDLL runs once at startup
    /// before any other config. The ReGameDLL step ships that file with
    /// bot_enable "0" on every run, so this has to run after it.
    /// </summary>
    public static List<string> EnableZBot(IEnumerable<string> gameInitLines) {
        List<string> updated = [];
        bool hasEnableLine = false;
        foreach (string line in gameInitLines) {
            if (BotEnablePattern().IsMatch(line)) {
                hasEnableLine = true;
                updated.Add(BotEnableLine);
            } else {
                updated.Add(line);
            }
        }

        if (!hasEnableLine) {
            updated.Add(BotEnableLine);
        }
        return updated;
    }

    /// <summary>The only line of addons\metamod\plugins.ini, with the dll path relative to cstrike.</summary>
    public static string MetamodPluginLine(string cstrikeDir, string amxModuleFile) {
        string relativePath = amxModuleFile.Substring(cstrikeDir.TrimEnd('\\', '/').Length + 1).Replace('\\', '/');
        return "win32 " + relativePath;
    }

    /// <summary>Appends the block unless its first line is already in the file.</summary>
    public static List<string> AddBlockOnce(IEnumerable<string> lines, IReadOnlyList<string> block) {
        List<string> updated = [.. lines];
        string marker = block[0];
        bool hasMarker = updated.Any(line => line.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (hasMarker) {
            return updated;
        }

        updated.Add("");
        updated.AddRange(block);
        return updated;
    }

    /// <summary>
    /// Turns on miscstats and registers the fun plugins in AMX Mod X's
    /// plugins.ini.
    /// </summary>
    public static List<string> MergeAmxPluginList(IEnumerable<string> pluginListLines, IReadOnlyList<string> funPlugins) {
        // miscstats brings the "Headshot", "Monster Kill" and similar announcements.
        List<string> updated = [.. pluginListLines.Select(line => DisabledMiscStatsPattern().Replace(line, "miscstats.amxx"))];

        updated = AddBlockOnce(updated, [InstallComponents.FunPluginsMarker, .. funPlugins]);

        // Plugins added in a later version of the package must reach existing
        // installs too, where the block above is already present.
        var registeredLines = new HashSet<string>(updated.Select(line => line.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (string plugin in funPlugins) {
            if (!registeredLines.Contains(plugin)) {
                updated.Add(plugin);
            }
        }
        return updated;
    }

    public static bool HasPackageAdmin(IEnumerable<string> usersLines) {
        return usersLines.Any(line => line.Contains(InstallComponents.FunPluginsMarker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Null when the account can be written into users.ini, otherwise why not.</summary>
    public static string? AdminAccountProblem(AdminAccount account) {
        if (string.IsNullOrWhiteSpace(account.Name)) {
            return "The admin name is empty.";
        }
        if (string.IsNullOrWhiteSpace(account.Password)) {
            return "The admin password is empty.";
        }
        // users.ini has no escaping, a quote would end the field early.
        if (account.Name.Contains('"')) {
            return "The admin name must not contain quotes.";
        }
        bool passwordBreaksCommand = account.Password.Contains('"') || account.Password.Any(char.IsWhiteSpace);
        if (passwordBreaksCommand) {
            return "The admin password must not contain spaces or quotes.";
        }
        return null;
    }

    public static List<string> AddAdminAccount(IEnumerable<string> usersLines, AdminAccount account) {
        string adminLine = $"\"{AsFileBytes(account.Name)}\" \"{AsFileBytes(account.Password)}\" {AdminFlags}";
        return AddBlockOnce(usersLines, [InstallComponents.FunPluginsMarker, adminLine]);
    }

    /// <summary>
    /// The engine compares player names as UTF-8 bytes. The files are written
    /// byte for byte (Latin-1), so a name with umlauts has to be stored as
    /// its UTF-8 bytes to match in game.
    /// </summary>
    private static string AsFileBytes(string text) {
        return Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));
    }
}
