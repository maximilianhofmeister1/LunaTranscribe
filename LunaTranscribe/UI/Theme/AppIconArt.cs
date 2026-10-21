using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LunaTranscribe.Core;

namespace LunaTranscribe.UI.Theme;

/// <summary>
/// Das App-Icon „Mondnacht“ (Mondsichel, Wellenform, Sterne) als Vektorzeichnung – Port von
/// scripts/make-icon.swift der macOS-Version. Dient im Fenster als Logo und erzeugt per Selbsttest die .ico-Datei.
/// </summary>
public static class AppIconArt
{
    private static DrawingImage? _image;

    /// <summary>Logo für Seitenleiste und Popover.</summary>
    public static DrawingImage Image => _image ??= Freeze(new DrawingImage(Create()));

    /// <summary>Zeichnung im 1024er-Raster; der Körper füllt das Raster bis auf einen schmalen Rand (Windows-Icons sind größer als macOS-Icons).</summary>
    public static Drawing Create()
    {
        var nightTop = LunaTheme.NightTop;
        var nightBottom = LunaTheme.NightBottom;
        var moonlight = LunaTheme.Moonlight;
        var honey = LunaTheme.HoneyNight;
        var lavender = LunaTheme.LavenderNight;

        // Koordinaten wie im Swift-Skript (Ursprung unten links) → WPF (oben links): y' = 1024 - y.
        static Point P(double x, double y) => new(x, 1024 - y);

        var body = new Rect(100, 100, 824, 824);
        var shape = new RectangleGeometry(body, 185, 185);
        var content = new DrawingGroup { ClipGeometry = shape };

        // Nachthimmel
        content.Children.Add(new GeometryDrawing(new LinearGradientBrush(nightTop, nightBottom, 90), null, shape));

        // Warmer Schein hinter dem Mond
        var glow = new RadialGradientBrush(WithAlpha(honey, 0.45), WithAlpha(honey, 0))
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = P(440, 520), GradientOrigin = P(440, 520), RadiusX = 430, RadiusY = 430,
        };
        content.Children.Add(new GeometryDrawing(glow, null, shape));

        // Sterne (vierzackig, verspielt verteilt)
        (double x, double y, double r, double a)[] stars =
            { (770, 850, 28, 0.95), (865, 700, 14, 0.7), (235, 815, 18, 0.75), (600, 885, 10, 0.6), (190, 300, 12, 0.5), (820, 250, 10, 0.45) };
        foreach (var (x, y, r, a) in stars)
        {
            var p = P(x, y);
            content.Children.Add(new GeometryDrawing(new SolidColorBrush(WithAlpha(moonlight, a)), null, LunaIcon.SparklePath(p.X, p.Y, r)));
        }

        // Mondsichel mit weichem Schein
        var moonCenter = P(440, 520);
        var hollowCenter = P(590, 595);
        var crescent = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(moonCenter, 255, 255), new EllipseGeometry(hollowCenter, 215, 215));
        for (var i = 3; i >= 1; i--)
        {
            var halo = new Pen(new SolidColorBrush(WithAlpha(honey, 0.10)), i * 22) { LineJoin = PenLineJoin.Round };
            content.Children.Add(new GeometryDrawing(null, halo, crescent));
        }
        content.Children.Add(new GeometryDrawing(new SolidColorBrush(moonlight), null, crescent));

        // Wellenform in der Aussparung der Sichel – der Mond „spricht“
        double[] bars = { 64, 140, 220, 120, 176, 84 };
        const double barWidth = 30, gap = 20;
        var totalWidth = bars.Length * barWidth + (bars.Length - 1) * gap;
        var bx = hollowCenter.X - totalWidth / 2 + 20;
        foreach (var h in bars)
        {
            var rect = new Rect(bx, hollowCenter.Y - h / 2, barWidth, h);
            var brush = new LinearGradientBrush(honey, lavender, new Point(0, 1), new Point(0, 0));
            content.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(rect, barWidth / 2, barWidth / 2)));
            bx += barWidth + gap;
        }

        // Leichter Glanz oben
        var sheen = new LinearGradientBrush(WithAlpha(Colors.White, 0.10), WithAlpha(Colors.White, 0), new Point(0, 0), new Point(0, 0.5));
        content.Children.Add(new GeometryDrawing(sheen, null, shape));

        // Auf das Raster vergrößern (Rand 40 px) – unter Windows wirken Icons mit macOS-Rand zu klein.
        const double margin = 40;
        var scale = (1024 - 2 * margin) / body.Width;
        content.Transform = new MatrixTransform(scale, 0, 0, scale, margin - body.X * scale, margin - body.Y * scale);

        var root = new DrawingGroup();
        root.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 1024, 1024))));
        root.Children.Add(content);
        return Freeze(root);
    }

    public static BitmapSource Render(int pixels)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(pixels / 1024.0, pixels / 1024.0));
            dc.DrawDrawing(Create());
        }
        var bmp = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return bmp;
    }

    /// <summary>Schreibt die .ico (PNG-Einträge in allen üblichen Größen) und optional eine 1024er-Vorschau.</summary>
    [SelfTest("--make-icon")]
    private static Task<int> MakeIcon(string[] args)
    {
        var target = args.Length > 0 ? args[0] : "LunaTranscribe.ico";
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
        var images = new List<byte[]>();
        foreach (var size in sizes)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Render(size)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            images.Add(ms.ToArray());
        }
        using (var fs = File.Create(target))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            var offset = 6 + 16 * sizes.Length;
            for (var i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
        SelfTests.Print("Icon geschrieben: " + Path.GetFullPath(target));
        if (args.Length > 1)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Render(1024)));
            using var fs = File.Create(args[1]);
            enc.Save(fs);
            SelfTests.Print("Vorschau: " + Path.GetFullPath(args[1]));
        }
        return Task.FromResult(0);
    }

    private static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B);

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
