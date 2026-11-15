using System.Drawing.Drawing2D;

namespace Corkboard;

internal sealed class BoardWindow : ResizableWindow
{
    private readonly CorkboardApp app;
    private readonly CorkBoard board;
    private readonly BoardSurface canvas;
    private readonly Dictionary<Guid, NoteCard> cards = [];
    private readonly Dictionary<Guid, Image?> images = [];
    private readonly Dictionary<Guid, FloatingNote> floating = [];
    private readonly Button fab;
    private readonly Panel actions;
    private readonly Panel drawerClip = new() { Visible = false };
    private readonly Panel drawer = new() { BackColor = BoardStyle.Paper };
    private readonly ListBox boardList = new() { IntegralHeight = false, AccessibleName = "Corkboards" };
    private readonly ListBox savedNotes = new() { IntegralHeight = false, AccessibleName = "Saved notes" };
    private readonly Label notice;
    private readonly Panel emptyState;
    private readonly CorkMenu threadMenu = new();
    private readonly ToolTip tips = new();
    private readonly System.Windows.Forms.Timer drawerMotion = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer noticeTimer = new() { Interval = 5000 };
    private readonly ThreadPointerFilter threadInput;
    private Guid? connectFrom;
    private Point? threadPointer;
    private NoteCard? threadTarget;
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
        threadInput = new ThreadPointerFilter(this);
        canvas.MouseClick += CanvasClick;
        canvas.MouseMove += (_, e) =>
        {
            Cursor desired = !selectPin && !connectFrom.HasValue && board.Threads.Count > 0 && HitThread(e.Location) is not null
                ? Cursors.Hand : Cursors.Default;
            if (canvas.Cursor != desired) canvas.Cursor = desired;
        };
        canvas.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && HitThread(e.Location) is null && !selectPin && connectFrom is null) AddNote(e.Location);
        };
        emptyState = BuildEmptyState();
        canvas.Controls.Add(emptyState);

        notice = new Label
        {
            Visible = false, TextAlign = ContentAlignment.MiddleCenter, BackColor = BoardStyle.Paper,
            AutoEllipsis = true, Padding = new Padding(8, 0, 8, 0)
        };
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); notice.Visible = false; };

        fab = BoardStyle.Button("Board menu", BoardStyle.Paper, BoardStyle.Ink);
        fab.Font = new Font("Segoe UI Semibold", 10F);
        fab.TextAlign = ContentAlignment.MiddleLeft;
        fab.Padding = new Padding(40, 0, 9, 0);
        fab.FlatAppearance.BorderSize = 1;
        fab.FlatAppearance.BorderColor = BoardStyle.PanelTint;
        fab.AccessibleName = "Board actions";
        fab.Paint += PaintMenuGlyph;
        actions = BuildActions();
        actions.VisibleChanged += (_, _) => RefreshMenuButton();
        drawerClip.VisibleChanged += (_, _) => RefreshMenuButton();
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
        ClearSelectionMode();
        RememberBounds();
        app.Save();
        closing = true;
        foreach (FloatingNote popup in floating.Values.ToArray()) popup.Close();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (connectFrom.HasValue) ClearSelectionMode();
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
            if (connectFrom.HasValue) ClearSelectionMode();
            closing = true;
            foreach (FloatingNote popup in floating.Values.ToArray()) popup.Dispose();
            noticeTimer.Dispose();
            drawerMotion.Dispose();
            tips.Dispose();
            threadMenu.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (Image? image in images.Values) image?.Dispose();
            images.Clear();
        }
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
        tips.SetToolTip(notice, message);
        LayoutNotice();
        notice.Visible = true;
        noticeTimer.Stop();
        if (!stay) noticeTimer.Start();
    }

    public void FocusNote(StickyNote note)
    {
        if (!cards.TryGetValue(note.Id, out NoteCard? card)) return;
        AnimateDrawer(false);
        card.BringToFront();
        card.FocusEditor();
    }

    public void AddNote(Point? click = null)
    {
        AddNewCard(new StickyNote(), click);
    }

    private void ChooseImage()
    {
        actions.Visible = false;
        using var picker = new OpenFileDialog
        {
            Title = "Add image to corkboard", CheckFileExists = true, RestoreDirectory = true,
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff"
        };
        if (picker.ShowDialog(this) == DialogResult.OK) AddImage(picker.FileName);
    }

    public void AddImage(string path)
    {
        string file;
        try { file = app.Images.Import(path); }
        catch (Exception error) when (ImageLibrary.IsImageError(error))
        {
            ShowNotice(error is InvalidDataException ? error.Message : "Couldn’t add that image. Try a PNG or JPEG.");
            return;
        }
        string title = Path.GetFileNameWithoutExtension(path).Trim();
        if (title.Length > 45) title = title[..45];
        var note = new StickyNote { ImageFile = file, Label = title, Color = BoardStyle.Linen, CardWidth = 264 };
        Image? photo = ReadImage(note);
        note.CardHeight = photo is null ? 278 : Math.Clamp(80 + (int)Math.Round(239.0 * photo.Height / photo.Width), 205, 368);
        AddNewCard(note);
        app.Save();
    }

    private void AddNewCard(StickyNote note, Point? click = null)
    {
        actions.Visible = false;
        ClearSelectionMode();
        Size size = LimitCardResize(CardArea.Location, PreferredCardSize(note));
        Point position = click is Point point ? new Point(point.X - Pixels(14), point.Y - Pixels(15)) : NextOpenPosition(size);
        Point fitted = LimitCardMove(position, size);
        if (note.ImageFile is null) note.Color = board.Notes.Count % BoardStyle.Colors.Length;
        board.Notes.Add(note);
        MoveNote(note, fitted.X, fitted.Y);
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

    public Point LimitCardMove(Point wanted, Size size)
    {
        Rectangle area = CardArea;
        return new Point(Math.Clamp(wanted.X, area.Left, Math.Max(area.Left, area.Right - size.Width)),
            Math.Clamp(wanted.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height)));
    }

    public Size LimitCardResize(Point anchor, Size wanted)
    {
        Rectangle area = CardArea;
        int width = Math.Max(1, Math.Min(Pixels(NoteCard.Largest.Width), area.Right - anchor.X));
        int height = Math.Max(1, Math.Min(Pixels(NoteCard.Largest.Height), area.Bottom - anchor.Y));
        return new Size(Math.Clamp(wanted.Width, Math.Min(Pixels(NoteCard.Smallest.Width), width), width),
            Math.Clamp(wanted.Height, Math.Min(Pixels(NoteCard.Smallest.Height), height), height));
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
        if (floating.TryGetValue(note.Id, out FloatingNote? popup)) popup.ApplyColor(color);
        app.MarkDirty();
    }

    public void ChangePinColor(StickyNote note, int color)
    {
        if (note.PinColor == color) return;
        note.PinColor = color;
        if (cards.TryGetValue(note.Id, out NoteCard? card)) card.RefreshPin();
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
            bool added = !board.Threads.Any(thread => thread.From == source && thread.To == note.Id || thread.From == note.Id && thread.To == source);
            if (added)
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
        if (!note.Pinned || !cards.ContainsKey(note.Id)) return;
        ClearSelectionMode();
        connectFrom = note.Id;
        Application.AddMessageFilter(threadInput);
        UpdateThreadPointer(canvas.PointToClient(Cursor.Position));
        ShowNotice("Click a note · Esc to cancel", true);
    }

    public void ListThreadColors(StickyNote note, ToolStripMenuItem menu)
    {
        ClearMenu(menu.DropDownItems);
        NoteThread[] connected = board.Threads.Where(thread => thread.From == note.Id || thread.To == note.Id).ToArray();
        menu.Visible = connected.Length > 0;
        if (connected.Length == 1)
        {
            AddThreadColors(menu.DropDownItems, connected[0]);
            return;
        }

        foreach (NoteThread thread in connected)
        {
            Guid other = thread.From == note.Id ? thread.To : thread.From;
            StickyNote? destination = board.Notes.Find(candidate => candidate.Id == other);
            if (destination is null) continue;
            string label = new NoteListItem(destination).Caption;
            if (label.Length > 38) label = label[..37] + "…";
            var item = new ToolStripMenuItem(label.Replace("&", "&&"));
            AddThreadColors(item.DropDownItems, thread);
            menu.DropDownItems.Add(item);
        }
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
        app.DeleteNote(owner, board, note);
    }

    public void RemoveNoteView(StickyNote note)
    {
        if (connectFrom == note.Id) ClearSelectionMode();
        if (floating.TryGetValue(note.Id, out FloatingNote? popup)) popup.Close();
        if (cards.Remove(note.Id, out NoteCard? card)) card.Dispose();
        if (images.Remove(note.Id, out Image? image)) image?.Dispose();
        UpdateEmptyState();
        canvas.Invalidate();
    }

    public void RefreshBoardList()
    {
        CorkBoard selected = boardList.SelectedItem as CorkBoard ?? board;
        CorkBoard[] available = app.Boards.ToArray();
        boardList.BeginUpdate();
        boardList.Items.Clear();
        boardList.Items.AddRange(available);
        boardList.SelectedItem = available.Contains(selected) ? selected : available.Contains(board) ? board : available[0];
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
            ClearSelectionMode();
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
        Add("▧  Add image…", ChooseImage);
        Add("●  Add pin", () => { actions.Visible = false; ClearSelectionMode(); selectPin = true; ShowNotice("Click a pin · Esc to cancel", true); });
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
            button.Size = new Size(186, 30);
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
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
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
        var manageBoards = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        AddCommand(manageBoards, "Rename", () => { if (boardList.SelectedItem is CorkBoard selected) app.RenameBoard(this, selected); }, 76);
        AddCommand(manageBoards, "Delete board", () => { if (boardList.SelectedItem is CorkBoard selected) app.DeleteBoard(this, selected); }, 98, destructive: true);
        var noteCommands = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        AddCommand(noteCommands, "Open note", OpenSavedNote, 92);
        AddCommand(noteCommands, "Delete note", () =>
        {
            if (boardList.SelectedItem is CorkBoard selected && savedNotes.SelectedItem is NoteListItem item)
                app.DeleteNote(this, selected, item.Note);
        }, 96, destructive: true);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(boardList, 0, 1);
        layout.Controls.Add(commands, 0, 2);
        layout.Controls.Add(manageBoards, 0, 3);
        layout.Controls.Add(notesLabel, 0, 4);
        layout.Controls.Add(savedNotes, 0, 5);
        layout.Controls.Add(noteCommands, 0, 6);
        drawer.Controls.Add(layout);
        drawerClip.Controls.Add(drawer);

        void AddCommand(Control group, string text, Action click, int width, bool primary = false, bool destructive = false)
        {
            Button button = BoardStyle.Button(text, primary ? BoardStyle.Brick : BoardStyle.PanelTint,
                primary ? Color.White : destructive ? BoardStyle.Brick : BoardStyle.Ink);
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
        if (WindowState != FormWindowState.Minimized && canvas.ClientSize.Width > 0 && canvas.ClientSize.Height > 0)
            foreach (StickyNote note in board.Notes)
                if (cards.TryGetValue(note.Id, out NoteCard? card)) card.Bounds = PreferredCardBounds(note);
        emptyState.Location = new Point(Math.Max(8, (canvas.ClientSize.Width - emptyState.Width) / 2), Math.Max(8, (canvas.ClientSize.Height - emptyState.Height) / 2));
        fab.Bounds = new Rectangle(inset + Pixels(12), Math.Max(inset, ClientSize.Height - inset - Pixels(54)), Pixels(145), Pixels(42));
        BoardStyle.RoundCorners(fab, Pixels(21));
        actions.Bounds = new Rectangle(fab.Left, Math.Max(inset + Pixels(4), fab.Top - Pixels(312)), Pixels(202), Pixels(304));
        BoardStyle.RoundCorners(actions, Pixels(8));
        drawerClip.Location = new Point(fab.Left, inset + Pixels(9));
        drawerClip.Height = Math.Max(0, fab.Top - drawerClip.Top - Pixels(7));
        drawer.Size = new Size(DrawerWidth, drawerClip.Height);
        BoardStyle.RoundCorners(drawer, Pixels(8));
        LayoutNotice();
    }

    private void LayoutNotice()
    {
        int right = canvas.Right - Pixels(12);
        int room = Math.Max(1, right - fab.Right - Pixels(10));
        int measured = TextRenderer.MeasureText(notice.Text, notice.Font).Width + Pixels(24);
        int width = Math.Min(room, measured);
        int height = Pixels(28);
        notice.Bounds = new Rectangle(right - width, fab.Top + (fab.Height - height) / 2, width, height);
        BoardStyle.RoundCorners(notice, Pixels(14));
    }

    private void RefreshMenuButton()
    {
        fab.Text = actions.Visible || drawerClip.Visible ? "Close menu" : "Board menu";
        fab.Invalidate();
    }

    private void PaintMenuGlyph(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int centerX = Pixels(21);
        int centerY = fab.Height / 2;
        int radius = Pixels(13);
        using var accent = new SolidBrush(BoardStyle.Brick);
        using var line = new Pen(Color.White, Pixels(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.FillEllipse(accent, centerX - radius, centerY - radius, radius * 2, radius * 2);
        int stroke = Pixels(4);
        if (actions.Visible || drawerClip.Visible)
        {
            e.Graphics.DrawLine(line, centerX - stroke, centerY - stroke, centerX + stroke, centerY + stroke);
            e.Graphics.DrawLine(line, centerX - stroke, centerY + stroke, centerX + stroke, centerY - stroke);
        }
        else
        {
            for (int row = -1; row <= 1; row++)
                e.Graphics.DrawLine(line, centerX - stroke, centerY + row * stroke, centerX + stroke, centerY + row * stroke);
        }
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
        var card = new NoteCard(this, note, floating: false, image: ReadImage(note)) { Bounds = PreferredCardBounds(note) };
        cards.Add(note.Id, card);
        canvas.Controls.Add(card);
        card.BringToFront();
        return card;
    }

    private Point NextOpenPosition(Size size)
    {
        Rectangle area = CardArea;
        int gap = Pixels(14);
        for (int y = area.Top; y <= area.Bottom - size.Height; y += size.Height + gap)
            for (int x = area.Left; x <= area.Right - size.Width; x += size.Width + gap)
            {
                var slot = new Rectangle(new Point(x, y), size);
                if (!cards.Values.Any(card => card.Bounds.IntersectsWith(slot))) return slot.Location;
            }
        int offset = Pixels(18) * (cards.Count % 7);
        return LimitCardMove(new Point(area.Left + offset, area.Top + offset), size);
    }

    private Rectangle CardArea => new(Pixels(12), Pixels(12), Math.Max(1, canvas.ClientSize.Width - Pixels(24)),
        Math.Max(1, canvas.ClientSize.Height - Pixels(72)));

    private Size PreferredCardSize(StickyNote note) => new(
        Pixels(ClampDimension(note.CardWidth, NoteCard.InitialCardSize.Width, NoteCard.Smallest.Width, NoteCard.Largest.Width)),
        Pixels(ClampDimension(note.CardHeight, NoteCard.InitialCardSize.Height, NoteCard.Smallest.Height, NoteCard.Largest.Height)));

    private Rectangle PreferredCardBounds(StickyNote note)
    {
        var position = new Point(Pixels(Math.Clamp(note.X, 0, 16384)), Pixels(Math.Clamp(note.Y, 0, 16384)));
        return FitPreferredCard(position, PreferredCardSize(note));
    }

    private Image? ReadImage(StickyNote note)
    {
        if (note.ImageFile is null) return null;
        if (!images.TryGetValue(note.Id, out Image? image)) images[note.Id] = image = app.Images.Load(note.ImageFile);
        return image;
    }

    private Rectangle FitPreferredCard(Point location, Size wanted)
    {
        Rectangle area = CardArea;
        var minimum = new Size(Math.Min(Pixels(NoteCard.Smallest.Width), area.Width), Math.Min(Pixels(NoteCard.Smallest.Height), area.Height));
        Point anchor = LimitCardMove(location, minimum);
        return new Rectangle(anchor, LimitCardResize(anchor, wanted));
    }

    private void OpenFloating(StickyNote note, bool activate)
    {
        if (!floating.TryGetValue(note.Id, out FloatingNote? window))
        {
            window = new FloatingNote(this, note, app.Icon, activate, ReadImage(note));
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
        if (board.Threads.Count == 0 && threadPointer is null) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.Black, Pixels(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (NoteThread thread in board.Threads)
        {
            if (!TryEndpoints(thread, out Point start, out Point end)) continue;
            pen.Color = BoardStyle.ThreadColors[Math.Clamp(thread.Color, 0, BoardStyle.ThreadColors.Length - 1)].Color;
            graphics.DrawLine(pen, start, end);
        }
        if (threadPointer is Point pointer && connectFrom is Guid source && cards.TryGetValue(source, out NoteCard? card))
        {
            pen.Color = BoardStyle.ThreadColors[0].Color;
            pen.DashStyle = DashStyle.Dash;
            graphics.DrawLine(pen, card.PinPointOnParent, pointer);
        }
    }

    private void CanvasClick(object? sender, MouseEventArgs e)
    {
        actions.Visible = false;
        if (HitThread(e.Location) is not NoteThread thread || selectPin || connectFrom.HasValue) return;
        ClearMenu(threadMenu.Items);
        var heading = new ToolStripLabel("Thread color");
        threadMenu.Items.Add(heading);
        AddThreadColors(threadMenu.Items, thread);
        threadMenu.Items.Add(new ToolStripSeparator());
        threadMenu.Items.Add("Remove thread", null, (_, _) => { board.Threads.Remove(thread); canvas.Invalidate(); app.MarkDirty(); });
        threadMenu.Show(canvas, e.Location);
    }

    private void AddThreadColors(ToolStripItemCollection items, NoteThread thread)
    {
        for (int i = 0; i < BoardStyle.ThreadColors.Length; i++)
        {
            int color = i;
            var item = new ToolStripMenuItem(BoardStyle.ThreadColors[i].Name) { Checked = thread.Color == i, ForeColor = BoardStyle.ThreadColors[i].Color };
            if (i == 4) item.ForeColor = BoardStyle.Ink;
            item.Click += (_, _) => { thread.Color = color; canvas.Invalidate(); app.MarkDirty(); };
            items.Add(item);
        }
    }

    private static void ClearMenu(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items.Cast<ToolStripItem>().ToArray()) item.Dispose();
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
        if (!connectFrom.HasValue) return;
        connectFrom = null;
        Application.RemoveMessageFilter(threadInput);
        threadPointer = null;
        threadTarget?.SetConnectionTarget(false);
        threadTarget = null;
        canvas.Invalidate();
    }

    private void UpdateThreadPointer(Point wanted)
    {
        if (connectFrom is not Guid source || !cards.TryGetValue(source, out NoteCard? from)) return;
        var next = new Point(Math.Clamp(wanted.X, 0, Math.Max(0, canvas.ClientSize.Width - 1)),
            Math.Clamp(wanted.Y, 0, Math.Max(0, canvas.ClientSize.Height - 1)));
        NoteCard? target = TargetAt(wanted);
        if (target != threadTarget)
        {
            threadTarget?.SetConnectionTarget(false);
            threadTarget = target;
            target?.SetConnectionTarget(true);
        }
        if (threadPointer == next) return;
        Point anchor = from.PinPointOnParent;
        if (threadPointer is Point previous) canvas.Invalidate(PreviewBounds(anchor, previous));
        threadPointer = next;
        canvas.Invalidate(PreviewBounds(anchor, next));
    }

    private NoteCard? TargetAt(Point point)
    {
        if (!canvas.ClientRectangle.Contains(point)) return null;
        return canvas.GetChildAtPoint(point, GetChildAtPointSkip.Invisible) is NoteCard card && card.Note.Id != connectFrom ? card : null;
    }

    private Rectangle PreviewBounds(Point from, Point to) => Rectangle.Inflate(
        Rectangle.FromLTRB(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Max(from.X, to.X) + 1, Math.Max(from.Y, to.Y) + 1),
        Pixels(4), Pixels(4));

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
    private bool IsConnecting => connectFrom.HasValue;
    private static int ClampDimension(int value, int fallback, int minimum, int maximum) => value == 0 ? fallback : Math.Clamp(value, minimum, maximum);

    private sealed class ThreadPointerFilter(BoardWindow window) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message message)
        {
            const int mouseMove = 0x0200, leftDown = 0x0201, leftDoubleClick = 0x0203, rightDown = 0x0204;
            if (message.Msg is not (mouseMove or leftDown or leftDoubleClick or rightDown) || !window.IsConnecting
                || Control.FromChildHandle(message.HWnd) is not Control control || control.FindForm() != window) return false;

            Point location = window.canvas.PointToClient(Cursor.Position);
            if (message.Msg == mouseMove)
            {
                window.UpdateThreadPointer(location);
                return false;
            }
            if (control != window.canvas && !window.canvas.Contains(control))
            {
                window.ClearSelectionMode();
                return false;
            }
            if (message.Msg == rightDown) window.ClearSelectionMode();
            else if (window.TargetAt(location) is NoteCard target) window.HandlePinSelection(target.Note);
            return true;
        }
    }

    private sealed class BoardSurface : Panel
    {
        private readonly Action<Graphics> paintThreads;

        public BoardSurface(Action<Graphics> paintThreads)
        {
            this.paintThreads = paintThreads;
            DoubleBuffered = true;
            BackColor = BoardStyle.Cork;
            BackgroundImage = BoardStyle.CorkTile;
            BackgroundImageLayout = ImageLayout.Tile;
            AccessibleName = "Corkboard surface";
            SetStyle(ControlStyles.StandardDoubleClick | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); paintThreads(e.Graphics); }
    }

    private sealed record NoteListItem(StickyNote Note)
    {
        public string Caption => !string.IsNullOrWhiteSpace(Note.Label) ? Note.Label
            : Note.ImageFile is not null ? "Image" : Note.Text.Split(['\r', '\n'], 2)[0].Trim() is { Length: > 0 } first ? first : "Untitled note";
    }
}
