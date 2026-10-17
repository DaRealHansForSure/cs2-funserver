using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cs2ServerControl.Services;

/// <summary>
/// Small map pictures: a .jpg or .png of the host's in tools\previews, else
/// a loose radar image of the map from resource\overviews.
/// </summary>
public sealed class MapThumbnails {
    // WPF reads these without extra codecs; the radar .tga files are skipped.
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp"];

    // Shared by every screen, so a picture rendered on the Maps screen shows
    // up in the Server screen's list too once Forget dropped the old answer.
    private static readonly ConcurrentDictionary<(int Width, string MapName), ImageSource?> Cache = new();

    private readonly PackagePaths _paths;
    private readonly int _decodeWidth;

    /// <summary>Small pictures for lists by default, a larger width for the details dialog.</summary>
    public MapThumbnails(PackagePaths paths, int decodeWidth = 160) {
        _decodeWidth = decodeWidth;
        _paths = paths;
    }

    public ImageSource? Get(string mapName) {
        return Cache.GetOrAdd((_decodeWidth, mapName.ToLowerInvariant()), key => Load(key.MapName));
    }

    /// <summary>Raised on the thread that called Forget with the map whose picture changed.</summary>
    public static event EventHandler<string>? PictureChanged;

    /// <summary>After a new picture was saved: the next Get reads the files again.</summary>
    public static void Forget(string mapName) {
        string key = mapName.ToLowerInvariant();
        foreach ((int Width, string MapName) cached in Cache.Keys.Where(cached => cached.MapName == key)) {
            Cache.TryRemove(cached, out _);
        }
        PictureChanged?.Invoke(null, key);
    }

    private ImageSource? Load(string mapName) {
        foreach (string folder in new[] { _paths.PreviewsDir, _paths.OverviewsDir }) {
            foreach (string extension in Extensions) {
                string candidate = Path.Combine(folder, mapName + extension);
                if (!File.Exists(candidate)) {
                    continue;
                }

                ImageSource? image = Decode(candidate, _decodeWidth);
                bool isRadar = folder == _paths.OverviewsDir;
                if (isRadar && image is BitmapSource radar) {
                    return RemoveRadarBackground(radar);
                }
                return image;
            }
        }
        return null;
    }

    private static ImageSource? Decode(string path, int decodeWidth) {
        try {
            // OnLoad releases the file so the installer can replace it.
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = decodeWidth;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        } catch (Exception exception) when (exception is IOException or NotSupportedException or FileFormatException) {
            return null;
        }
    }

    /// <summary>
    /// The game draws radar images with pure green as the "nothing here" color.
    /// Making it transparent lets the row background show through instead.
    /// </summary>
    private static BitmapSource RemoveRadarBackground(BitmapSource radar) {
        var converted = new FormatConvertedBitmap(radar, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        for (int index = 0; index < pixels.Length; index += 4) {
            byte blue = pixels[index];
            byte green = pixels[index + 1];
            byte red = pixels[index + 2];
            bool isBackground = green > 200 && red < 60 && blue < 60;
            if (isBackground) {
                pixels[index + 3] = 0;
            }
        }

        BitmapSource result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
