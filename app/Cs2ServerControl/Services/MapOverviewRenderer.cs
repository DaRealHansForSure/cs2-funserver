using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cs2ServerControl.Services;

/// <summary>
/// Draws the map wireframe straight from above into a bitmap, colored by
/// height like the live map, for the map details dialog. Works for every map,
/// unlike the radar images, which only the stock maps have.
/// </summary>
public static class MapOverviewRenderer {
    public static BitmapSource Render(BspWireframe wireframe, int size, Color background, Color low, Color high) {
        float width = wireframe.BoundsMax.X - wireframe.BoundsMin.X;
        float depth = wireframe.BoundsMax.Y - wireframe.BoundsMin.Y;
        double margin = size * 0.04;
        double scale = (size - 2 * margin) / Math.Max(1, Math.Max(width, depth));
        // Center the shorter side.
        double offsetX = margin + (size - 2 * margin - width * scale) / 2;
        double offsetY = margin + (size - 2 * margin - depth * scale) / 2;
        float heightRange = Math.Max(1, wireframe.MaxZ - wireframe.MinZ);

        // Few pens instead of one per line: heights are bucketed into 16 shades.
        const int Shades = 16;
        var pens = new Pen[Shades];
        for (int shade = 0; shade < Shades; shade++) {
            double amount = shade / (double)(Shades - 1);
            var color = Color.FromRgb(
                (byte)(low.R + (high.R - low.R) * amount),
                (byte)(low.G + (high.G - low.G) * amount),
                (byte)(low.B + (high.B - low.B) * amount));
            pens[shade] = new Pen(new SolidColorBrush(color), 1);
            pens[shade].Freeze();
        }

        // Low lines first, so the upper floors are drawn on top.
        var segments = wireframe.Segments
            .OrderBy(segment => Math.Max(segment.Start.Z, segment.End.Z))
            .ToList();

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen()) {
            context.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, size, size));
            foreach ((System.Numerics.Vector3 start, System.Numerics.Vector3 end) in segments) {
                float topZ = Math.Max(start.Z, end.Z);
                int shade = (int)Math.Clamp((topZ - wireframe.MinZ) / heightRange * (Shades - 1), 0, Shades - 1);
                // Y points north in the game, which is up on screen.
                var from = new Point(offsetX + (start.X - wireframe.BoundsMin.X) * scale, size - offsetY - (start.Y - wireframe.BoundsMin.Y) * scale);
                var to = new Point(offsetX + (end.X - wireframe.BoundsMin.X) * scale, size - offsetY - (end.Y - wireframe.BoundsMin.Y) * scale);
                context.DrawLine(pens[shade], from, to);
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
