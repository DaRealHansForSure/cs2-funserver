using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace Cs2ServerControl.Services;

/// <summary>
/// The game's own kill feed icons, read at runtime from the installed server:
/// CS2 keeps them as compiled SVGs (.vsvg_c) in game\csgo\pak01_dir.vpk, one
/// per weapon name (ak47, awp, knife_karambit ...), plus the headshot and
/// skull of the death notices. Nothing is copied into the package.
/// </summary>
public sealed class KillIcons {
    private const string EquipmentFolder = "panorama/images/icons/equipment/";
    private const string HeadshotIcon = "panorama/images/hud/deathnotice/icon_headshot.vsvg_c";
    private const string SkullIcon = "panorama/images/hud/deathnotice/icon_suicide.vsvg_c";

    private readonly PackagePaths _paths;
    private readonly Brush _tint;
    private readonly ConcurrentDictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _loadLock = new();
    private Dictionary<string, byte[]>? _svgFiles;

    public KillIcons(PackagePaths paths, Color tint) {
        _paths = paths;
        var brush = new SolidColorBrush(tint);
        brush.Freeze();
        _tint = brush;
    }

    /// <summary>The icon for a weapon name as the live feed sends it, null when there is none.</summary>
    public ImageSource? ForWeapon(string weapon) {
        if (weapon.Length == 0) {
            return null;
        }
        string iconName = weapon.ToLowerInvariant();
        ImageSource? icon = Get(EquipmentFolder + iconName + ".vsvg_c");
        // Knife skins without an icon of their own still get a knife.
        if (icon == null && iconName.StartsWith("knife", StringComparison.Ordinal)) {
            icon = Get(EquipmentFolder + "knife.vsvg_c");
        }
        return icon;
    }

    /// <summary>The skull the game shows for deaths without a killer (falling, world).</summary>
    public ImageSource? Skull => Get(SkullIcon);

    public ImageSource? Headshot => Get(HeadshotIcon);

    private ImageSource? Get(string path) {
        return _icons.GetOrAdd(path, Load);
    }

    private ImageSource? Load(string path) {
        Dictionary<string, byte[]> files = ReadSvgFiles();
        if (!files.TryGetValue(path, out byte[]? compiled)) {
            return null;
        }
        try {
            return Render(compiled);
        } catch (Exception exception) when (exception is IOException or InvalidDataException or System.Xml.XmlException or InvalidOperationException) {
            return null;
        }
    }

    /// <summary>
    /// All icons in one pass over the package, then the package is closed
    /// again: a CS2 update with SteamCMD must never find it held open.
    /// </summary>
    private Dictionary<string, byte[]> ReadSvgFiles() {
        lock (_loadLock) {
            if (_svgFiles != null) {
                return _svgFiles;
            }
            _svgFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            string packageFile = Path.Combine(_paths.GameDir, "pak01_dir.vpk");
            if (!File.Exists(packageFile)) {
                return _svgFiles;
            }
            try {
                using var package = new Package();
                package.Read(packageFile);
                if (!package.Entries.TryGetValue("vsvg_c", out List<PackageEntry>? entries)) {
                    return _svgFiles;
                }
                foreach (PackageEntry entry in entries) {
                    string path = entry.GetFullPath();
                    bool isWanted = path.StartsWith(EquipmentFolder, StringComparison.OrdinalIgnoreCase)
                        || path.Equals(HeadshotIcon, StringComparison.OrdinalIgnoreCase)
                        || path.Equals(SkullIcon, StringComparison.OrdinalIgnoreCase);
                    if (isWanted) {
                        package.ReadEntry(entry, out byte[] bytes);
                        _svgFiles[path] = bytes;
                    }
                }
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) {
                // Without the package the Live Map shows weapon names instead of icons.
            }
            return _svgFiles;
        }
    }

    /// <summary>The SVG inside the compiled resource, drawn in the tint color and frozen for any thread.</summary>
    private ImageSource? Render(byte[] compiled) {
        using var resource = new Resource();
        resource.Read(new MemoryStream(compiled));
        if (resource.DataBlock is not Panorama panorama) {
            return null;
        }

        var reader = new FileSvgReader(new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true });
        DrawingGroup? drawing = reader.Read(new MemoryStream(panorama.Data));
        if (drawing == null) {
            return null;
        }
        Tint(drawing);
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    /// <summary>The icons are white shapes; the Live Map draws them in its own token color.</summary>
    private void Tint(Drawing drawing) {
        switch (drawing) {
            case DrawingGroup group:
                foreach (Drawing child in group.Children) {
                    Tint(child);
                }
                return;
            case GeometryDrawing geometry:
                if (geometry.Brush != null) {
                    geometry.Brush = _tint;
                }
                if (geometry.Pen != null) {
                    geometry.Pen = new Pen(_tint, geometry.Pen.Thickness);
                }
                return;
        }
    }
}
