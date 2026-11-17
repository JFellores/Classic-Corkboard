namespace Corkboard;

internal sealed class FloatingNote : ResizableWindow
{
    private readonly bool activate;
    private readonly bool photo;
    private readonly BoardWindow board;
    private readonly StickyNote note;
    private readonly CorkMenu actions = new();
    private readonly Panel footer = new() { Dock = DockStyle.Bottom, Height = 41, Padding = new Padding(8, 5, 8, 6) };
    public NoteCard Card { get; }

    public FloatingNote(BoardWindow board, StickyNote note, Icon icon, bool activate, Image? image = null)
    {
        this.board = board;
        this.note = note;
        this.activate = activate;
        photo = note.ImageFile is not null;
        Icon = icon;
        bool table = note.Table is not null;
        Text = string.IsNullOrWhiteSpace(note.Label) ? (table ? "Table" : "Sticky note") : note.Label;
        Font = board.Font;
        ShowInTaskbar = false;
        TopMost = note.AlwaysOnTop;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Padding = new Padding(8);
        StartPosition = FormStartPosition.Manual;
        MinimumSize = table ? NoteCard.MinimumSizeFor(note) + new Size(16, 57) : new Size(205, 155);
        int width = table ? Math.Clamp(note.CardWidth + 16, MinimumSize.Width, 560) : photo ? Math.Clamp(note.CardWidth + 12, 258, 400) : 278;
        int height = table ? Math.Clamp(note.CardHeight + 57, MinimumSize.Height, 480) : photo ? Math.Clamp(note.CardHeight + 32, 238, 450) : 206;
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
        ResizeEnd += (_, _) => SaveBounds(record: true);
        Shown += (_, _) => SaveBounds(record: false);

        void SaveBounds(bool record)
        {
            if (WindowState == FormWindowState.Normal) board.SavePopupBounds(note, Bounds, record);
        }
    }

    protected override bool ShowWithoutActivation => !activate;

    protected override bool ProcessCmdKey(ref Message msg, Keys keys) =>
        board.HandleHistoryKey(keys, this) || base.ProcessCmdKey(ref msg, keys);

    public void RefreshFromHistory()
    {
        Text = string.IsNullOrWhiteSpace(note.Label) ? (note.Table is null ? "Sticky note" : "Table") : note.Label;
        TopMost = note.AlwaysOnTop;
        if (note.PopupBounds is Rectangle bounds && WindowState == FormWindowState.Normal && Bounds != bounds)
            Bounds = FitOnScreen(bounds, MinimumSize, Screen.FromRectangle(bounds));
        ApplyColor(note.Color);
        Card.RefreshFromHistory();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Card.CommitTableEdit();
        base.OnFormClosing(e);
    }

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
