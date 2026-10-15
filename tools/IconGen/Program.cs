// Draws the Pulse icon (a red heartbeat trace on a dark rounded square) at every size Windows
// asks for, and packs them into an .ico. Small sizes use a simpler trace so it stays legible.
//
//   dotnet run --project tools/IconGen -- <output folder>

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var output = args.Length > 0 ? args[0] : Path.Combine("src", "Pulse", "Resources");
Directory.CreateDirectory(output);

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = icoSizes.ToDictionary(size => size, Render);

WriteIco(Path.Combine(output, "Pulse.ico"), pngs);
File.WriteAllBytes(Path.Combine(output, "Pulse-64.png"), pngs[64]);
File.WriteAllBytes(Path.Combine(output, "Pulse-256.png"), pngs[256]);
Console.WriteLine($"Wrote Pulse.ico ({string.Join(", ", icoSizes)} px) and PNGs to {Path.GetFullPath(output)}");

static byte[] Render(int size)
{
    var s = (double)size;
    var brand = Color.FromRgb(0xFF, 0x3B, 0x5C);

    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        // Background: a dark rounded square with a faint top highlight and edge.
        var inset = Math.Max(0.5, s * 0.03);
        var rect = new Rect(inset, inset, s - inset * 2, s - inset * 2);
        var radius = s * 0.22;
        var background = new LinearGradientBrush(Color.FromRgb(0x24, 0x27, 0x30), Color.FromRgb(0x12, 0x13, 0x18), 90);
        var edge = s >= 32 ? new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), Math.Max(1, s / 128)) : null;
        dc.DrawRoundedRectangle(background, edge, rect, radius, radius);

        // The trace, in a 100×100 design space. Small icons drop the side bumps.
        var points = size <= 24
            ? new[] { (16.0, 55.0), (36, 55), (45, 22), (57, 80), (65, 55), (84, 55) }
            : new[] { (17.0, 55.0), (31, 55), (36, 48), (41, 55), (47, 55), (55, 24), (64, 78), (70, 55), (75, 55), (79, 49), (83, 55) };

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Scale(points[0], s), false, false);
            foreach (var p in points.Skip(1))
                ctx.LineTo(Scale(p, s), true, true);
        }
        geometry.Freeze();

        var thickness = Math.Max(1.6, s * 0.06);
        // Keep everything inside the rounded square.
        dc.PushClip(new RectangleGeometry(rect, radius, radius));
        // A soft halo under the line (no blur effect, so small sizes stay crisp); skipped where it would smudge.
        if (size >= 32)
            dc.DrawGeometry(null, RoundPen(Color.FromArgb(0x30, brand.R, brand.G, brand.B), thickness * 2.2), geometry);
        dc.DrawGeometry(null, RoundPen(brand, thickness), geometry);
        dc.Pop();
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

static Point Scale((double X, double Y) p, double size) => new(p.X / 100 * size, p.Y / 100 * size);

static Pen RoundPen(Color color, double thickness) => new(new SolidColorBrush(color), thickness)
{
    StartLineCap = PenLineCap.Round,
    EndLineCap = PenLineCap.Round,
    LineJoin = PenLineJoin.Round,
};

// ICO with PNG-compressed entries (supported by Windows since Vista).
static void WriteIco(string path, Dictionary<int, byte[]> images)
{
    using var file = File.Create(path);
    using var writer = new BinaryWriter(file);
    writer.Write((ushort)0);            // reserved
    writer.Write((ushort)1);            // type: icon
    writer.Write((ushort)images.Count);

    var offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        writer.Write((byte)(size >= 256 ? 0 : size)); // width; 0 means 256
        writer.Write((byte)(size >= 256 ? 0 : size)); // height
        writer.Write((byte)0);                         // palette size
        writer.Write((byte)0);                         // reserved
        writer.Write((ushort)1);                       // color planes
        writer.Write((ushort)32);                      // bits per pixel
        writer.Write(png.Length);
        writer.Write(offset);
        offset += png.Length;
    }
    foreach (var png in images.Values)
        writer.Write(png);
}
