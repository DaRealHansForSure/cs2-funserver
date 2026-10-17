using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cs2ServerControl.Services;

/// <summary>
/// The game's own kill feed icons (d_ak47, d_knife, d_headshot ...), read
/// at runtime from the installed server: sprites\hud.txt names the region,
/// the 640hud*.spr sheet holds the pixels. Nothing is copied into the package.
/// </summary>
public sealed class KillIcons {
    private const int HudResolution = 640;
    private const int SpriteHeaderSize = 40;
    private const int SpriteVersion = 2;
    private const int TextureAdditive = 1;
    private const int TextureAlphaTest = 3;

    private readonly PackagePaths _paths;
    private readonly Color _tint;
    private readonly ConcurrentDictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SpriteSheet?> _sheets = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, HudRegion>? _regions;

    public KillIcons(PackagePaths paths, Color tint) {
        _paths = paths;
        _tint = tint;
    }

    /// <summary>The icon for a weapon name as the live feed sends it, null when there is none.</summary>
    public ImageSource? ForWeapon(string weapon) {
        if (weapon.Length == 0) {
            return null;
        }
        string iconName = weapon.ToLowerInvariant();
        // The live feed sends the item a player holds, the kill icon is named after the grenade itself.
        if (iconName == "hegrenade") {
            iconName = "grenade";
        }
        return Get("d_" + iconName);
    }

    /// <summary>The skull the game shows for deaths without a killer (falling, world).</summary>
    public ImageSource? Skull => Get("d_skull");

    public ImageSource? Headshot => Get("d_headshot");

    private ImageSource? Get(string spriteName) {
        return _icons.GetOrAdd(spriteName, Load);
    }

    private ImageSource? Load(string spriteName) {
        try {
            _regions ??= ReadHudRegions(Path.Combine(_paths.CstrikeDir, "sprites", "hud.txt"));
            if (!_regions.TryGetValue(spriteName, out HudRegion? region)) {
                return null;
            }

            SpriteSheet? sheet = _sheets.GetOrAdd(region.SheetName, sheetName =>
                SpriteSheet.Read(Path.Combine(_paths.CstrikeDir, "sprites", sheetName + ".spr")));
            return sheet?.Cut(region, _tint);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) {
            return null;
        }
    }

    /// <summary>Reads "name resolution sheet x y width height" lines for the 640 HUD.</summary>
    internal static Dictionary<string, HudRegion> ParseHudRegions(IEnumerable<string> lines) {
        var regions = new Dictionary<string, HudRegion>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines) {
            string[] fields = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 7 || fields[1] != HudResolution.ToString(CultureInfo.InvariantCulture)) {
                continue;
            }

            bool numbersOk = int.TryParse(fields[3], out int x)
                & int.TryParse(fields[4], out int y)
                & int.TryParse(fields[5], out int width)
                & int.TryParse(fields[6], out int height);
            if (numbersOk && width > 0 && height > 0) {
                regions[fields[0]] = new HudRegion(fields[2], x, y, width, height);
            }
        }
        return regions;
    }

    private static Dictionary<string, HudRegion> ReadHudRegions(string hudFile) {
        if (!File.Exists(hudFile)) {
            return new Dictionary<string, HudRegion>();
        }
        return ParseHudRegions(File.ReadLines(hudFile));
    }

    internal sealed record HudRegion(string SheetName, int X, int Y, int Width, int Height);

    /// <summary>The first frame of a GoldSrc sprite (IDSP version 2) as palette indices.</summary>
    private sealed class SpriteSheet {
        private SpriteSheet(int width, int height, int textureFormat, byte[] palette, byte[] pixels) {
            Width = width;
            Height = height;
            TextureFormat = textureFormat;
            Palette = palette;
            Pixels = pixels;
        }

        public int Width { get; }
        public int Height { get; }
        public int TextureFormat { get; }
        public byte[] Palette { get; }
        public byte[] Pixels { get; }

        public static SpriteSheet? Read(string file) {
            if (!File.Exists(file)) {
                return null;
            }

            byte[] data = File.ReadAllBytes(file);
            if (data.Length < SpriteHeaderSize + 2 || data[0] != 'I' || data[1] != 'D' || data[2] != 'S' || data[3] != 'P') {
                throw new InvalidDataException("Not a sprite file.");
            }
            if (BitConverter.ToInt32(data, 4) != SpriteVersion) {
                throw new InvalidDataException("Unknown sprite version.");
            }

            int textureFormat = BitConverter.ToInt32(data, 12);
            int paletteCount = BitConverter.ToInt16(data, SpriteHeaderSize);
            int paletteStart = SpriteHeaderSize + 2;
            int frameStart = paletteStart + paletteCount * 3;

            // Frame: type (0 = single), origin x and y, width, height, then the indices.
            int frameWidth = BitConverter.ToInt32(data, frameStart + 12);
            int frameHeight = BitConverter.ToInt32(data, frameStart + 16);
            int pixelStart = frameStart + 20;
            if (frameWidth <= 0 || frameHeight <= 0 || pixelStart + frameWidth * frameHeight > data.Length) {
                throw new InvalidDataException("Broken sprite frame.");
            }

            byte[] palette = data[paletteStart..frameStart];
            byte[] pixels = data[pixelStart..(pixelStart + frameWidth * frameHeight)];
            return new SpriteSheet(frameWidth, frameHeight, textureFormat, palette, pixels);
        }

        /// <summary>
        /// Cuts one icon out and tints it like the game's HUD does: additive
        /// sprites use brightness as coverage, so black turns transparent.
        /// </summary>
        public BitmapSource? Cut(HudRegion region, Color tint) {
            if (region.X < 0 || region.Y < 0 || region.X + region.Width > Width || region.Y + region.Height > Height) {
                return null;
            }

            int stride = region.Width * 4;
            byte[] bgra = new byte[stride * region.Height];
            for (int row = 0; row < region.Height; row++) {
                for (int column = 0; column < region.Width; column++) {
                    int index = Pixels[(region.Y + row) * Width + region.X + column];
                    byte red = Palette[index * 3];
                    byte green = Palette[index * 3 + 1];
                    byte blue = Palette[index * 3 + 2];

                    byte coverage;
                    if (TextureFormat == TextureAlphaTest) {
                        coverage = index == 255 ? (byte)0 : (byte)255;
                    } else if (TextureFormat == TextureAdditive) {
                        coverage = Math.Max(red, Math.Max(green, blue));
                    } else {
                        coverage = 255;
                    }

                    int target = row * stride + column * 4;
                    bgra[target] = tint.B;
                    bgra[target + 1] = tint.G;
                    bgra[target + 2] = tint.R;
                    bgra[target + 3] = coverage;
                }
            }

            BitmapSource bitmap = BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
