namespace Corkboard;

internal sealed class FloatingNote : ResizableWindow
{
    private readonly bool activate;
    private readonly bool photo;
    private readonly CorkMenu actions = new();
    private readonly Panel footer = new() { Dock = DockStyle.Bottom, Height = 41, Padding = new Padding(8, 5, 8, 6) };
    public NoteCard Card { get; }

    public FloatingNote(BoardWindow board, StickyNote note, Icon icon, bool activate, Image? image = null)
    {
        this.activate = activate;
        photo = note.ImageFile is not null;
        Icon = icon;
        Text = string.IsNullOrWhiteSpace(note.Label) ? "Sticky note" : note.Label;
        Font = board.Font;
        ShowInTaskbar = false;
        TopMost = note.AlwaysOnTop;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Padding = new Padding(8);
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(205, 155);
        int width = photo ? Math.Clamp(note.CardWidth + 12, 258, 400) : 278;
        int height = photo ? Math.Clamp(note.CardHeight + 32, 238, 450) : 206;
        Bounds = FitOnScreen(note.PopupBounds ?? new Rectangle(board.Right - width - 17, board.Top + 66, width, height), MinimumSize, Screen.FromControl(board));
        Card = new NoteCard(board, note, floating: true, BeginWindowDrag, image) { Dock = DockStyle.Fill, Padding = Padding.Empty };
        Controls.Add(Card);

        var above = new ToolStripMenuItem("Keep above other windows", null, (_, _) => board.ToggleAlwaysOnTop(note));
        actions.Items.Add(above);
        actions.Items.Add(new ToolStripSeparator());
        actions.Items.Add("Minimize note", null, (_, _) => Hide());
        actions.Items.Add("Return to board", null, (_, _) => Close());
        actions.Opening += (_, _) => above.Checked = TopMost;
        Button fab = BoardStyle.Button("≡  Note menu", BoardStyle.Paper, BoardStyle.Ink);
        fab.AccessibleName = "Sticky window actions";
        fab.Dock = DockStyle.Right;
        fab.Width = 108;
        fab.Click += (_, _) => actions.Show(fab, new Point(fab.Width, -LogicalToDeviceUnits(5)), ToolStripDropDownDirection.AboveLeft);
        footer.Controls.Add(fab);
        Controls.Add(footer);
        ApplyColor(note.Color);
        ResizeEnd += (_, _) => SaveBounds();
        Shown += (_, _) => SaveBounds();

        void SaveBounds()
        {
            if (WindowState == FormWindowState.Normal) board.SavePopupBounds(note, Bounds);
        }
    }

    protected override bool ShowWithoutActivation => !activate;

    public void ApplyColor(int index)
    {
        var color = BoardStyle.Colors[Math.Clamp(index, 0, BoardStyle.Colors.Length - 1)];
        BackColor = photo ? BoardStyle.PhotoPaper(index) : color.Edge;
        footer.BackColor = photo ? BackColor : color.Paper;
        Card.ApplyColor();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int right = ClientSize.Width - 3;
        int bottom = ClientSize.Height - 3;
        using var pen = new Pen(BoardStyle.Muted);
        e.Graphics.DrawLine(pen, right - 7, bottom, right, bottom - 7);
        e.Graphics.DrawLine(pen, right - 4, bottom, right, bottom - 4);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) actions.Dispose();
        base.Dispose(disposing);
    }
}
