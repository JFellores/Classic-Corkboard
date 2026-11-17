using System.Drawing.Drawing2D;

namespace Corkboard;

internal sealed class BoardActionMenu : Panel
{
    private const int Inset = 7;
    private const int DividerHeight = 7;

    internal BoardActionMenu()
    {
        BackColor = BoardStyle.Paper;
        DoubleBuffered = true;
        Visible = false;
        AccessibleName = "Board menu";
    }

    internal void Fit(Rectangle available)
    {
        int inset = LogicalToDeviceUnits(Inset);
        int gap = LogicalToDeviceUnits(DividerHeight);
        int rows = Controls.OfType<BoardActionButton>().Count();
        int separators = Controls.Count - rows;
        int rowHeight = Math.Clamp((available.Height - inset * 2 - gap * separators) / Math.Max(1, rows), 0, LogicalToDeviceUnits(29));
        int width = Math.Min(available.Width, Math.Max(LogicalToDeviceUnits(218),
            Controls.OfType<BoardActionButton>().Max(button => button.LabelWidth) + inset * 2));
        int height = Math.Min(available.Height, inset * 2 + rows * rowHeight + separators * gap);

        SuspendLayout();
        Bounds = new Rectangle(available.Location, new Size(width, height));
        int y = inset;
        foreach (Control item in Controls)
        {
            if (item is BoardActionButton)
            {
                item.Bounds = new Rectangle(inset, y, Math.Max(0, width - inset * 2), rowHeight);
                y += rowHeight;
            }
            else
            {
                item.Bounds = new Rectangle(inset + LogicalToDeviceUnits(6), y + gap / 2,
                    Math.Max(0, width - LogicalToDeviceUnits((Inset + 6) * 2)), LogicalToDeviceUnits(1));
                y += gap;
            }
        }
        ResumeLayout();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        BoardStyle.RoundCorners(this, LogicalToDeviceUnits(10));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle border = Rectangle.Inflate(ClientRectangle, -1, -1);
        if (border.Width <= 0 || border.Height <= 0) return;
        using GraphicsPath shape = BoardStyle.RoundedOutline(border, LogicalToDeviceUnits(9));
        using var pen = new Pen(Color.FromArgb(208, 184, 153));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, shape);
    }
}

internal sealed class BoardActionButton : Button
{
    private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
        | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
    private readonly string glyph;
    private bool hovered;
    private bool? startupEnabled;

    internal BoardActionButton(string label, string glyph)
    {
        this.glyph = glyph;
        Text = AccessibleName = label;
        BackColor = BoardStyle.Paper;
        ForeColor = BoardStyle.Ink;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        Margin = Padding.Empty;
    }

    internal int LabelWidth => TextRenderer.MeasureText(Text, Font, Size.Empty, TextFlags).Width
        + LogicalToDeviceUnits(startupEnabled.HasValue ? 76 : 42);

    internal void ShowStartupState(bool enabled)
    {
        if (startupEnabled == enabled) return;
        startupEnabled = enabled;
        AccessibleDescription = enabled ? "On" : "Off";
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Rectangle bounds = ClientRectangle;
        if (bounds.Width < 2 || bounds.Height < 2) return;
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        bool focused = Focused && ShowFocusCues;
        if ((Enabled && hovered) || focused)
        {
            using GraphicsPath row = BoardStyle.RoundedOutline(Rectangle.Inflate(bounds, -1, -1), LogicalToDeviceUnits(5));
            using var fill = new SolidBrush(BoardStyle.PanelTint);
            if (hovered && Enabled) e.Graphics.FillPath(fill, row);
            if (focused)
            {
                using var pen = new Pen(BoardStyle.Brick);
                e.Graphics.DrawPath(pen, row);
            }
        }

        int labelLeft = LogicalToDeviceUnits(30);
        int right = bounds.Right - LogicalToDeviceUnits(8);
        Color ink = Enabled ? ForeColor : BoardStyle.Muted;
        var icon = new Rectangle(LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(23), bounds.Height);
        TextRenderer.DrawText(e.Graphics, glyph, Font, icon, Enabled ? BoardStyle.Brick : BoardStyle.Muted,
            TextFlags | TextFormatFlags.HorizontalCenter);
        if (startupEnabled is bool enabled)
        {
            int width = LogicalToDeviceUnits(29);
            int height = Math.Min(bounds.Height, LogicalToDeviceUnits(17));
            var badge = new Rectangle(right - width, (bounds.Height - height) / 2, width, height);
            using GraphicsPath shape = BoardStyle.RoundedOutline(badge, height / 2);
            using var fill = new SolidBrush(enabled ? BoardStyle.Brick : BoardStyle.PanelTint);
            e.Graphics.FillPath(fill, shape);
            TextRenderer.DrawText(e.Graphics, enabled ? "On" : "Off", Font, badge, enabled ? Color.White : BoardStyle.Ink,
                TextFlags | TextFormatFlags.HorizontalCenter);
            right = badge.Left - LogicalToDeviceUnits(6);
        }
        var label = new Rectangle(labelLeft, 0, Math.Max(0, right - labelLeft), bounds.Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, label, ink, TextFlags | TextFormatFlags.EndEllipsis);
    }
}

