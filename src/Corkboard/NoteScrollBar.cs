using System.Drawing.Drawing2D;

namespace Corkboard;

internal sealed class NoteScrollBar : Control
{
    private readonly NoteTextEditor editor;
    private bool active;
    private bool dragging;
    private int grabOffset;
    private int maximum;
    private Rectangle thumb;
    private Color accent;
    private bool refreshing;

    public NoteScrollBar(NoteTextEditor editor)
    {
        this.editor = editor;
        Dock = DockStyle.Right;
        Width = editor.LogicalToDeviceUnits(11);
        Visible = false;
        AccessibleName = "Note scrollbar";
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        editor.ContentsResized += (_, _) => RefreshScroll();
        editor.SizeChanged += (_, _) => RefreshScroll();
        editor.SelectionChanged += (_, _) => RefreshScroll();
        editor.VScroll += (_, _) => RefreshScroll();
        editor.HandleCreated += (_, _) => RefreshScroll();
    }

    public void SetActive(bool enabled)
    {
        active = enabled;
        editor.ScrollNote = enabled ? ScrollWheel : null;
        RefreshScroll();
    }

    public void ApplyColor(Color paper, Color edge)
    {
        BackColor = paper;
        accent = edge;
        Invalidate();
    }

    public void RefreshScroll()
    {
        if (refreshing || IsDisposed || !editor.IsHandleCreated) return;
        refreshing = true;
        try
        {
            int viewport = editor.ClientSize.Height;
            maximum = active && viewport > 0 ? Math.Max(0, editor.ContentBottom - viewport) : 0;
            bool show = maximum > 0;
            if (Visible != show) Visible = show;
            if (show) Invalidate();
        }
        finally { refreshing = false; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (maximum <= 0 || Height < 8 || Width < 5) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle track = Track();
        int viewport = editor.ClientSize.Height;
        int height = Math.Clamp((int)((long)track.Height * viewport / (viewport + maximum)),
            Math.Min(track.Height, LogicalToDeviceUnits(24)), track.Height);
        int offset = Math.Clamp(editor.ScrollOffset.Y, 0, maximum);
        thumb = new Rectangle(track.Left, track.Top + (int)((long)(track.Height - height) * offset / maximum), track.Width, height);
        Fill(track, Mix(accent, BackColor, 45));
        Fill(thumb, Mix(BoardStyle.Ink, accent, dragging || ClientRectangle.Contains(PointToClient(Cursor.Position)) ? 46 : 32));

        void Fill(Rectangle bounds, Color color)
        {
            using GraphicsPath path = BoardStyle.RoundedOutline(bounds, LogicalToDeviceUnits(4));
            using var brush = new SolidBrush(color);
            e.Graphics.FillPath(brush, path);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || thumb.IsEmpty || maximum == 0) return;
        grabOffset = thumb.Contains(e.Location) ? e.Y - thumb.Top : thumb.Height / 2;
        dragging = true;
        Capture = true;
        Cursor = Cursors.SizeNS;
        DragTo(e.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) DragTo(e.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Capture = false; }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        dragging = Capture;
        Cursor = dragging ? Cursors.SizeNS : Cursors.Hand;
        Invalidate();
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) => ScrollWheel(e.Delta);

    private Rectangle Track() => new(LogicalToDeviceUnits(3), 2, Math.Max(1, Width - LogicalToDeviceUnits(5)), Math.Max(1, Height - 4));

    private void DragTo(int y)
    {
        Rectangle track = Track();
        int travel = track.Height - thumb.Height;
        if (travel > 0) SetOffset((int)((long)(y - track.Top - grabOffset) * maximum / travel));
    }

    private void ScrollWheel(int delta) => SetOffset(editor.ScrollOffset.Y - delta * editor.Font.Height * 3 / 120);

    private void SetOffset(int y)
    {
        editor.ScrollToOffset(Math.Clamp(y, 0, maximum));
        Invalidate();
    }

    private static Color Mix(Color ink, Color background, int percent) => Color.FromArgb(
        (ink.R * percent + background.R * (100 - percent)) / 100,
        (ink.G * percent + background.G * (100 - percent)) / 100,
        (ink.B * percent + background.B * (100 - percent)) / 100);
}
