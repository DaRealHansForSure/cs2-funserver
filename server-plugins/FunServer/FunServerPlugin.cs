using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace FunServer;

/// <summary>
/// The package's CounterStrikeSharp plugin. The parts live in partial files:
/// the live feed for the window's map (LiveFeed.cs), the mode configs on
/// every map (ModeConfigs.cs) and the admin commands the window sends over
/// RCON (AdminCommands.cs).
/// </summary>
public sealed partial class FunServerPlugin : BasePlugin {
    public override string ModuleName => "Fun Server";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "LAN Fun Server";
    public override string ModuleDescription => "Live map feed, fun modes and admin commands for the CS2 Server Control window";

    public override void Load(bool hotReload) {
        LoadModeConfigs();
        LoadLiveFeed();
        Server.PrintToConsole($"[FunServer] {ModuleVersion} loaded on {Server.MapName}");
    }

    public override void Unload(bool hotReload) {
        UnloadLiveFeed();
    }
}
