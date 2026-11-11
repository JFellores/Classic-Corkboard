namespace Corkboard;

internal sealed class FloatingNote : Form
{
    private readonly bool activate;
    public NoteCard Card { get; }

    public FloatingNote(BoardWindow board, StickyNote note, Icon icon, bool activate)
    {
        this.activate = activate;
        Icon = icon;
        Text = "Sticky note";
        Font = board.Font;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        TopMost = note.AlwaysOnTop;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(190, 125);
        Bounds = OnScreenBounds(board, note.PopupBounds);
        Card = new NoteCard(board, note, floating: true) { Dock = DockStyle.Fill, Padding = Padding.Empty };
        Controls.Add(Card);
        ResizeEnd += (_, _) => SaveBounds();
        Shown += (_, _) => SaveBounds();

        void SaveBounds()
        {
            if (WindowState == FormWindowState.Normal) board.SavePopupBounds(note, Bounds);
        }
    }

    protected override bool ShowWithoutActivation => !activate;

    private static Rectangle OnScreenBounds(Form board, Rectangle? saved)
    {
        Rectangle workingArea = saved is Rectangle old && Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(old))
            ? Screen.FromRectangle(old).WorkingArea : Screen.FromControl(board).WorkingArea;
        Rectangle target = saved ?? new Rectangle(board.Right - 288, board.Top + 88, 270, 205);
        int width = Math.Clamp(target.Width, 190, workingArea.Width);
        int height = Math.Clamp(target.Height, 125, workingArea.Height);
        return new Rectangle(Math.Clamp(target.X, workingArea.Left, workingArea.Right - width),
            Math.Clamp(target.Y, workingArea.Top, workingArea.Bottom - height), width, height);
    }
}
