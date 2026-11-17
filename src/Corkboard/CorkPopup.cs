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
    public static (string Alias, Uri Destination, Color Color)? Show(IWin32Window owner, string selection)
    {
        using var dialog = new CorkDialog("Insert link", 264);
        using var swatchTips = new ToolTip();
        string initialText = selection.Length <= 160 && !selection.Any(char.IsControl) ? selection : "";
        TextBox display = AddField("Text to display", "Video Link", initialText, 160, 55);
        Color selectedColor = BoardStyle.TextColors[0].Color;
        display.ForeColor = selectedColor;
        var palette = new Panel { Bounds = new Rectangle(196, 52, 154, 23), AccessibleName = "Link color" };
        palette.Controls.Add(new Label
        {
            Text = "Color", ForeColor = BoardStyle.Muted,
            TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(0, 0, 43, 23)
        });
        for (int i = 0; i < BoardStyle.TextColors.Length; i++)
        {
            (string name, Color color) = BoardStyle.TextColors[i];
            var swatch = new RadioButton
            {
                Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                Bounds = new Rectangle(43 + i * 22, 1, 21, 21), BackColor = BoardStyle.Paper,
                ForeColor = color, AccessibleName = name + " link color", Cursor = Cursors.Hand
            };
            swatch.FlatAppearance.BorderSize = 0;
            swatch.FlatAppearance.CheckedBackColor = BoardStyle.Paper;
            swatch.Paint += BoardStyle.PaintColorSwatch;
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

}

internal static class TextSizePrompt
{
    private const int MinimumOpacity = 25;

    public static Color TextInk(Color paper, int opacity) => TextInk(BoardStyle.Ink, paper, opacity);

    private static Color TextInk(Color ink, Color paper, int opacity) => Color.FromArgb(
        (ink.R * opacity + paper.R * (100 - opacity)) / 100,
        (ink.G * opacity + paper.G * (100 - opacity)) / 100,
        (ink.B * opacity + paper.B * (100 - opacity)) / 100);

    public static (int? Size, Color? Ink, string? Family)? Show(IWin32Window owner, int? current, int maximum,
        Font noteFont, string? currentFamily, Color notePaper, Color currentInk)
    {
        using var dialog = new CorkDialog("Text appearance", 395);
        using var swatchTips = new ToolTip();
        using var installedFonts = new System.Drawing.Text.InstalledFontCollection();
        int defaultSize = Math.Clamp((int)Math.Round(noteFont.SizeInPoints), 1, maximum);
        var familyLabel = new Label { Text = "Font", Bounds = new Rectangle(22, 58, 80, 25) };
        var family = new ComboBox
        {
            AccessibleName = "Font family", DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            Bounds = new Rectangle(112, 56, 238, 28), MaxDropDownItems = 12
        };
        string[] available = installedFonts.Families.Select(font => font.Name).Where(name => name.Length < 32)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        family.Items.Add("Keep current");
        family.Items.AddRange(available);
        family.SelectedIndex = 0;
        if (currentFamily is string selected && family.Items.IndexOf(selected) is int index && index > 0)
            family.SelectedIndex = index;
        int originalFamily = family.SelectedIndex;
        var sizeLabel = new Label { Text = "Size", Bounds = new Rectangle(22, 103, 78, 30), TextAlign = ContentAlignment.MiddleLeft };
        var stepper = new Panel { Bounds = new Rectangle(112, 100, 138, 36), BackColor = Color.White };
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
            BorderStyle = BorderStyle.None, MaxLength = 2, Text = current?.ToString() ?? "",
            PlaceholderText = "Mixed",
            AccessibleName = "Text size in points", BackColor = Color.White
        };
        var unit = new Label
        {
            Text = "pt", Bounds = new Rectangle(77, 6, 26, 24), TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = BoardStyle.Muted
        };
        stepper.Controls.AddRange([minus, entry, unit, plus]);
        Button reset = BoardStyle.Button("Use default", BoardStyle.PanelTint, BoardStyle.Ink);
        reset.Text = $"{defaultSize} pt default";
        reset.Bounds = new Rectangle(254, 101, 96, 32);
        var hint = new Label { Bounds = new Rectangle(112, 142, 238, 21), ForeColor = BoardStyle.Muted };
        var colorLabel = new Label { Text = "Color", Bounds = new Rectangle(22, 174, 80, 27), TextAlign = ContentAlignment.MiddleLeft };
        var palette = new Panel { Bounds = new Rectangle(112, 172, 238, 30), AccessibleName = "Font color palette" };
        var swatches = new List<RadioButton>(BoardStyle.TextColors.Length);
        Color baseInk = currentInk.IsEmpty ? BoardStyle.Ink : currentInk;
        int existingOpacity = 100;
        if (!currentInk.IsEmpty)
        {
            foreach (var (_, color) in BoardStyle.TextColors)
            {
                int match = Enumerable.Range(MinimumOpacity, 101 - MinimumOpacity).Reverse()
                    .FirstOrDefault(value => TextInk(color, notePaper, value).ToArgb() == currentInk.ToArgb());
                if (match == 0) continue;
                baseInk = color;
                existingOpacity = match;
                break;
            }
        }
        for (int i = 0; i < BoardStyle.TextColors.Length; i++)
        {
            (string name, Color color) = BoardStyle.TextColors[i];
            var swatch = new RadioButton
            {
                Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                Bounds = new Rectangle(i * 27, 3, 26, 25), BackColor = BoardStyle.Paper,
                ForeColor = color, AccessibleName = name + " font color", Cursor = Cursors.Hand,
                Checked = !currentInk.IsEmpty && color.ToArgb() == baseInk.ToArgb()
            };
            swatch.FlatAppearance.BorderSize = 0;
            swatch.FlatAppearance.CheckedBackColor = BoardStyle.Paper;
            swatch.Paint += BoardStyle.PaintColorSwatch;
            swatchTips.SetToolTip(swatch, i == 0 ? "Default text color" : name);
            swatches.Add(swatch);
            palette.Controls.Add(swatch);
        }
        var custom = BoardStyle.Button("Custom…", BoardStyle.PanelTint, BoardStyle.Ink);
        custom.AccessibleName = "Choose a custom font color";
        custom.Bounds = new Rectangle(156, 1, 82, 28);
        palette.Controls.Add(custom);
        var opacityLabel = new Label { Text = "Opacity", Bounds = new Rectangle(22, 217, 90, 27), TextAlign = ContentAlignment.MiddleLeft };
        var opacity = new TrackBar
        {
            AccessibleName = "Text opacity", Minimum = MinimumOpacity, Maximum = 100,
            TickStyle = TickStyle.None, AutoSize = false, SmallChange = 1, LargeChange = 10,
            Bounds = new Rectangle(106, 214, 184, 30), Value = existingOpacity
        };
        bool inkChanged = false;
        var percentage = new Label { Bounds = new Rectangle(290, 216, 60, 27), TextAlign = ContentAlignment.MiddleRight };
        var preview = new Panel { Bounds = new Rectangle(22, 261, 328, 69), BackColor = notePaper, AccessibleName = "Text size preview" };
        BoardStyle.RoundCorners(preview, 7);
        dialog.Controls.AddRange([familyLabel, family, sizeLabel, stepper, reset, hint, colorLabel, palette, opacityLabel, opacity, percentage, preview]);
        dialog.AddActions("Apply", acceptWithEnter: true);
        var apply = (Button)dialog.AcceptButton!;

        bool TryRead(out int size) => int.TryParse(entry.Text, out size) && size >= 1 && size <= maximum;
        void UpdateSize()
        {
            bool valid = TryRead(out int size);
            apply.Enabled = valid || (current is null && entry.Text.Length == 0);
            minus.Enabled = valid && size > 1;
            plus.Enabled = valid && size < maximum;
            hint.ForeColor = apply.Enabled ? BoardStyle.Muted : BoardStyle.Brick;
            hint.Text = apply.Enabled ? $"1–{maximum} pt" : $"Enter a number from 1 to {maximum}.";
            preview.Invalidate();
        }
        void Step(int change)
        {
            if (TryRead(out int size)) entry.Text = Math.Clamp(size + change, 1, maximum).ToString();
        }

        preview.Paint += (_, e) =>
        {
            float points = TryRead(out int size) && size > 0 ? size : noteFont.SizeInPoints;
            using var font = new Font(family.SelectedIndex > 0 ? (string)family.SelectedItem! : noteFont.Name, points);
            Color ink = inkChanged ? TextInk(baseInk, notePaper, opacity.Value)
                : currentInk.IsEmpty ? BoardStyle.Ink : currentInk;
            TextRenderer.DrawText(e.Graphics, "Sample text", font, preview.ClientRectangle, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        };
        entry.TextChanged += (_, _) => UpdateSize();
        percentage.Text = currentInk.IsEmpty ? "Mixed" : $"{opacity.Value}%";
        foreach (RadioButton swatch in swatches)
        {
            swatch.CheckedChanged += (_, _) =>
            {
                if (swatch.Checked) ChangeInk(swatch.ForeColor);
                swatch.Invalidate();
            };
        }
        custom.Click += (_, _) =>
        {
            using var picker = new ColorDialog { Color = baseInk, FullOpen = true };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            RadioButton? preset = swatches.FirstOrDefault(button => button.ForeColor.ToArgb() == picker.Color.ToArgb());
            foreach (RadioButton swatch in swatches) swatch.Checked = swatch == preset;
            ChangeInk(picker.Color);
        };

        void ChangeInk(Color color)
        {
            baseInk = color;
            inkChanged = true;
            percentage.Text = $"{opacity.Value}%";
            preview.Invalidate();
        }
        opacity.ValueChanged += (_, _) =>
        {
            inkChanged = true;
            percentage.Text = $"{opacity.Value}%";
            preview.Invalidate();
        };
        family.SelectedIndexChanged += (_, _) => preview.Invalidate();
        entry.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Up or Keys.Down)) return;
            Step(e.KeyCode == Keys.Up ? 1 : -1);
            entry.SelectAll();
            e.SuppressKeyPress = true;
        };
        minus.Click += (_, _) => Step(-1);
        plus.Click += (_, _) => Step(1);
        reset.Click += (_, _) => entry.Text = defaultSize.ToString();
        dialog.Shown += (_, _) => { entry.Focus(); entry.SelectAll(); };
        UpdateSize();
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        Color appliedInk = TextInk(baseInk, notePaper, opacity.Value);
        return (TryRead(out int result) && result != current ? result : null,
            inkChanged && (currentInk.IsEmpty || appliedInk.ToArgb() != currentInk.ToArgb()) ? appliedInk : null,
            family.SelectedIndex > 0 && family.SelectedIndex != originalFamily ? (string)family.SelectedItem! : null);
    }
}
