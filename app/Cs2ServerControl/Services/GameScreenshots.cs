using System.IO;
using System.Windows;
using SkiaSharp;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace Cs2ServerControl.Services;

/// <summary>
/// The pictures CS2 shows for its own maps in the map vote, read from the
/// installed server: game\csgo\pak01_dir.vpk holds one 640 x 360 texture per
/// Valve map in panorama/images/map_icons/screenshots/360p. They are turned
/// into PNG files in tools\previews\game\ once, in the background, so the
/// lists never wait for the package and nothing is copied into the package.
/// </summary>
public static class GameScreenshots {
    private const string ScreenshotFolder = "panorama/images/map_icons/screenshots/360p/";
    private const string ScreenshotSuffix = "_png.vtex_c";

    private static readonly object StartLock = new();
    private static bool _isStarted;

    public static string CacheDir(PackagePaths paths) {
        return Path.Combine(paths.PreviewsDir, "game");
    }

    /// <summary>
    /// Starts the one extraction of this session, unless it already ran.
    /// Each new picture raises MapThumbnails.PictureChanged on the UI thread.
    /// </summary>
    public static void EnsureExtracted(PackagePaths paths) {
        lock (StartLock) {
            if (_isStarted) {
                return;
            }
            _isStarted = true;
        }
        Task.Run(() => Extract(paths));
    }

    private static void Extract(PackagePaths paths) {
        string packageFile = Path.Combine(paths.GameDir, "pak01_dir.vpk");
        if (!File.Exists(packageFile)) {
            return;
        }
        List<string> newMaps = [];
        try {
            Directory.CreateDirectory(CacheDir(paths));
            // Opened once and closed again: a CS2 update with SteamCMD must never find it held open.
            using var package = new Package();
            package.Read(packageFile);
            if (package.Entries == null || !package.Entries.TryGetValue("vtex_c", out List<PackageEntry>? entries)) {
                return;
            }
            foreach (PackageEntry entry in entries) {
                string? mapName = MapNameOf(entry);
                if (mapName == null) {
                    continue;
                }
                string target = Path.Combine(CacheDir(paths), mapName + ".png");
                if (File.Exists(target)) {
                    continue;
                }
                if (TrySave(package, entry, target)) {
                    newMaps.Add(mapName);
                }
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) {
            // Without the pictures the lists show the radar or nothing, as before.
        }
        AnnounceOnUiThread(newMaps);
    }

    /// <summary>".../360p/de_dust2_png.vtex_c" is de_dust2; everything else is not a map screenshot.</summary>
    private static string? MapNameOf(PackageEntry entry) {
        string path = entry.GetFullPath();
        bool isScreenshot = path.StartsWith(ScreenshotFolder, StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(ScreenshotSuffix, StringComparison.OrdinalIgnoreCase);
        if (!isScreenshot) {
            return null;
        }
        string fileName = path[ScreenshotFolder.Length..];
        return fileName[..^ScreenshotSuffix.Length].ToLowerInvariant();
    }

    private static bool TrySave(Package package, PackageEntry entry, string target) {
        try {
            package.ReadEntry(entry, out byte[] data);
            using var resource = new Resource();
            resource.Read(new MemoryStream(data));
            if (resource.DataBlock is not Texture texture) {
                return false;
            }
            using SKBitmap bitmap = texture.GenerateBitmap();
            using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            // Written under a temporary name first, so a list never reads half a file.
            string partial = target + ".part";
            File.WriteAllBytes(partial, png.ToArray());
            File.Move(partial, target, overwrite: true);
            return true;
        } catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException) {
            return false;
        }
    }

    private static void AnnounceOnUiThread(List<string> mapNames) {
        if (mapNames.Count == 0 || Application.Current == null) {
            return;
        }
        Application.Current.Dispatcher.BeginInvoke(() => {
            foreach (string mapName in mapNames) {
                MapThumbnails.Forget(mapName);
            }
        });
    }
}
