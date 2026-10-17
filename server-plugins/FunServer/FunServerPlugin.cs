using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace FunServer;

/// <summary>
/// The package's CounterStrikeSharp plugin. The parts live in partial files:
/// the live feed for the window's map (LiveFeed.cs), the mode configs on
/// every map (ModeConfigs.cs), the admin commands the window sends over
/// RCON (AdminCommands.cs), the cvars and helpers (FunSettings.cs), hook,
/// grab and parachute (HookGrab.cs), the fun rounds (FunRounds.cs),
/// kill streaks with the mode vote (StreaksAndVote.cs), the outline glow
/// (Glow.cs), the zombie and deathrun modes (Zombie.cs, Deathrun.cs) and
/// the log for the window's Stats screen (FunLog.cs).
/// </summary>
public sealed partial class FunServerPlugin : BasePlugin {
    public override string ModuleName => "Fun Server";
    public override string ModuleVersion => "0.3.0";
    public override string ModuleAuthor => "LAN Fun Server";
    public override string ModuleDescription => "Live map feed, fun modes, hook, grab, parachute, fun rounds, streaks, mode vote, zombie and deathrun";

    public override void Load(bool hotReload) {
        LoadModeConfigs();
        LoadLiveFeed();
        LoadHookGrab();
        LoadFunRounds();
        LoadStreaksAndVote();
        LoadGlow();
        LoadZombie();
        LoadDeathrun();
        LoadFunLog();
        Server.PrintToConsole($"[FunServer] {ModuleVersion} loaded on {Server.MapName}");
    }

    public override void Unload(bool hotReload) {
        UnloadLiveFeed();
    }
}
