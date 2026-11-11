using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Corkboard;

internal static class BoardStyle
{
    public static readonly Color Ink = Color.FromArgb(45, 35, 28);
    public static readonly Color Muted = Color.FromArgb(107, 88, 73);
    public static readonly Color Paper = Color.FromArgb(251, 246, 235);
    public static readonly Color Brick = Color.FromArgb(135, 57, 43);
    public static readonly (string Name, Color Paper, Color Edge)[] Colors =
    [
        ("Butter", Color.FromArgb(255, 240, 173), Color.FromArgb(239, 218, 130)),
        ("Rose", Color.FromArgb(255, 218, 213), Color.FromArgb(237, 181, 174)),
        ("Sage", Color.FromArgb(222, 238, 194), Color.FromArgb(190, 215, 157)),
        ("Sky", Color.FromArgb(211, 233, 247), Color.FromArgb(171, 204, 228)),
        ("Lilac", Color.FromArgb(234, 221, 249), Color.FromArgb(208, 186, 230)),
        ("Linen", Color.FromArgb(255, 247, 229), Color.FromArgb(230, 215, 183))
    ];

    public static Button Button(string text, Color fill, Color ink)
    {
        var button = new Button
        {
            Text = text, BackColor = fill, ForeColor = ink, FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand, UseVisualStyleBackColor = false, Margin = Padding.Empty
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    public static Bitmap CreateCorkTile()
    {
        var tile = new Bitmap(240, 240, PixelFormat.Format24bppRgb);
        using var surface = Graphics.FromImage(tile);
        surface.Clear(Color.FromArgb(174, 125, 86));
        var random = new Random(79);
        Color[] flecks = [Color.FromArgb(90, 109, 65, 38), Color.FromArgb(100, 214, 160, 110),
            Color.FromArgb(80, 237, 188, 135), Color.FromArgb(68, 127, 82, 52), Color.FromArgb(90, 194, 137, 94)];
        using var brush = new SolidBrush(Color.White);
        for (int index = 0; index < 6600; index++)
        {
            brush.Color = flecks[random.Next(flecks.Length)];
            surface.FillEllipse(brush, random.Next(240), random.Next(240), random.Next(1, 8), random.Next(1, 5));
        }
        return tile;
    }

    public static void DrawPin(Graphics graphics, Point center)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shadow = new SolidBrush(Color.FromArgb(64, 60, 29, 23));
        using var pin = new SolidBrush(Brick);
        using var light = new SolidBrush(Color.FromArgb(208, 115, 95));
        graphics.FillEllipse(shadow, center.X - 3, center.Y - 3, 12, 12);
        graphics.FillEllipse(pin, center.X - 6, center.Y - 6, 12, 12);
        graphics.FillEllipse(light, center.X - 4, center.Y - 4, 5, 5);
    }
}
