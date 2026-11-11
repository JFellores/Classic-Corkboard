namespace Corkboard;

internal sealed class NoteCard : UserControl
{
    private readonly StickyNote note;
    private readonly TextBox editor;
    private readonly Panel grip;
    private readonly Panel header;
    private readonly Button menuButton;
    private readonly Button? popButton;
    private readonly ContextMenuStrip menu = new();
    private readonly ToolTip tips = new();
    private Point? dragStart;
    private Point dragOrigin;
    private bool refreshing;

    public NoteCard(BoardWindow board, StickyNote note, bool floating)
    {
        this.note = note;
        Font = board.Font;
        ForeColor = BoardStyle.Ink;
        Padding = new Padding(1, 1, 3, 3);
        BackColor = Color.FromArgb(121, 81, 54);
        AccessibleName = floating ? "Floating sticky note" : "Board sticky note";

        var paper = new Panel { Dock = DockStyle.Fill };
        var writingArea = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 9, 10, 10) };
        editor = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Text = note.Text,
            ForeColor = BoardStyle.Ink, AccessibleName = "Note text", Margin = Padding.Empty
        };
        editor.TextChanged += (_, _) =>
        {
            if (!refreshing) board.ChangeText(note, editor.Text, this);
        };
        writingArea.Controls.Add(editor);

        header = new Panel { Dock = DockStyle.Top, Height = 31 };
        grip = new Panel { Dock = DockStyle.Fill, Cursor = floating ? Cursors.Default : Cursors.SizeAll };
        grip.Paint += (_, e) => BoardStyle.DrawPin(e.Graphics, new Point(16, grip.Height / 2));
        if (!floating)
        {
            tips.SetToolTip(grip, "Drag to move note");
            grip.MouseDown += StartDrag;
            grip.MouseMove += MoveDrag;
            grip.MouseCaptureChanged += (_, _) => FinishDrag(board);
            grip.MouseUp += (_, _) => FinishDrag(board);
        }

        menuButton = HeaderButton("⋯", "Note options");
        menuButton.Click += (_, _) => menu.Show(menuButton, new Point(0, menuButton.Height));
        header.Controls.Add(grip);
        if (!floating)
        {
            popButton = HeaderButton("↗", "Pop out note");
            popButton.Click += (_, _) => board.PopOut(note);
            header.Controls.Add(popButton);
        }
        header.Controls.Add(menuButton);

        var colors = new ToolStripMenuItem("Note color");
        for (int index = 0; index < BoardStyle.Colors.Length; index++)
        {
            int chosenColor = index;
            var item = new ToolStripMenuItem(BoardStyle.Colors[index].Name);
            item.Click += (_, _) => board.ChangeColor(note, chosenColor);
            colors.DropDownItems.Add(item);
        }
        var pin = new ToolStripMenuItem("Keep above other windows");
        pin.Click += (_, _) => board.TogglePin(note);
        var returnToBoard = new ToolStripMenuItem("Return to board");
        returnToBoard.Click += (_, _) => board.ReturnToBoard(note);
        var delete = new ToolStripMenuItem("Delete note");
        delete.Click += (_, _) => board.DeleteNote(note, FindForm() ?? board);
        menu.Items.AddRange([colors, pin, returnToBoard, new ToolStripSeparator(), delete]);
        menu.Opening += (_, _) =>
        {
            pin.Checked = note.PoppedOut && note.AlwaysOnTop;
            returnToBoard.Visible = note.PoppedOut;
            for (int index = 0; index < colors.DropDownItems.Count; index++)
                ((ToolStripMenuItem)colors.DropDownItems[index]).Checked = index == note.Color;
        };
        ContextMenuStrip = menu;
        grip.ContextMenuStrip = menu;

        paper.Controls.Add(writingArea);
        paper.Controls.Add(header);
        Controls.Add(paper);
        ApplyColor();
    }

    public void FocusEditor() => editor.Focus();

    public void SetText(string text)
    {
        if (editor.Text == text) return;
        refreshing = true;
        try { editor.Text = text; }
        finally { refreshing = false; }
    }

    public void ApplyColor()
    {
        var color = BoardStyle.Colors[Math.Clamp(note.Color, 0, BoardStyle.Colors.Length - 1)];
        editor.BackColor = color.Paper;
        editor.Parent!.BackColor = color.Paper;
        header.BackColor = color.Edge;
        menuButton.BackColor = color.Edge;
        if (popButton != null) popButton.BackColor = color.Edge;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tips.Dispose();
            menu.Dispose();
        }
        base.Dispose(disposing);
    }

    private Button HeaderButton(string text, string label)
    {
        Button button = BoardStyle.Button(text, Color.Transparent, BoardStyle.Ink);
        button.Dock = DockStyle.Right;
        button.Width = 32;
        button.AccessibleName = label;
        tips.SetToolTip(button, label);
        return button;
    }

    private void StartDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
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
        Location = new Point(Math.Clamp(dragOrigin.X + cursor.X - start.X - scroll.X, 8, 12000) + scroll.X,
            Math.Clamp(dragOrigin.Y + cursor.Y - start.Y - scroll.Y, 8, 12000) + scroll.Y);
    }

    private void FinishDrag(BoardWindow board)
    {
        if (dragStart is null || Parent is not Panel canvas) return;
        dragStart = null;
        grip.Capture = false;
        Point scroll = canvas.AutoScrollPosition;
        board.MoveNote(note, Left - scroll.X, Top - scroll.Y);
    }
}
