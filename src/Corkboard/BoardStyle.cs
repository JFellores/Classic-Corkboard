using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Corkboard;

internal static class BoardStyle
{
    public static readonly Color Ink = Color.FromArgb(45, 35, 28);
    public static readonly Color Muted = Color.FromArgb(107, 88, 73);
    public static readonly Color Paper = Color.FromArgb(251, 246, 235);
    public static readonly Color Brick = Color.FromArgb(135, 57, 43);
    public static readonly Color PanelTint = Color.FromArgb(234, 220, 194);
    public static readonly Color Cork = Color.FromArgb(203, 151, 104);
    public static readonly Bitmap CorkTile = CreateCorkTile();
    private static readonly Bitmap WoodGrain = CreateWoodGrain();
    public static readonly (string Name, Color Color)[] ThreadColors =
    [
        ("Red", Color.FromArgb(145, 44, 40)), ("Blue", Color.FromArgb(32, 80, 129)),
        ("Green", Color.FromArgb(43, 99, 65)), ("Purple", Color.FromArgb(103, 55, 122)),
        ("Cream", Color.FromArgb(255, 242, 199)), ("Charcoal", Color.FromArgb(54, 43, 37))
    ];
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

    private static Bitmap CreateCorkTile()
    {
        var tile = new Bitmap(240, 240, PixelFormat.Format24bppRgb);
        using var surface = Graphics.FromImage(tile);
        surface.Clear(Cork);
        var random = new Random(79);
        Color[] flecks = [Color.FromArgb(106, 118, 69, 41), Color.FromArgb(100, 227, 173, 122),
            Color.FromArgb(98, 249, 206, 159), Color.FromArgb(70, 143, 91, 61), Color.FromArgb(100, 209, 151, 106)];
        using var brush = new SolidBrush(Color.White);
        for (int index = 0; index < 12000; index++)
        {
            brush.Color = flecks[random.Next(flecks.Length)];
            surface.FillEllipse(brush, random.Next(240), random.Next(240), random.Next(1, 5), random.Next(1, 4));
        }
        return tile;
    }

    public static void DrawPin(Graphics graphics, Point center, bool pinned = true)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (!pinned)
        {
            using var ring = new Pen(Color.FromArgb(147, 91, 63), 1.5f);
            graphics.DrawEllipse(ring, center.X - 5, center.Y - 5, 10, 10);
            return;
        }
        using var shadow = new SolidBrush(Color.FromArgb(64, 60, 29, 23));
        using var pin = new SolidBrush(Brick);
        using var light = new SolidBrush(Color.FromArgb(208, 115, 95));
        graphics.FillEllipse(shadow, center.X - 3, center.Y - 3, 12, 12);
        graphics.FillEllipse(pin, center.X - 6, center.Y - 6, 12, 12);
        graphics.FillEllipse(light, center.X - 4, center.Y - 4, 5, 5);
    }

    private static Bitmap CreateWoodGrain()
    {
        const int length = 480;
        const int depth = 32;
        var bitmap = new Bitmap(length, depth, PixelFormat.Format24bppRgb);
        var random = new Random(326);
        int[] grain = Enumerable.Range(0, depth).Select(_ => random.Next(-13, 8)).ToArray();
        for (int y = 0; y < depth; y++)
        {
            for (int x = 0; x < length; x++)
            {
                double wave = 1.1 * Math.Sin(x / 95.0 + y / 7.0) + 0.45 * Math.Sin(x / 34.0);
                int row = Math.Clamp((int)Math.Round(y + wave), 0, depth - 1);
                int detail = grain[row] + random.Next(-3, 5);
                int bevel = y < 4 ? 8 : y > 27 ? -16 : y == 11 ? -6 : 0;
                bitmap.SetPixel(x, y, Color.FromArgb(Math.Min(255, 240 + detail + bevel), 194 + detail + bevel, 140 + detail + bevel));
            }
        }
        return bitmap;
    }

    public static void DrawFrame(Graphics graphics, int width, int height, int frame)
    {
        if (width < frame * 2 || height < frame * 2) return;
        DrawSide([new(0, 0), new(width, 0), new(width - frame, frame), new(frame, frame)],
            [new(0, 0), new(width, 0), new(0, frame)]);
        DrawSide([new(width, 0), new(width, height), new(width - frame, height - frame), new(width - frame, frame)],
            [new(width, 0), new(width, height), new(width - frame, 0)]);
        DrawSide([new(width, height), new(0, height), new(frame, height - frame), new(width - frame, height - frame)],
            [new(width, height), new(0, height), new(width, height - frame)]);
        DrawSide([new(0, height), new(0, 0), new(frame, frame), new(frame, height - frame)],
            [new(0, height), new(0, 0), new(frame, height)]);

        using var border = new Pen(Color.FromArgb(147, 96, 55));
        graphics.DrawRectangle(border, frame - 1, frame - 1, width - frame * 2 + 1, height - frame * 2 + 1);
        graphics.DrawRectangle(border, 0, 0, width - 1, height - 1);
        graphics.DrawLine(border, 0, 0, frame, frame);
        graphics.DrawLine(border, width - 1, 0, width - frame, frame);
        graphics.DrawLine(border, width - 1, height - 1, width - frame, height - frame);
        graphics.DrawLine(border, 0, height - 1, frame, height - frame);

        void DrawSide(Point[] polygon, Point[] destination)
        {
            using var path = new GraphicsPath();
            path.AddPolygon(polygon);
            GraphicsState original = graphics.Save();
            graphics.SetClip(path, CombineMode.Intersect);
            graphics.DrawImage(WoodGrain, destination);
            graphics.Restore(original);
        }
    }
}
