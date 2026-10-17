using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cs2ServerControl.LiveMap;

/// <summary>
/// Draws the ViewCube faces in the app's colors. Helix expects one 6:1 strip
/// with the faces in the order front, back, left, right, top, bottom.
/// </summary>
public static class ViewCubeTexture {
    private const int FaceSize = 100;
    private static readonly string[] FaceLabels = ["FRONT", "BACK", "LEFT", "RIGHT", "TOP", "BOTTOM"];

    public static Stream Create(FrameworkElement resources) {
        var faceBrush = new SolidColorBrush((Color)resources.FindResource("ControlBackgroundColor"));
        var edgePen = new Pen(new SolidColorBrush((Color)resources.FindResource("ControlBorderColor")), 2);
        var textBrush = new SolidColorBrush((Color)resources.FindResource("TextSecondaryColor"));
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen()) {
            for (int face = 0; face < FaceLabels.Length; face++) {
                var bounds = new Rect(face * FaceSize, 0, FaceSize, FaceSize);
                context.DrawRectangle(faceBrush, edgePen, bounds);

                var text = new FormattedText(FaceLabels[face], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, 22, textBrush, 1.0);
                context.DrawText(text, new Point(bounds.Left + (FaceSize - text.Width) / 2, (FaceSize - text.Height) / 2));
            }
        }

        var bitmap = new RenderTargetBitmap(FaceSize * FaceLabels.Length, FaceSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return stream;
    }
}
