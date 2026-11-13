using System.Drawing.Drawing2D;

namespace Corkboard;

internal sealed class BoardWindow : ResizableWindow
{
    private readonly CorkboardApp app;
    private readonly CorkBoard board;
    private readonly BoardSurface canvas;
    private readonly Dictionary<Guid, NoteCard> cards = [];
    private readonly Dictionary<Guid, FloatingNote> floating = [];
    private readonly Button fab;
    private readonly Panel actions;
    private readonly Panel drawerClip = new() { Visible = false };
    private readonly Panel drawer = new() { BackColor = BoardStyle.Paper };
    private readonly ListBox boardList = new() { IntegralHeight = false, AccessibleName = "Corkboards" };
    private readonly ListBox savedNotes = new() { IntegralHeight = false, AccessibleName = "Saved notes" };
    private readonly Label notice;
    private readonly Panel emptyState;
    private readonly ContextMenuStrip threadMenu = new();
    private readonly ToolTip tips = new();
    private readonly System.Windows.Forms.Timer drawerMotion = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer noticeTimer = new() { Interval = 5000 };
    private Guid? connectFrom;
    private bool selectPin;
    private bool initialized;
    private bool hiding;
    private bool drawerOpening;
    private bool closing;
    private Rectangle? lastNormalBounds;
    private Rectangle? hiddenBounds;
    private bool hiddenMaximized;

    protected override int FrameDragWidth => 24;
    private int Frame => Pixels(FrameDragWidth);
    private int DrawerWidth => Pixels(280);

    public BoardWindow(CorkboardApp app, CorkBoard board)
    {
        this.app = app;
        this.board = board;
        Text = board.Name;
        Icon = app.Icon;
        Font = new Font("Segoe UI", 10F);
        ForeColor = BoardStyle.Ink;
        BackColor = BoardStyle.Cork;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        MinimumSize = new Size(392, 414);
        ClientSize = new Size(980, 620);
        StartPosition = FormStartPosition.CenterScreen;
        if (board.Bounds is Rectangle previous)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = FitOnScreen(previous, MinimumSize, Screen.FromPoint(Cursor.Position));
        }
        ResizeEnd += (_, _) => RememberBounds();

