using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace FunServer;

/// <summary>
/// The package's CounterStrikeSharp plugin. The parts live in partial files:
/// the live feed for the window's map (LiveFeed.cs), the mode configs on
/// every map (ModeConfigs.cs), the admin commands the window sends over
/// RCON (AdminCommands.cs), the cvars and helpers (FunSettings.cs), hook,
/// grab and parachute (HookGrab.cs), the fun rounds (FunRounds.cs), and
/// kill streaks with the mode vote (StreaksAndVote.cs).
/// </summary>
public sealed partial class FunServerPlugin : BasePlugin {
    public override string ModuleName => "Fun Server";
    public override string ModuleVersion => "0.2.0";
    public override string ModuleAuthor => "LAN Fun Server";
    public override string ModuleDescription => "Live map feed, fun modes, hook, grab, parachute, fun rounds, streaks and mode vote";

    public override void Load(bool hotReload) {
        LoadModeConfigs();
        LoadLiveFeed();
        LoadHookGrab();
        LoadFunRounds();
        LoadStreaksAndVote();
        Server.PrintToConsole($"[FunServer] {ModuleVersion} loaded on {Server.MapName}");
    }

    public override void Unload(bool hotReload) {
        UnloadLiveFeed();
    }
}
