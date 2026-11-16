using System.Drawing.Drawing2D;
using System.Globalization;

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
        if (sender is not ToolStripMenuItem command || command.GetCurrentParent() is not ToolStrip owner ||
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

internal sealed class CorkSubmenuItem(string text) : ToolStripMenuItem(text)
{
    protected override Point DropDownLocation
    {
        get
        {
            if (GetCurrentParent() is not ToolStripDropDown parent) return base.DropDownLocation;
            Rectangle menu = parent.RectangleToScreen(parent.ClientRectangle);
            Rectangle workArea = Screen.FromRectangle(menu).WorkingArea;
            Size size = DropDown.GetPreferredSize(Size.Empty);
            int overlap = parent.LogicalToDeviceUnits(1);
            int right = menu.Right - overlap;
            int left = menu.Left + overlap;
            int roomRight = workArea.Right - right;
            int roomLeft = left - workArea.Left;
            int x = roomRight >= size.Width || roomRight >= roomLeft ? right : left - size.Width;
            return new Point(
                Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width)),
                Math.Clamp(menu.Top + Bounds.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height)));
        }
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

internal static class NoteLinkPrompt
{
    private static readonly (string Name, Color Color)[] Colors =
    [
        ("Black", BoardStyle.Ink), ("Red", Color.FromArgb(163, 39, 51)), ("Blue", Color.FromArgb(48, 71, 174)),
        ("Green", Color.FromArgb(27, 105, 60)), ("Purple", Color.FromArgb(120, 57, 142))
    ];

    public static (string Alias, Uri Destination, Color Color)? Show(IWin32Window owner, string selection)
    {
        using var dialog = new CorkDialog("Insert link", 264);
        using var swatchTips = new ToolTip();
        string initialText = selection.Length <= 160 && !selection.Any(char.IsControl) ? selection : "";
        TextBox display = AddField("Text to display", "Video Link", initialText, 160, 55);
        Color selectedColor = Colors[0].Color;
        display.ForeColor = selectedColor;
        var palette = new Panel { Bounds = new Rectangle(196, 52, 154, 23), AccessibleName = "Link color" };
        palette.Controls.Add(new Label
        {
            Text = "Color", ForeColor = BoardStyle.Muted,
            TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(0, 0, 43, 23)
        });
        for (int i = 0; i < Colors.Length; i++)
        {
            (string name, Color color) = Colors[i];
            var swatch = new RadioButton
            {
                Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                Bounds = new Rectangle(43 + i * 22, 1, 21, 21), BackColor = BoardStyle.Paper,
                ForeColor = color, AccessibleName = name + " link color", Cursor = Cursors.Hand
            };
            swatch.FlatAppearance.BorderSize = 0;
            swatch.FlatAppearance.CheckedBackColor = BoardStyle.Paper;
            swatch.Paint += PaintSwatch;
            swatch.CheckedChanged += (_, _) =>
            {
                if (swatch.Checked) display.ForeColor = selectedColor = color;
                swatch.Invalidate();
            };
            swatch.Checked = i == 0;
            swatchTips.SetToolTip(swatch, name);
            palette.Controls.Add(swatch);
        }
        dialog.Controls.Add(palette);
        TextBox address = AddField("Web address", "https://example.com", "", 2048, 122);
        var hint = new Label
        {
            Bounds = new Rectangle(22, 182, 328, 23),
            ForeColor = BoardStyle.Muted
        };
        dialog.Controls.Add(hint);
        dialog.AddActions("Insert link", acceptWithEnter: true);
        var insert = (Button)dialog.AcceptButton!;
        Uri? destination = null;
        address.TextChanged += (_, _) => ValidateAddress();
        dialog.Shown += (_, _) => (initialText.Length == 0 ? display : address).Focus();
        ValidateAddress();

        if (dialog.ShowDialog(owner) != DialogResult.OK || destination is null) return null;
        string alias = display.Text.ReplaceLineEndings(" ").Trim();
        return (alias.Length == 0 ? address.Text.Trim() : alias, destination, selectedColor);

        void ValidateAddress()
        {
            destination = NoteLinks.ParseWebAddress(address.Text, allowBareHost: true);
            insert.Enabled = destination is not null;
            insert.BackColor = insert.Enabled ? BoardStyle.Brick : BoardStyle.PanelTint;
            bool invalid = !insert.Enabled && !string.IsNullOrWhiteSpace(address.Text);
            hint.ForeColor = invalid ? BoardStyle.Brick : BoardStyle.Muted;
            hint.Text = invalid ? "Enter a valid web address." : "Leave the display text blank to show the address.";
        }

        TextBox AddField(string label, string placeholder, string value, int maxLength, int top)
        {
            var caption = new Label { Text = label, Bounds = new Rectangle(22, top, 168, 21) };
            var panel = new Panel
            {
                Bounds = new Rectangle(22, top + 23, 328, 34), BackColor = Color.White,
                Padding = new Padding(10, 7, 10, 4)
            };
            panel.Resize += (_, _) => BoardStyle.RoundCorners(panel, panel.LogicalToDeviceUnits(6));
            BoardStyle.RoundCorners(panel, 6);
            var input = new TextBox
            {
                Text = value, PlaceholderText = placeholder, AccessibleName = label, MaxLength = maxLength,
                Dock = DockStyle.Top, BorderStyle = BorderStyle.None
            };
            panel.Controls.Add(input);
            panel.Click += (_, _) => input.Focus();
            dialog.Controls.AddRange([caption, panel]);
            return input;
        }
    }

