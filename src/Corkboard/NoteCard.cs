using System.Runtime.InteropServices;

namespace Corkboard;

internal sealed class NoteCard : UserControl
{
    public static readonly Size InitialCardSize = new(248, 184);
    public static readonly Size Smallest = new(224, 144);
    public static readonly Size Largest = new(1400, 1100);
    private readonly BoardWindow board;
    private readonly StickyNote note;
    private readonly RichTextBox? editor;
    private readonly Button label;
    private readonly Button? pin;
    private readonly Label grip;
    private readonly Panel header;
    private readonly Panel? photoCaption;
    private readonly Panel? toolbar;
    private readonly Panel writingArea;
    private readonly Panel? resizeFooter;
    private readonly Button? resizeGrip;
    private readonly List<Button> headerButtons = [];
    private readonly Dictionary<uint, Button> styleButtons = [];
    private readonly CorkMenu menu = new();
    private readonly CorkMenu pinMenu = new();
    private readonly CorkMenu? colorMenu;
    private readonly ToolTip tips = new();
    private Point? dragStart;
    private Point dragOrigin;
    private Point? resizeStart;
    private Size resizeOrigin;
    private bool refreshing;
    private bool connectionTarget;

    public NoteCard(BoardWindow board, StickyNote note, bool floating, Action? moveWindow = null, Image? image = null)
    {
        this.board = board;
        this.note = note;
        Font = board.Font;
        ForeColor = BoardStyle.Ink;
        Padding = new Padding(1, 1, 3, 3);
        BackColor = Color.FromArgb(142, 96, 63);
        bool isImage = note.ImageFile is not null;
        AccessibleName = isImage ? "Image card" : floating ? "Floating sticky note" : "Board sticky note";

        var paper = new Panel { Dock = DockStyle.Fill };
        writingArea = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 10, floating ? 8 : 0) };
        if (isImage)
        {
            writingArea.Padding = new Padding(11, 0, 11, 0);
            if (image is not null)
            {
                var print = new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, AccessibleName = "Attached image" };
                print.Paint += DrawPhotoEdge;
                writingArea.Controls.Add(print);
            }
            else writingArea.Controls.Add(new Label { Text = "Image unavailable", ForeColor = BoardStyle.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
        }
        else
        {
            editor = new RichTextBox
            {
                Multiline = true, AcceptsTab = false, ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false,
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, ForeColor = BoardStyle.Ink,
                AccessibleName = "Note text", Margin = Padding.Empty, HideSelection = false
            };
            editor.TextChanged += (_, _) => PublishEdit();
            editor.SelectionChanged += (_, _) => RefreshFormattingButtons();
            writingArea.Controls.Add(editor);
        }

        header = new Panel { Dock = DockStyle.Top, Height = isImage ? (floating ? 11 : 25) : 32 };
        if (isImage) photoCaption = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(7, 3, 3, 0) };
        Panel commands = photoCaption ?? header;
        label = BoardStyle.Button("", BoardStyle.PanelTint, BoardStyle.Ink);
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.AutoEllipsis = true;
        label.Font = isImage ? new Font("Segoe UI", 9.5F, FontStyle.Italic) : new Font("Segoe UI Semibold", 9.5F);
        label.AccessibleName = "Edit note label";
        label.Click += (_, _) => board.EditLabel(note, FindForm() ?? board);
        headerButtons.Add(label);
        commands.Controls.Add(label);

        grip = new Label
        {
            Dock = DockStyle.Right, Width = 24, Text = "⠿", TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = floating ? "Drag sticky window" : "Drag note"
        };
        if (floating)
        {
            grip.Cursor = Cursors.SizeAll;
            grip.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) moveWindow?.Invoke(); };
        }
        else
        {
            grip.MouseDown += StartDrag;
            grip.MouseMove += MoveDrag;
            grip.MouseCaptureChanged += (_, _) => FinishDrag();
            grip.MouseUp += (_, _) => FinishDrag();
        }
        commands.Controls.Add(grip);

        Button options = HeaderButton("⋯", "Note options", DockStyle.Right);
        options.Click += (_, _) => menu.Show(options, new Point(0, options.Height));
        commands.Controls.Add(options);
        if (!floating)
        {
            Button pop = HeaderButton("↗", "Pop out note", DockStyle.Right);
            pop.Click += (_, _) => board.PopOut(note);
            commands.Controls.Add(pop);
            pin = HeaderButton("", "Pin note", isImage ? DockStyle.None : DockStyle.Left);
            if (isImage)
            {
                pin.Height = 24;
                pin.Left = (header.ClientSize.Width - pin.Width) / 2;
                header.Resize += (_, _) => pin.Left = (header.ClientSize.Width - pin.Width) / 2;
            }
            pin.Paint += (_, e) => BoardStyle.DrawPin(e.Graphics, new Point(pin.Width / 2, pin.Height / 2), note.Pinned, note.PinColor, connectionTarget);
            pin.Click += (_, _) =>
            {
                if (board.HandlePinSelection(note)) return;
                if (!note.Pinned) board.SetPinned(note, true);
                else pinMenu.Show(pin, new Point(0, pin.Height));
            };
            header.Controls.Add(pin);
            pinMenu.Items.Add("Remove pin", null, (_, _) => board.SetPinned(note, false));
            pinMenu.Items.Add("Add thread", null, (_, _) => board.StartThread(note));
            var pinColors = new ToolStripMenuItem("Pin color");
            for (int i = 0; i < BoardStyle.PinColors.Length; i++)
            {
                int index = i;
                var choice = new ToolStripMenuItem(BoardStyle.PinColors[i].Name) { ForeColor = BoardStyle.PinColors[i].Color };
                choice.Click += (_, _) => board.ChangePinColor(note, index);
                pinColors.DropDownItems.Add(choice);
            }
            pinMenu.Items.Add(pinColors);
            var threadColors = new ToolStripMenuItem("Thread color");
            pinMenu.Items.Add(threadColors);
            pinMenu.Opening += (_, _) =>
            {
                for (int i = 0; i < pinColors.DropDownItems.Count; i++) ((ToolStripMenuItem)pinColors.DropDownItems[i]).Checked = note.PinColor == i;
                board.ListThreadColors(note, threadColors);
            };
        }

        if (!isImage)
        {
            colorMenu = new CorkMenu();
            toolbar = new Panel { Dock = DockStyle.Top, Height = 31, Padding = new Padding(7, 3, 6, 0) };
            var tools = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 134, WrapContents = false, Margin = Padding.Empty };
            AddFormat(tools, "B", "Bold (Ctrl+B)", FontStyle.Bold, 1);
            AddFormat(tools, "I", "Italic (Ctrl+I)", FontStyle.Italic, 2);
            AddFormat(tools, "U", "Underline (Ctrl+U)", FontStyle.Underline, 4);
            AddFormat(tools, "ab", "Strikethrough", FontStyle.Strikeout, 8);
            Button color = BoardStyle.Button("●", BoardStyle.PanelTint, BoardStyle.Ink);
            color.Dock = DockStyle.Right;
            color.Width = 29;
            color.AccessibleName = "Note color";
            color.Click += (_, _) => colorMenu.Show(color, new Point(0, color.Height));
            tips.SetToolTip(color, "Note color");
            headerButtons.Add(color);
            toolbar.Controls.Add(tools);
            toolbar.Controls.Add(color);
        }

        var colorOptions = new ToolStripMenuItem(isImage ? "Frame color" : "Note color");
        for (int i = 0; i < BoardStyle.Colors.Length; i++)
        {
            int chosen = i;
            colorMenu?.Items.Add(BoardStyle.Colors[i].Name, null, (_, _) => board.ChangeColor(note, chosen));
            colorOptions.DropDownItems.Add(BoardStyle.Colors[i].Name, null, (_, _) => board.ChangeColor(note, chosen));
        }
        if (colorMenu is not null) colorMenu.Opening += (_, _) => CheckColors(colorMenu.Items);
        var above = new ToolStripMenuItem("Keep above other windows", null, (_, _) => board.ToggleAlwaysOnTop(note));
        menu.Items.Add(colorOptions);
        if (!floating) menu.Items.Add("Pop out note", null, (_, _) => board.PopOut(note));
        menu.Items.Add(above);
        menu.Items.Add("Edit label", null, (_, _) => board.EditLabel(note, FindForm() ?? board));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete note", null, (_, _) => board.DeleteNote(note, FindForm() ?? board));
        menu.Opening += (_, _) => { above.Checked = note.PoppedOut && note.AlwaysOnTop; CheckColors(colorOptions.DropDownItems); };

        paper.Controls.Add(writingArea);
        if (photoCaption is not null) paper.Controls.Add(photoCaption);
        if (!floating)
        {
            resizeFooter = new Panel { Dock = DockStyle.Bottom, Height = isImage ? 17 : 23 };
            resizeGrip = BoardStyle.Button("", BoardStyle.PanelTint, BoardStyle.Ink);
            resizeGrip.Dock = DockStyle.Right;
            resizeGrip.Width = 29;
            resizeGrip.Cursor = Cursors.SizeNWSE;
            resizeGrip.AccessibleName = "Resize note. Use the arrow keys when focused.";
            tips.SetToolTip(resizeGrip, "Drag to resize note");
            resizeGrip.Paint += DrawResizeGrip;
            resizeGrip.MouseDown += StartResize;
            resizeGrip.MouseMove += ContinueResize;
            resizeGrip.MouseUp += (_, _) => FinishResize();
            resizeGrip.MouseCaptureChanged += (_, _) => FinishResize();
            resizeGrip.PreviewKeyDown += (_, e) => e.IsInputKey = IsArrow(e.KeyCode);
            resizeGrip.KeyDown += ResizeWithKeyboard;
            resizeFooter.Controls.Add(resizeGrip);
            paper.Controls.Add(resizeFooter);
            SizeChanged += (_, _) => BoardStyle.RoundCorners(this, LogicalToDeviceUnits(isImage ? 2 : 7));
        }
        if (toolbar is not null) paper.Controls.Add(toolbar);
        paper.Controls.Add(header);
        Controls.Add(paper);
        RefreshLabel();
        RefreshPin();
        ApplyColor();
        ReadText();
    }

    public Point PinPointOnParent => new(Left + Padding.Left + header.Left + (pin?.Left ?? 0) + (pin?.Width ?? 28) / 2,
        Top + Padding.Top + header.Top + (pin?.Top ?? 0) + (pin?.Height ?? header.Height) / 2);
    public StickyNote Note => note;

    public void SetConnectionTarget(bool value)
    {
        if (connectionTarget == value) return;
        connectionTarget = value;
        pin?.Invalidate();
    }

    public void FocusEditor()
    {
        if (editor is not null) editor.Focus();
        else label.Focus();
    }

    public void RefreshLabel()
    {
        label.Text = string.IsNullOrWhiteSpace(note.Label) ? "Add label" : note.Label;
        tips.SetToolTip(label, string.IsNullOrWhiteSpace(note.Label) ? "Label this note" : note.Label);
    }

    public void RefreshPin()
    {
        if (pin is null) return;
        pin.AccessibleName = note.Pinned ? "Pinned note options" : "Pin note";
        tips.SetToolTip(pin, note.Pinned ? "Pinned note options" : "Pin note in place");
        grip.Cursor = note.Pinned ? Cursors.Default : Cursors.SizeAll;
        tips.SetToolTip(grip, note.Pinned ? "Unpin to move this note" : "Drag to move note");
        pin.Invalidate();
    }

    public void ReadText()
    {
        if (editor is null) return;
        if (editor.Text == note.Text && (note.FormattedText is null || editor.Rtf == note.FormattedText)) return;
        refreshing = true;
        int start = editor.SelectionStart;
        int length = editor.SelectionLength;
        try
        {
            if (!string.IsNullOrWhiteSpace(note.FormattedText))
            {
                try { editor.Rtf = note.FormattedText; }
                catch (ArgumentException) { editor.Text = note.Text; }
            }
            else editor.Text = note.Text;
            start = Math.Min(start, editor.TextLength);
            editor.Select(start, Math.Min(length, editor.TextLength - start));
        }
        finally { refreshing = false; }
        RefreshFormattingButtons();
    }

    public void ApplyColor()
    {
        var color = BoardStyle.Colors[Math.Clamp(note.Color, 0, BoardStyle.Colors.Length - 1)];
        Color paper = photoCaption is null ? color.Paper : BoardStyle.PhotoPaper(note.Color);
        Color controls = photoCaption is null ? color.Edge : paper;
        if (editor is not null) editor.BackColor = paper;
        writingArea.BackColor = paper;
        header.BackColor = controls;
        if (photoCaption is not null) photoCaption.BackColor = paper;
        if (toolbar is not null) toolbar.BackColor = paper;
        if (resizeFooter is not null) resizeFooter.BackColor = paper;
        if (resizeGrip is not null) resizeGrip.BackColor = paper;
        foreach (Button button in headerButtons) button.BackColor = button.Parent == toolbar ? paper : controls;
        RefreshFormattingButtons();
    }

    private static void DrawPhotoEdge(object? sender, PaintEventArgs e)
    {
        if (sender is not PictureBox { Image: Image image } print || print.Width < 2 || print.Height < 2) return;
        double scale = Math.Min((double)print.Width / image.Width, (double)print.Height / image.Height);
        int width = Math.Max(1, (int)(image.Width * scale));
        int height = Math.Max(1, (int)(image.Height * scale));
        using var pen = new Pen(Color.FromArgb(95, BoardStyle.Muted));
        e.Graphics.DrawRectangle(pen, (print.Width - width) / 2, (print.Height - height) / 2, width - 1, height - 1);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        uint effect = keys switch { Keys.Control | Keys.B => 1, Keys.Control | Keys.I => 2, Keys.Control | Keys.U => 4, _ => 0 };
        if (effect == 0 || editor is null) return base.ProcessCmdKey(ref message, keys);
        ToggleFormat(effect);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tips.Dispose();
            menu.Dispose();
            pinMenu.Dispose();
            colorMenu?.Dispose();
        }
        base.Dispose(disposing);
    }

    private Button HeaderButton(string text, string title, DockStyle dock)
    {
        Button button = BoardStyle.Button(text, BoardStyle.PanelTint, BoardStyle.Ink);
        button.Dock = dock;
        button.Width = 28;
        button.AccessibleName = title;
        tips.SetToolTip(button, title);
        headerButtons.Add(button);
        return button;
    }

    private void AddFormat(Control tools, string text, string title, FontStyle style, uint effect)
    {
        Button button = BoardStyle.Button(text, BoardStyle.PanelTint, BoardStyle.Ink);
        button.Size = new Size(28, 27);
        button.Margin = new Padding(0, 0, 3, 0);
        button.Font = new Font(Font.FontFamily, 9F, style);
        button.AccessibleName = title;
        button.Click += (_, _) => ToggleFormat(effect);
        tips.SetToolTip(button, title);
        tools.Controls.Add(button);
        styleButtons.Add(effect, button);
    }

    private void CheckColors(ToolStripItemCollection items)
    {
        for (int i = 0; i < items.Count; i++) if (items[i] is ToolStripMenuItem item) item.Checked = i == note.Color;
    }

    private void ToggleFormat(uint effect)
    {
        if (editor is null) return;
        editor.Focus();
        CharFormat current = ReadSelectionFormat(editor);
        var change = CharFormat.Empty;
        change.Mask = effect;
        change.Effects = (current.Mask & current.Effects & effect) == 0 ? effect : 0;
        SendFormat(editor.Handle, 0x0444, new IntPtr(1), ref change);
        RefreshFormattingButtons();
        PublishEdit();
    }

    private void RefreshFormattingButtons()
    {
        if (refreshing || toolbar is null || editor is null || !editor.IsHandleCreated) return;
        CharFormat selection = ReadSelectionFormat(editor);
        foreach ((uint flag, Button button) in styleButtons)
            button.BackColor = (selection.Mask & selection.Effects & flag) != 0 ? header.BackColor : toolbar.BackColor;
    }

    private static CharFormat ReadSelectionFormat(RichTextBox editor)
    {
        CharFormat format = CharFormat.Empty;
        SendFormat(editor.Handle, 0x043A, new IntPtr(1), ref format);
        return format;
    }

    private void PublishEdit()
    {
        if (!refreshing && editor is not null) board.ChangeText(note, editor.Text, editor.Rtf ?? "", this);
    }

    private void StartDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || note.Pinned) return;
        BringToFront();
        dragStart = Cursor.Position;
        dragOrigin = Location;
        grip.Capture = true;
    }

    private void MoveDrag(object? sender, MouseEventArgs e)
    {
        if (dragStart is not Point start) return;
        Point cursor = Cursor.Position;
        var wanted = new Point(dragOrigin.X + cursor.X - start.X, dragOrigin.Y + cursor.Y - start.Y);
        Point next = board.LimitCardMove(wanted, Size);
        SetCardBounds(new Rectangle(next, Size));
    }

    private void FinishDrag()
    {
        if (dragStart is null) return;
        dragStart = null;
        grip.Capture = false;
        board.MoveNote(note, Left, Top);
    }

    private static void DrawResizeGrip(object? sender, PaintEventArgs e)
    {
        if (sender is not Control grip) return;
        using var pen = new Pen(BoardStyle.Muted, 1.3f);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int right = grip.Width - 7;
        int bottom = grip.Height - 5;
        e.Graphics.DrawLine(pen, right - 11, bottom, right, bottom - 11);
        e.Graphics.DrawLine(pen, right - 6, bottom, right, bottom - 6);
    }

    private void StartResize(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        BringToFront();
        resizeStart = Cursor.Position;
        resizeOrigin = Size;
        resizeGrip!.Capture = true;
    }

    private void ContinueResize(object? sender, MouseEventArgs e)
    {
        if (resizeStart is not Point start) return;
        Point cursor = Cursor.Position;
        SetCardSize(resizeOrigin.Width + cursor.X - start.X, resizeOrigin.Height + cursor.Y - start.Y);
    }

    private void FinishResize()
    {
        if (resizeStart is null) return;
        resizeStart = null;
        resizeGrip!.Capture = false;
        board.SaveCardSize(note, Size);
    }

    private static bool IsArrow(Keys key) => key is Keys.Left or Keys.Right or Keys.Up or Keys.Down;

    private void ResizeWithKeyboard(object? sender, KeyEventArgs e)
    {
        if (!IsArrow(e.KeyCode)) return;
        int step = LogicalToDeviceUnits(e.Shift ? 4 : 16);
        int width = Width + (e.KeyCode == Keys.Right ? step : e.KeyCode == Keys.Left ? -step : 0);
        int height = Height + (e.KeyCode == Keys.Down ? step : e.KeyCode == Keys.Up ? -step : 0);
        SetCardSize(width, height);
        board.SaveCardSize(note, Size);
        e.Handled = true;
    }

    private void SetCardSize(int width, int height)
    {
        Size size = board.LimitCardResize(Location, new Size(width, height));
        SetCardBounds(new Rectangle(Location, size));
    }

    private void SetCardBounds(Rectangle next)
    {
        Rectangle previous = Bounds;
        if (previous == next) return;
        Bounds = next;
        if (Parent is not Panel canvas) return;
        // Redraw the exposed cork and thread segments before the next drag frame.
        canvas.Invalidate(Rectangle.Union(previous, Bounds));
        canvas.Update();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CharFormat
    {
        public uint Size, Mask, Effects;
        public int Height, Offset;
        public uint TextColor;
        public byte CharacterSet, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;

        public static CharFormat Empty => new() { Size = (uint)Marshal.SizeOf<CharFormat>(), FaceName = "" };
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendFormat(IntPtr window, uint message, IntPtr selection, ref CharFormat format);
}
