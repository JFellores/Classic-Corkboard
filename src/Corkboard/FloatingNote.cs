namespace Corkboard;

internal sealed class FloatingNote : ResizableWindow
{
    private readonly bool activate;
    private readonly ContextMenuStrip actions = new();
    public NoteCard Card { get; }

    public FloatingNote(BoardWindow board, StickyNote note, Icon icon, bool activate)
    {
        this.activate = activate;
        Icon = icon;
        Text = string.IsNullOrWhiteSpace(note.Label) ? "Sticky note" : note.Label;
        Font = board.Font;
        ShowInTaskbar = false;
        TopMost = note.AlwaysOnTop;
        BackColor = BoardStyle.Colors[Math.Clamp(note.Color, 0, BoardStyle.Colors.Length - 1)].Edge;
        Padding = new Padding(8);
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(205, 135);
        Bounds = FitOnScreen(note.PopupBounds ?? new Rectangle(board.Right - 295, board.Top + 66, 278, 206), MinimumSize, Screen.FromControl(board));
        Card = new NoteCard(board, note, floating: true, BeginWindowDrag) { Dock = DockStyle.Fill, Padding = Padding.Empty };
        Controls.Add(Card);

        var above = new ToolStripMenuItem("Keep above other windows", null, (_, _) => board.ToggleAlwaysOnTop(note));
        actions.Items.Add(above);
        actions.Items.Add("Minimize note", null, (_, _) => Hide());
        actions.Items.Add("Return to board", null, (_, _) => Close());
        actions.Opening += (_, _) => above.Checked = TopMost;
        Button fab = BoardStyle.Button("≡", BoardStyle.Brick, Color.White);
        fab.AccessibleName = "Sticky window actions";
        fab.Size = new Size(29, 24);
        fab.Location = new Point(ClientSize.Width - fab.Width - 12, ClientSize.Height - fab.Height - 12);
        fab.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        fab.Click += (_, _) => actions.Show(fab, Point.Empty, ToolStripDropDownDirection.AboveLeft);
        Controls.Add(fab);
        fab.BringToFront();
        ResizeEnd += (_, _) => SaveBounds();
        Shown += (_, _) => SaveBounds();

        void SaveBounds()
        {
            if (WindowState == FormWindowState.Normal) board.SavePopupBounds(note, Bounds);
        }
    }

    protected override bool ShowWithoutActivation => !activate;

    protected override void Dispose(bool disposing)
    {
        if (disposing) actions.Dispose();
        base.Dispose(disposing);
    }
}