        canvas = new BoardSurface(DrawThreads);
        canvas.MouseClick += CanvasClick;
        canvas.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && HitThread(e.Location) is null && !selectPin && connectFrom is null) AddNote(e.Location);
        };
        emptyState = BuildEmptyState();
        canvas.Controls.Add(emptyState);

        notice = new Label { Visible = false, TextAlign = ContentAlignment.MiddleCenter, BackColor = BoardStyle.Paper, AutoEllipsis = true };
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); notice.Visible = false; };

        fab = BoardStyle.Button("≡", BoardStyle.Brick, Color.White);
        fab.Font = new Font("Segoe UI", 19F);
        fab.AccessibleName = "Board actions";
        tips.SetToolTip(fab, "Board actions");
        actions = BuildActions();
        fab.Click += (_, _) =>
        {
            if (drawerClip.Visible) { AnimateDrawer(false); return; }
            actions.Visible = !actions.Visible;
            if (actions.Visible) actions.SelectNextControl(null, true, true, true, true);
        };
        BuildDrawer();
        drawerMotion.Tick += (_, _) => SlideDrawer();

        Controls.AddRange([canvas, drawerClip, actions, fab, notice]);
        notice.BringToFront();
        fab.BringToFront();
        actions.BringToFront();
        drawerClip.BringToFront();
        LayoutBoard();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (initialized) return;
        initialized = true;
        canvas.SuspendLayout();
        foreach (StickyNote note in board.Notes) AddCard(note);
        canvas.ResumeLayout();
        UpdateEmptyState();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (closing) return;
        foreach (StickyNote note in board.Notes.Where(note => note.PoppedOut)) OpenFloating(note, false);
        RememberBounds();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        BoardStyle.DrawFrame(e.Graphics, ClientSize.Width, ClientSize.Height, Frame);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        TrackNormalBounds();
        if (canvas is not null) LayoutBoard();
        if (WindowState == FormWindowState.Minimized && IsHandleCreated && !hiding)
            BeginInvoke(() => { if (!closing && WindowState == FormWindowState.Minimized) MinimizeBoard(); });
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        TrackNormalBounds();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        RememberBounds();
        app.Save();
        closing = true;
        foreach (FloatingNote popup in floating.Values.ToArray()) popup.Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keys)
    {
        if (keys == (Keys.Control | Keys.N)) { AddNote(); return true; }
        if (keys == (Keys.Control | Keys.Shift | Keys.B)) { actions.Visible = false; AnimateDrawer(!drawerClip.Visible); return true; }
        if (keys == Keys.Escape && (selectPin || connectFrom.HasValue || actions.Visible || drawerClip.Visible))
        {
            ClearSelectionMode();
            actions.Visible = false;
            AnimateDrawer(false);
            fab.Focus();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keys);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true;
            foreach (FloatingNote popup in floating.Values.ToArray()) popup.Dispose();
            noticeTimer.Dispose();
            drawerMotion.Dispose();
            tips.Dispose();
            threadMenu.Dispose();
        }
        base.Dispose(disposing);
    }

    public void ShowBoard()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (hiddenBounds is Rectangle previous)
        {
            hiddenBounds = null;
            Bounds = FitOnScreen(previous, MinimumSize, Screen.FromControl(this));
            if (hiddenMaximized)
            {
                MaximizedBounds = Screen.FromControl(this).WorkingArea;
                WindowState = FormWindowState.Maximized;
            }
        }
        Activate();
    }

    public void ShowNotice(string message, bool stay = false)
    {
        notice.Text = message;
        notice.Visible = true;
        noticeTimer.Stop();
        if (!stay) noticeTimer.Start();
    }

    public void FocusNote(StickyNote note)
    {
        if (!cards.TryGetValue(note.Id, out NoteCard? card)) return;
        AnimateDrawer(false);
        canvas.ScrollControlIntoView(card);
        card.BringToFront();
        card.FocusEditor();
    }

    public void AddNote(Point? click = null)
    {
        actions.Visible = false;
        ClearSelectionMode();
        Point scroll = canvas.AutoScrollPosition;
        Point position = click is Point point
            ? new Point(Math.Max(8, point.X - scroll.X - 14), Math.Max(8, point.Y - scroll.Y - 15))
            : NextOpenPosition();
        var note = new StickyNote { Color = board.Notes.Count % BoardStyle.Colors.Length };
        board.Notes.Add(note);
        MoveNote(note, position.X, position.Y);
        AddCard(note);
        UpdateEmptyState();
        FocusNote(note);
        RefreshSavedNotes();
        app.MarkDirty();
    }

    public void MoveNote(StickyNote note, int x, int y)
    {
        if (note.Pinned) return;
        int logicalX = (int)Math.Round(x * 96.0 / DeviceDpi);
        int logicalY = (int)Math.Round(y * 96.0 / DeviceDpi);
        if (note.X == logicalX && note.Y == logicalY) return;
        note.X = logicalX;
        note.Y = logicalY;
        app.MarkDirty();
    }

    public void SaveCardSize(StickyNote note, Size size)
    {
        int width = (int)Math.Round(size.Width * 96.0 / DeviceDpi);
        int height = (int)Math.Round(size.Height * 96.0 / DeviceDpi);
        if (note.CardWidth == width && note.CardHeight == height) return;
        note.CardWidth = width;
        note.CardHeight = height;
        canvas.Invalidate();
        app.MarkDirty();
    }

    public void ChangeText(StickyNote note, string text, string rtf, NoteCard sender)
    {
        if (note.Text == text && note.FormattedText == rtf) return;
        note.Text = text;
        note.FormattedText = rtf;
        if (cards.TryGetValue(note.Id, out NoteCard? card) && card != sender) card.ReadText();
        if (floating.TryGetValue(note.Id, out FloatingNote? popup) && popup.Card != sender) popup.Card.ReadText();
        if (drawerClip.Visible) RefreshSavedNotes();
        app.MarkDirty();
    }

    public void EditLabel(StickyNote note, Form owner)
    {
        string? label = NamePrompt.Show(owner, "Label note", "Category or label", note.Label, 45);
        if (label is null || label == note.Label) return;
        note.Label = label;
        cards[note.Id].RefreshLabel();
        if (floating.TryGetValue(note.Id, out FloatingNote? popup))
        {
            popup.Card.RefreshLabel();
            popup.Text = string.IsNullOrWhiteSpace(label) ? "Sticky note" : label;
        }
        RefreshSavedNotes();
        app.MarkDirty();
    }

    public void ChangeColor(StickyNote note, int color)
    {
        if (note.Color == color) return;
        note.Color = color;
        cards[note.Id].ApplyColor();
        if (floating.TryGetValue(note.Id, out FloatingNote? popup)) popup.Card.ApplyColor();
        app.MarkDirty();
    }

    public void SetPinned(StickyNote note, bool pinned)
    {
        if (note.Pinned == pinned) return;
        note.Pinned = pinned;
        cards[note.Id].RefreshPin();
        if (!pinned)
        {
            board.Threads.RemoveAll(thread => thread.From == note.Id || thread.To == note.Id);
            canvas.Invalidate();
            if (connectFrom == note.Id) ClearSelectionMode();
        }
        app.MarkDirty();
    }

    public bool HandlePinSelection(StickyNote note)
    {
        if (connectFrom is Guid source)
        {
            if (source == note.Id) return true;
            if (!board.Threads.Any(thread => thread.From == source && thread.To == note.Id || thread.From == note.Id && thread.To == source))
            {
                SetPinned(note, true);
                board.Threads.Add(new NoteThread { From = source, To = note.Id });
                canvas.Invalidate();
                app.MarkDirty();
            }
            ClearSelectionMode();
            return true;
        }
        if (!selectPin) return false;
        SetPinned(note, true);
        ClearSelectionMode();
        return true;
    }

    public void StartThread(StickyNote note)
    {
        selectPin = false;
        connectFrom = note.Id;
        ShowNotice("Click another note’s pin to connect it. Esc cancels.", true);
    }

    public void PopOut(StickyNote note)
    {
        if (!note.PoppedOut) { note.PoppedOut = true; app.MarkDirty(); }
        OpenFloating(note, true);
    }

    public void ToggleAlwaysOnTop(StickyNote note)
    {
        note.AlwaysOnTop = !note.PoppedOut || !note.AlwaysOnTop;
        PopOut(note);
        floating[note.Id].TopMost = note.AlwaysOnTop;
        app.MarkDirty();
    }

    public void SavePopupBounds(StickyNote note, Rectangle bounds)
    {
        if (closing || bounds == note.PopupBounds) return;
        note.PopupBounds = bounds;
        app.MarkDirty();
    }

    public void DeleteNote(StickyNote note, Form owner)
    {
        if (MessageBox.Show(owner, "Delete this note?", "Corkboard", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        SetPinned(note, false);
        if (floating.TryGetValue(note.Id, out FloatingNote? popup)) popup.Close();
        if (cards.Remove(note.Id, out NoteCard? card)) card.Dispose();
        board.Notes.Remove(note);
        UpdateEmptyState();
        RefreshSavedNotes();
        app.MarkDirty();
    }

    public void RefreshBoardList()
    {
        CorkBoard selected = boardList.SelectedItem as CorkBoard ?? board;
        boardList.BeginUpdate();
        boardList.Items.Clear();
        boardList.Items.AddRange(app.Boards.Cast<object>().ToArray());
        boardList.SelectedItem = selected;
        boardList.EndUpdate();
        RefreshSavedNotes();
    }

    private void MinimizeBoard()
    {
        if (!Visible || hiding) return;
        hiding = true;
        try
        {
            actions.Visible = false;
            RememberBounds();
            if (!app.CanHideInTray(board))
            {
                ShowInTaskbar = true;
                WindowState = FormWindowState.Minimized;
                ShowNotice("Tray unavailable. Corkboard is on the taskbar.");
                return;
            }
            hiddenBounds = lastNormalBounds ?? board.Bounds;
            hiddenMaximized = WindowState == FormWindowState.Maximized;
            Hide();
            ShowInTaskbar = false;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        }
        finally { hiding = false; }
    }

    private Panel BuildActions()
    {
        var panel = new Panel { BackColor = BoardStyle.Paper, Visible = false };
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };
        Add("＋  Add note", () => AddNote());
        Add("●  Add pin", () => { actions.Visible = false; selectPin = true; connectFrom = null; ShowNotice("Click a note’s pin to lock it. Esc cancels.", true); });
        Add("≡  Corkboards", () => { actions.Visible = false; AnimateDrawer(true); });
        Separator();
        Add("−  Minimize to tray", MinimizeBoard);
        Add("□  Maximize / restore", () =>
        {
            actions.Visible = false;
            MaximizedBounds = Screen.FromControl(this).WorkingArea;
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        });
        Button startup = Add("Start with Windows", () =>
        {
            try { Startup.SetEnabled(!Startup.IsEnabled()); }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { ShowNotice("Windows couldn’t change the startup setting."); }
        });
        startup.Click += (_, _) => UpdateStartupLabel();
        Add("×  Close board", Close);
        Separator();
        Add("Exit Corkboard", app.Exit);
        panel.VisibleChanged += (_, _) => { if (panel.Visible) UpdateStartupLabel(); };
        panel.Controls.Add(stack);
        return panel;

        Button Add(string text, Action action)
        {
            Button button = BoardStyle.Button(text, BoardStyle.Paper, BoardStyle.Ink);
            button.Size = new Size(186, 32);
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(8, 0, 4, 0);
            button.Click += (_, _) => action();
            stack.Controls.Add(button);
            return button;
        }
        void Separator()
        {
            var line = new Panel { Size = new Size(166, 1), BackColor = BoardStyle.PanelTint, Margin = new Padding(10, 4, 0, 4), TabStop = false };
            stack.Controls.Add(line);
        }
        void UpdateStartupLabel()
        {
            try { startup.Text = Startup.IsEnabled() ? "✓  Start with Windows" : "○  Start with Windows"; }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { startup.Enabled = false; }
        }
    }

    private void BuildDrawer()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        var heading = new Label { Text = "Corkboards", Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 13F) };
        var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        Button dismiss = BoardStyle.Button("×", BoardStyle.Paper, BoardStyle.Ink);
        dismiss.Dock = DockStyle.Right;
        dismiss.Width = 32;
        dismiss.AccessibleName = "Close board list";
        dismiss.Click += (_, _) => AnimateDrawer(false);
        header.Controls.Add(heading);
        header.Controls.Add(dismiss);
        boardList.DisplayMember = nameof(CorkBoard.Name);
        boardList.SelectedIndexChanged += (_, _) => RefreshSavedNotes();
        boardList.DoubleClick += (_, _) => OpenSelected();
        boardList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenSelected(); } };
        boardList.Dock = DockStyle.Fill;
        boardList.BackColor = Color.White;
        boardList.BorderStyle = BorderStyle.FixedSingle;
        savedNotes.DisplayMember = nameof(NoteListItem.Caption);
        savedNotes.Dock = DockStyle.Fill;
        savedNotes.DoubleClick += (_, _) => OpenSavedNote();
        savedNotes.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenSavedNote(); } };
        savedNotes.BorderStyle = BorderStyle.FixedSingle;
        var notesLabel = new Label { Text = "Saved notes", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, Font = new Font("Segoe UI Semibold", 10F) };

        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        AddCommand(commands, "New board", () => app.CreateBoard(this), 90, true);
        AddCommand(commands, "Open", OpenSelected, 62);
        AddCommand(commands, "Rename", () => { if (boardList.SelectedItem is CorkBoard selected) app.RenameBoard(this, selected); }, 76);
        var noteCommands = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        AddCommand(noteCommands, "Open note", OpenSavedNote, 92);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(boardList, 0, 1);
        layout.Controls.Add(commands, 0, 2);
        layout.Controls.Add(notesLabel, 0, 3);
        layout.Controls.Add(savedNotes, 0, 4);
        layout.Controls.Add(noteCommands, 0, 5);
        drawer.Controls.Add(layout);
        drawerClip.Controls.Add(drawer);

        void AddCommand(Control group, string text, Action click, int width, bool primary = false)
        {
            Button button = BoardStyle.Button(text, primary ? BoardStyle.Brick : BoardStyle.PanelTint, primary ? Color.White : BoardStyle.Ink);
            button.Size = new Size(width, 28);
            button.Margin = new Padding(0, 3, 4, 0);
            button.Click += (_, _) => click();
            group.Controls.Add(button);
        }
        void OpenSelected() { if (boardList.SelectedItem is CorkBoard selected) app.Open(selected); }
        void OpenSavedNote()
        {
            if (boardList.SelectedItem is CorkBoard selected && savedNotes.SelectedItem is NoteListItem item)
                app.Open(selected).FocusNote(item.Note);
        }
    }

    private void RefreshSavedNotes()
    {
        Guid? selected = (savedNotes.SelectedItem as NoteListItem)?.Note.Id;
        savedNotes.BeginUpdate();
        savedNotes.Items.Clear();
        if (boardList.SelectedItem is CorkBoard source)
            foreach (StickyNote note in source.Notes)
            {
                var item = new NoteListItem(note);
                savedNotes.Items.Add(item);
                if (note.Id == selected) savedNotes.SelectedItem = item;
            }
        savedNotes.EndUpdate();
    }

    private void AnimateDrawer(bool open)
    {
        if (!open && !drawerClip.Visible) return;
        drawerOpening = open;
        if (open)
        {
            RefreshBoardList();
            drawerClip.Visible = true;
        }
        LayoutBoard();
        drawerMotion.Start();
    }

    private void SlideDrawer()
    {
        int next = Math.Clamp(drawerClip.Width + (drawerOpening ? Pixels(40) : -Pixels(40)), 0, DrawerWidth);
        drawerClip.Width = next;
        drawer.Left = next - DrawerWidth;
        if (next != (drawerOpening ? DrawerWidth : 0)) return;
        drawerMotion.Stop();
        if (drawerOpening) boardList.Focus();
        else drawerClip.Visible = false;
    }

    private void LayoutBoard()
    {
        int inset = Frame;
        canvas.Bounds = new Rectangle(inset, inset, Math.Max(0, ClientSize.Width - inset * 2), Math.Max(0, ClientSize.Height - inset * 2));
        emptyState.Location = new Point(Math.Max(8, (canvas.ClientSize.Width - emptyState.Width) / 2), Math.Max(8, (canvas.ClientSize.Height - emptyState.Height) / 2));
        fab.Bounds = new Rectangle(inset + Pixels(11), Math.Max(inset, ClientSize.Height - inset - Pixels(54)), Pixels(44), Pixels(44));
        using var shape = new GraphicsPath();
        shape.AddEllipse(0, 0, fab.Width, fab.Height);
        Region? previous = fab.Region;
        fab.Region = new Region(shape);
        previous?.Dispose();
        actions.Bounds = new Rectangle(fab.Left, Math.Max(inset + Pixels(4), fab.Top - Pixels(298)), Pixels(202), Pixels(290));
        BoardStyle.RoundCorners(actions, Pixels(8));
        drawerClip.Location = new Point(fab.Left, inset + Pixels(9));
        drawerClip.Height = Math.Max(0, fab.Top - drawerClip.Top - Pixels(7));
        drawer.Size = new Size(DrawerWidth, drawerClip.Height);
        BoardStyle.RoundCorners(drawer, Pixels(8));
        notice.Bounds = new Rectangle(inset + Pixels(9), inset + Pixels(8), Math.Max(0, canvas.Width - Pixels(18)), Pixels(27));
    }

    private Panel BuildEmptyState()
    {
        var panel = new Panel { Size = new Size(276, 132), BackColor = BoardStyle.Paper };
        BoardStyle.RoundCorners(panel, 9);
        var label = new Label { Text = "Double-click anywhere to add a note", Bounds = new Rectangle(0, 37, 276, 27), TextAlign = ContentAlignment.MiddleCenter, ForeColor = BoardStyle.Muted };
        Button button = BoardStyle.Button("Add note", BoardStyle.Brick, Color.White);
        button.Bounds = new Rectangle(83, 77, 110, 34);
        button.Click += (_, _) => AddNote();
        panel.Controls.AddRange([label, button]);
        panel.Paint += (_, e) => BoardStyle.DrawPin(e.Graphics, new Point(panel.Width / 2, 22));
        return panel;
    }

    private void UpdateEmptyState()
    {
        emptyState.Visible = board.Notes.Count == 0;
        if (emptyState.Visible) LayoutBoard();
    }

    private NoteCard AddCard(StickyNote note)
    {
        Point scroll = canvas.AutoScrollPosition;
        var card = new NoteCard(this, note, floating: false)
        {
            Size = new Size(Pixels(ClampDimension(note.CardWidth, NoteCard.InitialCardSize.Width, NoteCard.Smallest.Width, NoteCard.Largest.Width)),
                Pixels(ClampDimension(note.CardHeight, NoteCard.InitialCardSize.Height, NoteCard.Smallest.Height, NoteCard.Largest.Height))),
            Location = new Point(Pixels(Math.Max(8, note.X)) + scroll.X, Pixels(Math.Max(8, note.Y)) + scroll.Y)
        };
        cards.Add(note.Id, card);
        canvas.Controls.Add(card);
        card.SizeChanged += (_, _) => canvas.Invalidate(card.Bounds);
        card.BringToFront();
        return card;
    }

    private Point NextOpenPosition()
    {
        int inset = Pixels(15);
        int stepX = Pixels(NoteCard.InitialCardSize.Width + 16);
        int stepY = Pixels(NoteCard.InitialCardSize.Height + 16);
        int columns = Math.Max(1, canvas.ClientSize.Width / stepX);
        Point scroll = canvas.AutoScrollPosition;
        for (int row = 0; ; row++)
            for (int column = 0; column < columns; column++)
            {
                var slot = new Rectangle(inset + column * stepX, inset + row * stepY, Pixels(NoteCard.InitialCardSize.Width), Pixels(NoteCard.InitialCardSize.Height));
                if (!cards.Values.Any(card => new Rectangle(card.Left - scroll.X, card.Top - scroll.Y, card.Width, card.Height).IntersectsWith(slot)))
                    return slot.Location;
            }
    }

    private void OpenFloating(StickyNote note, bool activate)
    {
        if (!floating.TryGetValue(note.Id, out FloatingNote? window))
        {
            window = new FloatingNote(this, note, app.Icon, activate);
            floating.Add(note.Id, window);
            window.FormClosed += (_, _) =>
            {
                floating.Remove(note.Id);
                if (closing) return;
                note.PoppedOut = false;
                app.MarkDirty();
            };
            window.Show();
        }
        if (!activate) return;
        if (!window.Visible) window.Show();
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.Activate();
        window.Card.FocusEditor();
    }

    private void DrawThreads(Graphics graphics)
    {
        if (board.Threads.Count == 0) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.Black, Pixels(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (NoteThread thread in board.Threads)
        {
            if (!TryEndpoints(thread, out Point start, out Point end)) continue;
            pen.Color = BoardStyle.ThreadColors[Math.Clamp(thread.Color, 0, BoardStyle.ThreadColors.Length - 1)].Color;
            graphics.DrawLine(pen, start, end);
        }
    }

    private void CanvasClick(object? sender, MouseEventArgs e)
    {
        actions.Visible = false;
        if (HitThread(e.Location) is not NoteThread thread || selectPin || connectFrom.HasValue) return;
        foreach (ToolStripItem item in threadMenu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        for (int i = 0; i < BoardStyle.ThreadColors.Length; i++)
        {
            int color = i;
            var item = new ToolStripMenuItem(BoardStyle.ThreadColors[i].Name) { Checked = thread.Color == i, ForeColor = BoardStyle.ThreadColors[i].Color };
            if (i == 4) item.ForeColor = BoardStyle.Ink;
            item.Click += (_, _) => { thread.Color = color; canvas.Invalidate(); app.MarkDirty(); };
            threadMenu.Items.Add(item);
        }
        threadMenu.Items.Add(new ToolStripSeparator());
        threadMenu.Items.Add("Remove thread", null, (_, _) => { board.Threads.Remove(thread); canvas.Invalidate(); app.MarkDirty(); });
        threadMenu.Show(canvas, e.Location);
    }

    private NoteThread? HitThread(Point point) => board.Threads.LastOrDefault(thread =>
    {
        if (!TryEndpoints(thread, out Point start, out Point end)) return false;
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double squaredLength = dx * dx + dy * dy;
        if (squaredLength == 0) return false;
        double t = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / squaredLength, 0, 1);
        double x = point.X - start.X - t * dx;
        double y = point.Y - start.Y - t * dy;
        int radius = Pixels(7);
        return x * x + y * y <= radius * radius;
    });

    private bool TryEndpoints(NoteThread thread, out Point start, out Point end)
    {
        start = end = Point.Empty;
        if (!cards.TryGetValue(thread.From, out NoteCard? from) || !cards.TryGetValue(thread.To, out NoteCard? to)) return false;
        start = from.PinPointOnParent;
        end = to.PinPointOnParent;
        return true;
    }

    private void ClearSelectionMode()
    {
        if (selectPin || connectFrom.HasValue) notice.Visible = false;
        selectPin = false;
        connectFrom = null;
    }

    private void RememberBounds()
    {
        TrackNormalBounds();
        if (!initialized || (hiddenBounds ?? lastNormalBounds) is not Rectangle wanted || board.Bounds == wanted) return;
        board.Bounds = wanted;
        app.MarkDirty();
    }

    private void TrackNormalBounds()
    {
        if (initialized && !hiding && hiddenBounds is null && WindowState == FormWindowState.Normal
            && Width >= MinimumSize.Width && Height >= MinimumSize.Height)
            lastNormalBounds = Bounds;
    }

    private int Pixels(int value) => LogicalToDeviceUnits(value);
    private static int ClampDimension(int value, int fallback, int minimum, int maximum) => value == 0 ? fallback : Math.Clamp(value, minimum, maximum);

    private sealed class BoardSurface : Panel
    {
        private readonly Action<Graphics> paintThreads;

        public BoardSurface(Action<Graphics> paintThreads)
        {
            this.paintThreads = paintThreads;
            DoubleBuffered = true;
            AutoScroll = true;
            BackColor = BoardStyle.Cork;
            BackgroundImage = BoardStyle.CorkTile;
            BackgroundImageLayout = ImageLayout.Tile;
            AccessibleName = "Corkboard surface";
            SetStyle(ControlStyles.StandardDoubleClick | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); paintThreads(e.Graphics); }
        protected override void OnScroll(ScrollEventArgs e) { base.OnScroll(e); Invalidate(); }
    }

    private sealed record NoteListItem(StickyNote Note)
    {
        public string Caption => !string.IsNullOrWhiteSpace(Note.Label) ? Note.Label
            : Note.Text.Split(['\r', '\n'], 2)[0].Trim() is { Length: > 0 } first ? first : "Untitled note";
    }
}
