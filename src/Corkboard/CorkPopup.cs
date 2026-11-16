using System.Drawing.Drawing2D;

namespace Corkboard;

internal sealed class CorkMenu : ContextMenuStrip
{
    private static readonly CorkRenderer MenuRenderer = new();

    public CorkMenu()
    {
        Renderer = MenuRenderer;
        Font = new Font("Segoe UI", 10F);
        BackColor = BoardStyle.Paper;
        ForeColor = BoardStyle.Ink;
        Padding = new Padding(6);
        ShowImageMargin = false;
        ShowCheckMargin = true;
    }

    public void SetSurface(Color color)
    {
        if (BackColor == color) return;
        BackColor = color;
        Renderer = new CorkRenderer(color);
    }

    private static Color Blend(Color surface, Color accent, int percent) => Color.FromArgb(
        (surface.R * (100 - percent) + accent.R * percent) / 100,
        (surface.G * (100 - percent) + accent.G * percent) / 100,
        (surface.B * (100 - percent) + accent.B * percent) / 100);

    protected override void OnItemAdded(ToolStripItemEventArgs e)
    {
        base.OnItemAdded(e);
        if (e.Item is not null) StyleItem(e.Item);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        BoardStyle.RoundCorners(this, LogicalToDeviceUnits(9));
    }

    private static void StyleItem(ToolStripItem item)
    {
        if (item is ToolStripMenuItem command)
        {
            command.Padding = new Padding(8, 5, 14, 5);
            command.DropDownOpening -= StyleSubmenu;
            command.DropDownOpening += StyleSubmenu;
        }
        else if (item is ToolStripLabel heading)
        {
            heading.ForeColor = BoardStyle.Muted;
            heading.Padding = new Padding(9, 5, 9, 3);
        }
    }

    private static void StyleSubmenu(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem { Owner: ToolStrip owner } command ||
            command.DropDown is not ToolStripDropDownMenu submenu) return;
        submenu.Renderer = owner.Renderer;
        submenu.BackColor = owner.BackColor;
        submenu.ForeColor = BoardStyle.Ink;
        submenu.Padding = new Padding(6);
        submenu.ShowImageMargin = false;
        submenu.ShowCheckMargin = true;
        submenu.Opened -= RoundSubmenu;
        submenu.Opened += RoundSubmenu;
        foreach (ToolStripItem item in submenu.Items) StyleItem(item);
    }

    private static void RoundSubmenu(object? sender, EventArgs e)
    {
        if (sender is ToolStripDropDownMenu submenu) BoardStyle.RoundCorners(submenu, submenu.LogicalToDeviceUnits(9));
    }

    private sealed class CorkRenderer : ToolStripProfessionalRenderer
    {
        private readonly Color? surface;
        private readonly Color ink;
        private readonly Color selection;
        private readonly Color borderColor;

        public CorkRenderer(Color? surface = null) : base(new CorkMenuColors(surface))
        {
            this.surface = surface;
            ink = surface is Color color ? SurfaceInk(color) : BoardStyle.Ink;
            selection = surface is Color fill ? Blend(fill, ink, 14) : BoardStyle.PanelTint;
            borderColor = surface is Color border ? Blend(border, ink, 28) : Color.FromArgb(208, 184, 153);
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            if (surface is not Color color) { base.OnRenderToolStripBackground(e); return; }
            using var brush = new SolidBrush(color);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (surface.HasValue) e.TextColor = ink;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle area = e.ImageRectangle;
            float x = area.Left + area.Width / 2f;
            float y = area.Top + area.Height / 2f;
            float scale = (e.ToolStrip?.DeviceDpi ?? 96) / 96f;
            using var pen = new Pen(ink, 1.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            GraphicsState state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, [new PointF(x - 4 * scale, y), new PointF(x - scale, y + 3 * scale), new PointF(x + 4 * scale, y - 3 * scale)]);
            e.Graphics.Restore(state);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled || e.Item.Width < 10 || e.Item.Height < 8) return;
            using GraphicsPath shape = BoardStyle.RoundedOutline(new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2), 5);
            using var fill = new SolidBrush(selection);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(fill, shape);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle border = e.ToolStrip.ClientRectangle;
            if (border.Width < 3 || border.Height < 3) return;
            border.Width--;
            border.Height--;
            using GraphicsPath outline = BoardStyle.RoundedOutline(border, e.ToolStrip.LogicalToDeviceUnits(9));
            using var pen = new Pen(borderColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, outline);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = surface.HasValue ? ink : BoardStyle.Muted;
            base.OnRenderArrow(e);
        }
    }

    private static Color SurfaceInk(Color surface) =>
        (surface.R * 299 + surface.G * 587 + surface.B * 114) >= 140_000 ? BoardStyle.Ink : Color.White;

    private sealed class CorkMenuColors(Color? surface) : ProfessionalColorTable
    {
        private readonly Color paper = surface ?? BoardStyle.Paper;
        private readonly Color detail = surface is Color color ? Blend(color, SurfaceInk(color), 20) : BoardStyle.PanelTint;
        public override Color ToolStripDropDownBackground => paper;
        public override Color ImageMarginGradientBegin => paper;
        public override Color ImageMarginGradientMiddle => paper;
        public override Color ImageMarginGradientEnd => paper;
        public override Color MenuBorder => detail;
        public override Color SeparatorDark => detail;
        public override Color SeparatorLight => paper;
        public override Color CheckBackground => detail;
        public override Color CheckSelectedBackground => detail;
        public override Color CheckPressedBackground => detail;
    }
}

