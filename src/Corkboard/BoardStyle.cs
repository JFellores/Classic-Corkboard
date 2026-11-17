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
    public static readonly (string Name, Color Color)[] PinColors =
    [
        ("Red", Brick), ("Amber", Color.FromArgb(165, 88, 13)),
        ("Green", Color.FromArgb(41, 108, 70)), ("Blue", Color.FromArgb(36, 91, 153)),
        ("Purple", Color.FromArgb(105, 58, 144)), ("Charcoal", Color.FromArgb(65, 61, 60))
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

    internal static readonly (string Name, Color Color)[] TextColors =
    [
        ("Black", BoardStyle.Ink), ("Red", Color.FromArgb(163, 39, 51)), ("Blue", Color.FromArgb(48, 71, 174)),
        ("Green", Color.FromArgb(27, 105, 60)), ("Purple", Color.FromArgb(120, 57, 142))
    ];

    internal static void PaintColorSwatch(object? sender, PaintEventArgs e)
    {
        if (sender is not RadioButton swatch) return;
        int size = swatch.LogicalToDeviceUnits(13);
        var dot = new Rectangle((swatch.Width - size) / 2, (swatch.Height - size) / 2, size, size);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(swatch.ForeColor);
        e.Graphics.FillEllipse(fill, dot);
        if (!swatch.Checked) return;
        int gap = swatch.LogicalToDeviceUnits(2);
        using var outline = new Pen(swatch.ForeColor, swatch.LogicalToDeviceUnits(1));
        e.Graphics.DrawEllipse(outline, Rectangle.Inflate(dot, gap, gap));
    }

    public static Button Button(string text, Color fill, Color ink, bool compactText = false)
    {
        var button = new CommandButton(compactText)
        {
            Text = text, BackColor = fill, ForeColor = ink, FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand, UseVisualStyleBackColor = false, Margin = Padding.Empty
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    public const int Linen = 5;

    public static Color NoteAccent(int color) => color switch
    {
        0 => PinColors[1].Color,
        1 => PinColors[0].Color,
        2 => PinColors[2].Color,
        3 => PinColors[3].Color,
        4 => PinColors[4].Color,
        _ => Color.FromArgb(123, 96, 59)
    };

    public static Color PhotoPaper(int color)
    {
        Color tint = Colors[Math.Clamp(color, 0, Colors.Length - 1)].Paper;
        return Color.FromArgb((249 * 4 + tint.R) / 5, (244 * 4 + tint.G) / 5, (229 * 4 + tint.B) / 5);
    }

    public static void RoundCorners(Control control, int radius)
    {
        if (control.Width <= 0 || control.Height <= 0) return;
        using GraphicsPath outline = RoundedOutline(control.ClientRectangle, radius);
        Region? old = control.Region;
        control.Region = new Region(outline);
        old?.Dispose();
    }

    public static GraphicsPath RoundedOutline(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int corner = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (corner <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, corner, corner, 180, 90);
        path.AddArc(bounds.Right - corner, bounds.Top, corner, corner, 270, 90);
        path.AddArc(bounds.Right - corner, bounds.Bottom - corner, corner, corner, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - corner, corner, corner, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class CommandButton(bool compactText) : Button
    {
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            RoundCorners(this, LogicalToDeviceUnits(5));
        }

        protected override void OnBackColorChanged(EventArgs e)
        {
            base.OnBackColorChanged(e);
            Color tint = ForeColor;
            Color surface = BackColor;
            FlatAppearance.MouseOverBackColor = Color.FromArgb(
                (surface.R * 9 + tint.R) / 10, (surface.G * 9 + tint.G) / 10, (surface.B * 9 + tint.B) / 10);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (compactText)
            {
                bool hovered = Enabled && ClientRectangle.Contains(PointToClient(Cursor.Position));
                using var fill = new SolidBrush(hovered ? FlatAppearance.MouseOverBackColor : BackColor);
                e.Graphics.FillRectangle(fill, ClientRectangle);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Muted,
                    TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            else base.OnPaint(e);
            if (!Focused || !ShowFocusCues || Width < 10 || Height < 10) return;
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), ForeColor, BackColor);
        }
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

    public static void DrawPin(Graphics graphics, Point center, bool pinned = true, Color? color = null, bool target = false)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color selected = color ?? Brick;
        if (target)
        {
            using var indicator = new Pen(Ink, 1.5f);
            graphics.DrawEllipse(indicator, center.X - 9, center.Y - 9, 18, 18);
        }
        if (!pinned)
        {
            using var ring = new Pen(selected, 1.5f);
            graphics.DrawEllipse(ring, center.X - 5, center.Y - 5, 10, 10);
            return;
        }
        using var shadow = new SolidBrush(Color.FromArgb(64, 60, 29, 23));
        using var pin = new SolidBrush(selected);
        using var light = new SolidBrush(Color.FromArgb((selected.R + 255) / 2, (selected.G + 255) / 2, (selected.B + 255) / 2));
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

    public static void DrawFrame(Graphics graphics, int width, int height, int frame, int topFrame)
    {
        if (width < frame * 2 || height < topFrame + frame) return;
        DrawSide([new(0, 0), new(width, 0), new(width - frame, topFrame), new(frame, topFrame)],
            [new(0, 0), new(width, 0), new(0, topFrame)]);
        DrawSide([new(width, 0), new(width, height), new(width - frame, height - frame), new(width - frame, topFrame)],
            [new(width, 0), new(width, height), new(width - frame, 0)]);
        DrawSide([new(width, height), new(0, height), new(frame, height - frame), new(width - frame, height - frame)],
            [new(width, height), new(0, height), new(width, height - frame)]);
        DrawSide([new(0, height), new(0, 0), new(frame, topFrame), new(frame, height - frame)],
            [new(0, height), new(0, 0), new(frame, height)]);

        using var border = new Pen(Color.FromArgb(147, 96, 55));
        graphics.DrawRectangle(border, frame - 1, topFrame - 1, width - frame * 2 + 1, height - frame - topFrame + 1);
        graphics.DrawRectangle(border, 0, 0, width - 1, height - 1);
        graphics.DrawLine(border, 0, 0, frame, topFrame);
        graphics.DrawLine(border, width - 1, 0, width - frame, topFrame);
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
