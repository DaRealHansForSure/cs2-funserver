using System.Globalization;

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
    /// <summary>The hidden fast download server next to hlds.exe, no window at all.</summary>
    FastDownload,
}

/// <summary>What the exe was started for, read from its command line.</summary>
public sealed record AppCommand(AppCommandKind Kind, string? Root = null, int Port = 0, int? WatchProcessId = null) {
    public bool HasWindow => Kind != AppCommandKind.FastDownload;

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
            case "--fastdl":
                return ParseFastDownload(args);
            default:
                return new AppCommand(AppCommandKind.Window);
        }
    }

    /// <summary>--fastdl --root "path" --port 27080 [--watch-pid 1234]</summary>
    private static AppCommand ParseFastDownload(IReadOnlyList<string> args) {
        string? root = null;
        int port = 0;
        int? watchProcessId = null;
        for (int index = 1; index < args.Count - 1; index++) {
            string value = args[index + 1];
            switch (args[index].ToLowerInvariant()) {
                case "--root":
                    root = value;
                    break;
                case "--port":
                    port = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPort) ? parsedPort : 0;
                    break;
                case "--watch-pid":
                    watchProcessId = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedId) ? parsedId : null;
                    break;
            }
        }

        if (root == null || port <= 0) {
            // Nothing sensible to serve; a window is better than a silent no-op.
            return new AppCommand(AppCommandKind.Window);
        }
        return new AppCommand(AppCommandKind.FastDownload, root, port, watchProcessId);
    }
}