internal sealed class CorkDialog : Form
{
    public CorkDialog(string title, int height)
    {
        Text = title;
        ClientSize = new Size(372, height);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10F);
        BackColor = BoardStyle.Paper;
        ForeColor = BoardStyle.Ink;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BoardStyle.RoundCorners(this, LogicalToDeviceUnits(12));

        Controls.Add(new Label
        {
            Text = title, Bounds = new Rectangle(22, 17, 290, 31),
            Font = new Font("Segoe UI Semibold", 15F), TextAlign = ContentAlignment.MiddleLeft
        });
        Button close = BoardStyle.Button("×", BoardStyle.Paper, BoardStyle.Muted);
        close.AccessibleName = "Close dialog";
        close.Font = new Font("Segoe UI", 13F);
        close.Bounds = new Rectangle(326, 15, 30, 30);
        close.DialogResult = DialogResult.Cancel;
        close.TabStop = false;
        Controls.Add(close);
    }

    public Button AddActions(string primaryLabel, bool acceptWithEnter)
    {
        int y = ClientSize.Height - 52;
        Button primary = BoardStyle.Button(primaryLabel, BoardStyle.Brick, Color.White);
        primary.Bounds = new Rectangle(232, y, 118, 34);
        primary.DialogResult = DialogResult.OK;
        Button cancel = BoardStyle.Button("Cancel", BoardStyle.PanelTint, BoardStyle.Ink);
        cancel.Bounds = new Rectangle(140, y, 84, 34);
        cancel.DialogResult = DialogResult.Cancel;
        Controls.AddRange([cancel, primary]);
        CancelButton = cancel;
        AcceptButton = acceptWithEnter ? primary : cancel;
        return cancel;
    }

    public static bool Confirm(IWin32Window owner, string title, string message, string action)
    {
        using var dialog = new CorkDialog(title, 170);
        dialog.Controls.Add(new Label { Text = message, Bounds = new Rectangle(22, 59, 328, 52) });
        Button cancel = dialog.AddActions(action, acceptWithEnter: false);
        dialog.Shown += (_, _) => cancel.Focus();
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        BoardStyle.RoundCorners(this, LogicalToDeviceUnits(12));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle border = ClientRectangle;
        if (border.Width < 3 || border.Height < 3) return;
        border.Width--;
        border.Height--;
        using GraphicsPath outline = BoardStyle.RoundedOutline(border, LogicalToDeviceUnits(12));
        using var pen = new Pen(Color.FromArgb(208, 184, 153));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, outline);
    }
}

internal static class NamePrompt
{
    public static string? Show(IWin32Window owner, string caption, string label, string value, int maxLength)
    {
        using var form = new CorkDialog(caption, 180);
        var prompt = new Label { Text = label, Bounds = new Rectangle(22, 58, 328, 21) };
        var field = new Panel { Bounds = new Rectangle(22, 81, 328, 33), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(9, 6, 9, 4) };
        var entry = new TextBox { Text = value, MaxLength = maxLength, Dock = DockStyle.Top, AccessibleName = label, BorderStyle = BorderStyle.None };
        field.Controls.Add(entry);
        form.Controls.AddRange([prompt, field]);
        form.AddActions("Save", acceptWithEnter: true);
        form.Shown += (_, _) => { entry.Focus(); entry.SelectAll(); };
        return form.ShowDialog(owner) == DialogResult.OK ? entry.Text.Trim() : null;
    }
}
