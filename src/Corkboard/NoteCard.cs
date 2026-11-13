using System.Runtime.InteropServices;

namespace Corkboard;

internal sealed class NoteCard : UserControl
{
    public static readonly Size InitialCardSize = new(248, 184);
    public static readonly Size Smallest = new(224, 144);
    public static readonly Size Largest = new(1400, 1100);
    private readonly BoardWindow board;
    private readonly StickyNote note;
    private readonly RichTextBox editor;
    private readonly Button label;
    private readonly Button? pin;
    private readonly Label grip;
    private readonly Panel header;
    private readonly Panel toolbar;
    private readonly Panel writingArea;
    private readonly Panel? resizeFooter;
    private readonly Button? resizeGrip;
    private readonly List<Button> headerButtons = [];
    private readonly Dictionary<uint, Button> styleButtons = [];
    private readonly ContextMenuStrip menu = new();
    private readonly ContextMenuStrip pinMenu = new();
    private readonly ContextMenuStrip colorMenu = new();
    private readonly ToolTip tips = new();
    private Point? dragStart;
    private Point dragOrigin;
    private Point? resizeStart;
    private Size resizeOrigin;
    private bool refreshing;

    public NoteCard(BoardWindow board, StickyNote note, bool floating, Action? moveWindow = null)
    {
        this.board = board;
        this.note = note;
        Font = board.Font;
        ForeColor = BoardStyle.Ink;
        Padding = new Padding(1, 1, 3, 3);
        BackColor = Color.FromArgb(142, 96, 63);
        AccessibleName = floating ? "Floating sticky note" : "Board sticky note";

        var paper = new Panel { Dock = DockStyle.Fill };
        writingArea = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 10, floating ? 8 : 0) };
        editor = new RichTextBox
        {
            Multiline = true, AcceptsTab = false, ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false,
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, ForeColor = BoardStyle.Ink,
            AccessibleName = "Note text", Margin = Padding.Empty, HideSelection = false
        };
        editor.TextChanged += (_, _) => PublishEdit();
        editor.SelectionChanged += (_, _) => RefreshFormattingButtons();
        writingArea.Controls.Add(editor);

        header = new Panel { Dock = DockStyle.Top, Height = 32 };
        label = BoardStyle.Button("", BoardStyle.PanelTint, BoardStyle.Ink);
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.AutoEllipsis = true;
        label.Font = new Font("Segoe UI Semibold", 9.5F);
        label.AccessibleName = "Edit note label";
        label.Click += (_, _) => board.EditLabel(note, FindForm() ?? board);
        headerButtons.Add(label);
        header.Controls.Add(label);

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
        header.Controls.Add(grip);

        Button options = HeaderButton("⋯", "Note options", DockStyle.Right);
        options.Click += (_, _) => menu.Show(options, new Point(0, options.Height));
        header.Controls.Add(options);
        if (!floating)
        {
            Button pop = HeaderButton("↗", "Pop out note", DockStyle.Right);
            pop.Click += (_, _) => board.PopOut(note);
            header.Controls.Add(pop);
            pin = HeaderButton("", "Pin note", DockStyle.Left);
            pin.Paint += (_, e) => BoardStyle.DrawPin(e.Graphics, new Point(pin.Width / 2, pin.Height / 2), note.Pinned);
            pin.Click += (_, _) =>
            {
                if (board.HandlePinSelection(note)) return;
                if (!note.Pinned) board.SetPinned(note, true);
                else pinMenu.Show(pin, new Point(0, pin.Height));
            };
            header.Controls.Add(pin);
            pinMenu.Items.Add("Remove pin", null, (_, _) => board.SetPinned(note, false));
            pinMenu.Items.Add("Add thread", null, (_, _) => board.StartThread(note));
        }

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

        var colorOptions = new ToolStripMenuItem("Note color");
        for (int i = 0; i < BoardStyle.Colors.Length; i++)
        {
            int chosen = i;
            colorMenu.Items.Add(BoardStyle.Colors[i].Name, null, (_, _) => board.ChangeColor(note, chosen));
            colorOptions.DropDownItems.Add(BoardStyle.Colors[i].Name, null, (_, _) => board.ChangeColor(note, chosen));
        }
        colorMenu.Opening += (_, _) => CheckColors(colorMenu.Items);
        var above = new ToolStripMenuItem("Keep above other windows", null, (_, _) => board.ToggleAlwaysOnTop(note));
        menu.Items.Add(colorOptions);
        if (!floating) menu.Items.Add("Pop out note", null, (_, _) => board.PopOut(note));
        menu.Items.Add(above);
        menu.Items.Add("Edit label", null, (_, _) => board.EditLabel(note, FindForm() ?? board));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete note", null, (_, _) => board.DeleteNote(note, FindForm() ?? board));
        menu.Opening += (_, _) => { above.Checked = note.PoppedOut && note.AlwaysOnTop; CheckColors(colorOptions.DropDownItems); };

        paper.Controls.Add(writingArea);
        if (!floating)
        {
            resizeFooter = new Panel { Dock = DockStyle.Bottom, Height = 23 };
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
            SizeChanged += (_, _) => BoardStyle.RoundCorners(this, LogicalToDeviceUnits(7));
        }
        paper.Controls.Add(toolbar);
        paper.Controls.Add(header);
        Controls.Add(paper);
        RefreshLabel();
        RefreshPin();
        ApplyColor();
        ReadText();
    }

    public Point PinPointOnParent => new(Left + Padding.Left + (pin?.Width ?? 28) / 2, Top + Padding.Top + header.Height / 2);

    public void FocusEditor() => editor.Focus();

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
        editor.BackColor = color.Paper;
        writingArea.BackColor = color.Paper;
        header.BackColor = color.Edge;
        toolbar.BackColor = color.Paper;
        if (resizeFooter is not null) resizeFooter.BackColor = color.Paper;
        if (resizeGrip is not null) resizeGrip.BackColor = color.Paper;
        foreach (Button button in headerButtons) button.BackColor = button.Parent == toolbar ? color.Paper : color.Edge;
        RefreshFormattingButtons();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        uint effect = keys switch { Keys.Control | Keys.B => 1, Keys.Control | Keys.I => 2, Keys.Control | Keys.U => 4, _ => 0 };
        if (effect == 0) return base.ProcessCmdKey(ref message, keys);
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
            colorMenu.Dispose();
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
        editor.Focus();
        CharFormat current = ReadSelectionFormat();
        var change = CharFormat.Empty;
        change.Mask = effect;
        change.Effects = (current.Mask & current.Effects & effect) == 0 ? effect : 0;
        SendFormat(editor.Handle, 0x0444, new IntPtr(1), ref change);
        RefreshFormattingButtons();
        PublishEdit();
    }

    private void RefreshFormattingButtons()
    {
        if (refreshing || toolbar is null || !editor.IsHandleCreated) return;
        CharFormat selection = ReadSelectionFormat();
        foreach ((uint flag, Button button) in styleButtons)
            button.BackColor = (selection.Mask & selection.Effects & flag) != 0 ? header.BackColor : toolbar.BackColor;
    }

    private CharFormat ReadSelectionFormat()
    {
        CharFormat format = CharFormat.Empty;
        SendFormat(editor.Handle, 0x043A, new IntPtr(1), ref format);
        return format;
    }

    private void PublishEdit()
    {
        if (!refreshing) board.ChangeText(note, editor.Text, editor.Rtf ?? "", this);
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
        if (dragStart is not Point start || Parent is not Panel canvas) return;
        Point cursor = Cursor.Position;
        Point scroll = canvas.AutoScrollPosition;
        Location = new Point(Math.Max(8, dragOrigin.X + cursor.X - start.X - scroll.X) + scroll.X,
            Math.Max(8, dragOrigin.Y + cursor.Y - start.Y - scroll.Y) + scroll.Y);
    }

    private void FinishDrag()
    {
        if (dragStart is null || Parent is not Panel canvas) return;
        dragStart = null;
        grip.Capture = false;
        Point scroll = canvas.AutoScrollPosition;
        board.MoveNote(note, Left - scroll.X, Top - scroll.Y);
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
        Size = new Size(Math.Clamp(width, LogicalToDeviceUnits(Smallest.Width), LogicalToDeviceUnits(Largest.Width)),
            Math.Clamp(height, LogicalToDeviceUnits(Smallest.Height), LogicalToDeviceUnits(Largest.Height)));
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
