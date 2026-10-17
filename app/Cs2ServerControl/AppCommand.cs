namespace Cs2ServerControl;

public enum AppCommandKind {
    /// <summary>Plain start: just the window.</summary>
    Window,
    /// <summary>--install: the window, which installs or repairs the server right away.</summary>
    Install,
    /// <summary>--update-maps: the window, which updates the maps right away.</summary>
    UpdateMaps,
    /// <summary>--start: the window, which starts the server right away.</summary>
    Start,
}

/// <summary>What the exe was started for, read from its command line.</summary>
public sealed record AppCommand(AppCommandKind Kind) {
    public static AppCommand Parse(IReadOnlyList<string> args) {
        if (args.Count == 0) {
            return new AppCommand(AppCommandKind.Window);
        }

        switch (args[0].ToLowerInvariant()) {
            case "--install":
                return new AppCommand(AppCommandKind.Install);
            case "--update-maps":
                return new AppCommand(AppCommandKind.UpdateMaps);
            case "--start":
                return new AppCommand(AppCommandKind.Start);
            default:
                return new AppCommand(AppCommandKind.Window);
        }
    }
}