    private static void PaintSwatch(object? sender, PaintEventArgs e)
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
}

internal static class ChecklistResetPrompt
{
    private static readonly string[] TimeFormats = ["HH:mm", "H:mm"];

    public static bool Show(IWin32Window owner, TimeOnly? current, out TimeOnly? time)
    {
        using var dialog = new CorkDialog("Daily checklist reset", 278);
        var description = new Label
        {
            Text = "Unchecks completed items on this note.",
            ForeColor = BoardStyle.Muted, Bounds = new Rectangle(22, 53, 328, 22)
        };
        var settings = new Panel
        {
            Bounds = new Rectangle(22, 83, 328, 113),
            BackColor = Color.FromArgb(245, 235, 219)
        };
        settings.Resize += (_, _) => BoardStyle.RoundCorners(settings, settings.LogicalToDeviceUnits(10));
        BoardStyle.RoundCorners(settings, 10);

        var scheduleLabel = new Label
        {
            Text = "Reset every day", Bounds = new Rectangle(14, 9, 224, 35),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var enabled = new CheckBox
        {
            AccessibleName = "Reset every day", Checked = current.HasValue,
            Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
            TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
            Bounds = new Rectangle(252, 10, 62, 32)
        };
        enabled.FlatAppearance.BorderSize = 0;
        enabled.Resize += (_, _) => BoardStyle.RoundCorners(enabled, enabled.LogicalToDeviceUnits(16));
        BoardStyle.RoundCorners(enabled, 16);
        scheduleLabel.Cursor = Cursors.Hand;
        scheduleLabel.Click += (_, _) =>
        {
            enabled.Focus();
            enabled.Checked = !enabled.Checked;
        };
        var divider = new Panel
        {
            BackColor = BoardStyle.PanelTint, Bounds = new Rectangle(14, 52, 300, 1)
        };
        var timeLabel = new Label { Text = "Reset time", Bounds = new Rectangle(14, 63, 178, 22) };
        var formatLabel = new Label
        {
            Text = "Local time · 24-hour", ForeColor = BoardStyle.Muted,
            Bounds = new Rectangle(14, 84, 178, 21)
        };
        var timeField = new Panel { Bounds = new Rectangle(215, 65, 99, 36) };
        timeField.Resize += (_, _) => BoardStyle.RoundCorners(timeField, timeField.LogicalToDeviceUnits(7));
        BoardStyle.RoundCorners(timeField, 7);
        using var timeFont = new Font("Segoe UI Semibold", 13F);
        var entry = new TextBox
        {
            AccessibleName = "Daily reset time, 24-hour local time",
            Text = (current ?? TimeOnly.MinValue).ToString("HH:mm", CultureInfo.InvariantCulture),
            TextAlign = HorizontalAlignment.Center, BorderStyle = BorderStyle.None, MaxLength = 5, Font = timeFont,
            Bounds = new Rectangle(5, 6, 89, 24)
        };
        timeField.Controls.Add(entry);
        timeField.Click += (_, _) => entry.Focus();
        var validation = new Label { Bounds = new Rectangle(22, 200, 328, 23), ForeColor = BoardStyle.Brick };
        settings.Controls.AddRange([scheduleLabel, enabled, divider, timeLabel, formatLabel, timeField]);
        dialog.Controls.AddRange([description, settings, validation]);
        dialog.AddActions("Save", acceptWithEnter: true);
        var save = (Button)dialog.AcceptButton!;

        bool TryRead(out TimeOnly result) => TimeOnly.TryParseExact(entry.Text, TimeFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out result);

        void UpdateState()
        {
            bool on = enabled.Checked;
            enabled.Text = on ? "On" : "Off";
            enabled.ForeColor = on ? Color.White : BoardStyle.Ink;
            enabled.BackColor = on ? BoardStyle.Brick : BoardStyle.PanelTint;
            enabled.FlatAppearance.CheckedBackColor = enabled.BackColor;
            timeLabel.ForeColor = on ? BoardStyle.Ink : BoardStyle.Muted;
            entry.Enabled = on;
            entry.BackColor = timeField.BackColor = on ? Color.White : BoardStyle.Paper;
            save.Enabled = !on || TryRead(out _);
            validation.Text = save.Enabled ? "" : "Enter a time from 00:00 to 23:59.";
        }

        enabled.CheckedChanged += (_, _) => UpdateState();
        entry.TextChanged += (_, _) => UpdateState();
        dialog.Shown += (_, _) =>
        {
            if (enabled.Checked) { entry.Focus(); entry.SelectAll(); }
            else enabled.Focus();
        };
        UpdateState();
        bool saved = dialog.ShowDialog(owner) == DialogResult.OK;
        time = saved && enabled.Checked && TryRead(out TimeOnly chosen) ? chosen : null;
        return saved;
    }
}

internal static class TextSizePrompt
{
    public static int? Show(IWin32Window owner, int current, int maximum, Font noteFont, Color notePaper)
    {
        using var dialog = new CorkDialog("Text size", 242);
        var stepper = new Panel { Bounds = new Rectangle(22, 59, 138, 36), BackColor = Color.White };
        BoardStyle.RoundCorners(stepper, 7);
        Button minus = BoardStyle.Button("−", BoardStyle.PanelTint, BoardStyle.Ink);
        minus.AccessibleName = "Decrease text size";
        minus.Bounds = new Rectangle(4, 4, 31, 28);
        Button plus = BoardStyle.Button("+", BoardStyle.PanelTint, BoardStyle.Ink);
        plus.AccessibleName = "Increase text size";
        plus.Bounds = new Rectangle(103, 4, 31, 28);
        var entry = new TextBox
        {
            Bounds = new Rectangle(39, 8, 40, 21), TextAlign = HorizontalAlignment.Center,
            BorderStyle = BorderStyle.None, MaxLength = 2, Text = current.ToString(),
            AccessibleName = "Text size in points", BackColor = Color.White
        };
        var unit = new Label
        {
            Text = "pt", Bounds = new Rectangle(77, 6, 26, 24), TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = BoardStyle.Muted
        };
        stepper.Controls.AddRange([minus, entry, unit, plus]);
        Button reset = BoardStyle.Button("Use default", BoardStyle.PanelTint, BoardStyle.Ink);
        reset.Bounds = new Rectangle(246, 61, 104, 32);
        var hint = new Label { Bounds = new Rectangle(22, 101, 328, 21), ForeColor = BoardStyle.Muted };
        var preview = new Panel { Bounds = new Rectangle(22, 129, 328, 48), BackColor = notePaper, AccessibleName = "Text size preview" };
        BoardStyle.RoundCorners(preview, 7);
        dialog.Controls.AddRange([stepper, reset, hint, preview]);
        dialog.AddActions("Apply", acceptWithEnter: true);
        var apply = (Button)dialog.AcceptButton!;

        bool TryRead(out int size) => int.TryParse(entry.Text, out size) && size >= 0 && size <= maximum;
        void UpdateSize()
        {
            bool valid = TryRead(out int size);
            apply.Enabled = valid;
            minus.Enabled = valid && size > 0;
            plus.Enabled = valid && size < maximum;
            hint.ForeColor = valid ? BoardStyle.Muted : BoardStyle.Brick;
            hint.Text = valid ? $"1–{maximum} pt · 0 uses the default" : $"Enter a number from 0 to {maximum}.";
            preview.Invalidate();
        }
        void Step(int change)
        {
            if (TryRead(out int size)) entry.Text = Math.Clamp(size + change, 0, maximum).ToString();
        }

        preview.Paint += (_, e) =>
        {
            float points = TryRead(out int size) && size > 0 ? size : noteFont.SizeInPoints;
            using var font = new Font(noteFont.FontFamily, points);
            TextRenderer.DrawText(e.Graphics, "Sample text", font, preview.ClientRectangle, BoardStyle.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        };
        entry.TextChanged += (_, _) => UpdateSize();
        entry.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Up or Keys.Down)) return;
            Step(e.KeyCode == Keys.Up ? 1 : -1);
            entry.SelectAll();
            e.SuppressKeyPress = true;
        };
        minus.Click += (_, _) => Step(-1);
        plus.Click += (_, _) => Step(1);
        reset.Click += (_, _) => entry.Text = "0";
        dialog.Shown += (_, _) => { entry.Focus(); entry.SelectAll(); };
        UpdateSize();
        return dialog.ShowDialog(owner) == DialogResult.OK && TryRead(out int result) ? result : null;
    }
}
