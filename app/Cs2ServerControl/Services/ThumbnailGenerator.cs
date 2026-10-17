using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cs2ServerControl.Services;

/// <summary>
/// Renders a picture for a map that has none: the top-down view of its
/// geometry, saved as tools\previews\auto\&lt;map&gt;.png. A real screenshot
/// would need the game client to load the map in a window, which would take
/// the focus from whoever plays on this PC.
/// </summary>
public sealed class ThumbnailGenerator {
    private const int Size = 320;

    private readonly PackagePaths _paths;
    private readonly Color _background;
    private readonly Color _low;
    private readonly Color _high;

    public ThumbnailGenerator(PackagePaths paths, Color background, Color low, Color high) {
        _paths = paths;
        _background = background;
        _low = low;
        _high = high;
    }

    /// <summary>Must run on the UI thread: WPF renders the bitmap there. False when the map could not be read.</summary>
    public async Task<bool> GenerateAsync(string mapName) {
        string bspFile = _paths.MapFile(mapName);
        if (!File.Exists(bspFile)) {
            return false;
        }

        try {
            BspWireframe wireframe = await BspWireframeLoader.LoadAsync(bspFile);
            BitmapSource picture = MapOverviewRenderer.Render(wireframe, Size, _background, _low, _high);

            Directory.CreateDirectory(_paths.GeneratedPreviewsDir);
            string target = Path.Combine(_paths.GeneratedPreviewsDir, mapName + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(picture));
            await using (var stream = File.Create(target)) {
                encoder.Save(stream);
            }
            MapThumbnails.Forget(mapName);
            return true;
        } catch (Exception exception) when (exception is IOException or BspFormatException or UnauthorizedAccessException) {
            return false;
        }
    }
}
