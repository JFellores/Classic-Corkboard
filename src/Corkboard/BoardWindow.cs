using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Corkboard;

internal sealed class BoardWindow : ResizableWindow
{
    private static readonly Color NavigationPaper = Color.FromArgb(250, 236, 214);
    private static readonly Color ActiveSlidePaper = Color.FromArgb(231, 204, 168);
    private readonly CorkboardApp app;
    private readonly CorkBoard board;
    private readonly Panel corkViewport;
    private readonly BoardSurface canvas;
    private readonly Dictionary<Guid, StickyNote> notes;
    private readonly Dictionary<Guid, NoteCard> cards = [];
    private readonly Dictionary<Guid, Image?> images = [];
    private readonly Dictionary<Guid, FloatingNote> floating = [];
    private readonly Button fab;
    private readonly Button bell;
    private readonly BoardActionMenu actions;
    private readonly Panel slideControls;
    private readonly Button previousSlide;
    private readonly Button slideCounter;
    private readonly Button nextSlide;
    private readonly Button addSlide;
    private readonly CorkMenu slideMenu = new();
    private readonly Panel drawerClip = new() { Visible = false };
    private readonly Panel drawer = new() { BackColor = BoardStyle.Paper };
    private readonly ListBox boardList = new() { IntegralHeight = false, AccessibleName = "Corkboards" };
    private readonly ListBox savedNotes = new() { IntegralHeight = false, AccessibleName = "Saved notes" };
    private readonly Label notice;
    private readonly Label shortcutHint;
    private readonly Panel emptyState;
    private readonly CorkMenu canvasMenu = new() { ShowCheckMargin = false };
    private readonly CorkMenu threadMenu = new();
    private readonly ToolTip tips = new();
    private readonly System.Windows.Forms.Timer drawerMotion = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer slideMotion = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer cardMotion = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer threadMotion = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer noticeTimer = new() { Interval = 5000 };
    private readonly ThreadPointerFilter threadInput;
    private Guid? connectFrom;
    private Guid? movePinned;
    private Point? threadPointer;
    private NoteCard? threadTarget;
    private bool selectPin;
    private bool initialized;
    private bool hiding;
    private bool drawerOpening;
    private bool closing;
    private bool menuHovered;
    private Point canvasMenuPosition;
    private long slideStartedAt;
    private int slideStartOffset;
    private readonly List<NoteCard> appearingCards = [];
    private long cardStartedAt;
    private long threadStartedAt;
    private double threadReveal = 1;
    private bool revealThreadsAfterCards;
    private Guid? focusAfterSlide;
    private Rectangle? lastNormalBounds;
    private Rectangle? hiddenBounds;
    private bool hiddenMaximized;

    protected override int FrameDragWidth => 24;
    protected override int TopFrameDragWidth => 36;
    private int Frame => Pixels(FrameDragWidth);
    private int TopFrame => Pixels(TopFrameDragWidth);
    private int DrawerWidth => Pixels(280);

    protected override bool IsFrameCommand(Point point) =>
        (fab?.Bounds.Contains(point) ?? false) || (bell?.Bounds.Contains(point) ?? false) || (slideControls?.Bounds.Contains(point) ?? false)
        || (notice?.Visible == true && notice.Bounds.Contains(point));

    public BoardWindow(CorkboardApp app, CorkBoard board)
    {
        this.app = app;
        this.board = board;
        notes = board.Notes.ToDictionary(note => note.Id);
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
        ResizeEnd += (_, _) => RememberBounds(record: true);

        corkViewport = new Panel
        {
            AccessibleName = "Cork surface viewport", BackColor = BoardStyle.Cork,
            BackgroundImage = BoardStyle.CorkTile, BackgroundImageLayout = ImageLayout.Tile
        };
        canvas = new BoardSurface(DrawThreads);
        corkViewport.Controls.Add(canvas);
        slideMotion.Tick += (_, _) => AdvanceSlideTransition();
        cardMotion.Tick += (_, _) => AdvanceCardEntrance();
        threadMotion.Tick += (_, _) => AdvanceThreadReveal();
        threadInput = new ThreadPointerFilter(this);
        canvas.MouseClick += CanvasClick;
        canvasMenu.Items.Add("Add note", null, (_, _) => AddNote(canvasMenuPosition));
        canvasMenu.Items.Add("Add image…", null, (_, _) => ChooseImage(canvasMenuPosition));
        canvasMenu.Items.Add("Add table", null, (_, _) => AddTable(canvasMenuPosition));
        canvas.MouseMove += (_, e) =>
        {
            Cursor desired = selectPin || IsConnecting ? Cursors.Cross
                : board.Threads.Count > 0 && HitThread(e.Location) is not null ? Cursors.Hand : Cursors.Default;
            if (canvas.Cursor != desired) canvas.Cursor = desired;
        };
        canvas.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && !slideMotion.Enabled && HitThread(e.Location) is null && !selectPin && connectFrom is null)
                AddNote(e.Location);
        };
        emptyState = BuildEmptyState();
        canvas.Controls.Add(emptyState);

        notice = new Label
        {
            Visible = false, TextAlign = ContentAlignment.MiddleCenter, BackColor = BoardStyle.Paper,
            AutoEllipsis = true, Padding = new Padding(8, 0, 8, 0)
        };
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); notice.Visible = false; };
        shortcutHint = new Label
        {
            Text = $"Minimize to tray: {CorkboardApp.BoardShortcut.Replace("+", " + ")}",
            AccessibleName = "Minimize to tray and reopen shortcut",
            Font = new Font("Segoe UI", 8.5F), TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = BoardStyle.Ink, BackColor = Color.Transparent, AutoEllipsis = true
        };

        fab = BoardStyle.Button("", NavigationPaper, BoardStyle.Ink);
        fab.Font = new Font("Segoe UI Semibold", 9F);
        fab.TextAlign = ContentAlignment.MiddleLeft;
        fab.Padding = new Padding(26, 0, 4, 0);
        fab.AccessibleName = "Board actions";
        fab.Paint += PaintMenuGlyph;
        actions = BuildActions();
        slideControls = new Panel { AccessibleName = "Corkboard slides", BackColor = NavigationPaper };
        bell = BoardStyle.Button("", NavigationPaper, BoardStyle.Ink);
        bell.AccessibleName = "Reminders";
        tips.SetToolTip(bell, "Reminders");
        bell.Click += (_, _) => ReminderViews.ShowCenter(this, app.Reminders);
        bell.Paint += PaintBell;
        app.Reminders.Changed += RefreshBell;
        RefreshBell();
        previousSlide = SlideButton("‹", "Previous slide", () => StepSlide(-1));
        slideCounter = SlideButton("", "Manage slides", ShowSlideMenu);
        slideCounter.BackColor = ActiveSlidePaper;
        nextSlide = SlideButton("›", "Next slide", () => StepSlide(1));
        addSlide = SlideButton("+", "Add slide", AddSlide);
        slideControls.Controls.AddRange([previousSlide, slideCounter, nextSlide, addSlide]);
        fab.MouseEnter += (_, _) => { menuHovered = true; RefreshMenuButton(); };
        fab.MouseLeave += (_, _) => { menuHovered = false; RefreshMenuButton(); };
        fab.GotFocus += (_, _) => RefreshMenuButton();
        fab.LostFocus += (_, _) => RefreshMenuButton();
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

        Controls.AddRange([corkViewport, drawerClip, actions, fab, notice, slideControls, shortcutHint, bell]);
        bell.BringToFront();
        notice.BringToFront();
        fab.BringToFront();
        actions.BringToFront();
        drawerClip.BringToFront();
        LayoutBoard();
    }

    private Button SlideButton(string text, string description, Action action)
    {
        Button button = BoardStyle.Button(text, NavigationPaper, BoardStyle.Ink, compactText: true);
        button.AccessibleName = description;
        tips.SetToolTip(button, description);
        button.Click += (_, _) => action();
        return button;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (initialized) return;
        // Apply saved screen coordinates after WinForms has created and scaled the window.
        if (board.Bounds is Rectangle saved)
            Bounds = FitOnScreen(saved, MinimumSize, Screen.FromControl(this));
        initialized = true;
        LoadSlide();
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
        BoardStyle.DrawFrame(e.Graphics, ClientSize.Width, ClientSize.Height, Frame, TopFrame);
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

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible && !closing && !Disposing && canvas is not null)
        {
            FinishSlideTransition(animate: false);
            FinishCardEntrance();
            FinishThreadReveal();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        CommitTableEdits();
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
        if (HandleHistoryKey(keys, this)) return true;
        if (keys == (Keys.Control | Keys.N)) { AddNote(); return true; }
        if (keys == (Keys.Control | Keys.PageUp)) { StepSlide(-1); return true; }
        if (keys == (Keys.Control | Keys.PageDown)) { StepSlide(1); return true; }
        if (keys == (Keys.Control | Keys.Shift | Keys.B)) { actions.Visible = false; AnimateDrawer(!drawerClip.Visible); return true; }
        if (keys == Keys.Escape && (selectPin || connectFrom.HasValue || movePinned.HasValue || actions.Visible || drawerClip.Visible))
        {
            ClearSelectionMode();
            actions.Visible = false;
            AnimateDrawer(false);
            fab.Focus();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keys);
    }

    internal bool HandleHistoryKey(Keys keys, Control origin) => app.HandleHistoryKey(keys, origin);
    internal IDisposable EditNote(StickyNote note, string label) => app.BeginEdit(board, label, [note]);
    internal IDisposable EditContent(StickyNote note, string label, NoteRules.Settings? rule) =>
        rule?.Destination is Guid id && GetRuleDestination(note, id) is { } destination
            ? app.BeginEdit(board, label, [note, destination]) : EditNote(note, label);
    internal string? UndoLabel => app.UndoLabel;
    internal string? RedoLabel => app.RedoLabel;
    internal bool StepHistory(bool redo) => app.StepHistory(redo, this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            app.Reminders.Changed -= RefreshBell;
            if (connectFrom.HasValue) ClearSelectionMode();
            closing = true;
            foreach (FloatingNote popup in floating.Values.ToArray()) popup.Dispose();
            noticeTimer.Dispose();
            drawerMotion.Dispose();
            StopSlideMotion();
            slideMotion.Dispose();
            FinishCardEntrance();
            cardMotion.Dispose();
            threadMotion.Dispose();
            tips.Dispose();
            canvasMenu.Dispose();
            threadMenu.Dispose();
            slideMenu.Dispose();
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
            Bounds = FitOnScreen(previous, MinimumSize, Screen.FromControl(this));
            lastNormalBounds = Bounds;
            hiddenBounds = null;
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
        ShowSlide(note.SlideAddress);
        AnimateDrawer(false);
        if (slideMotion.Enabled) focusAfterSlide = note.Id;
        else FocusVisibleNote(note.Id);
    }

    internal void ScheduleReminder(StickyNote note, Form owner, NoteRules.Selection selection)
    {
        if (ReminderViews.PickTime(owner, selection.Text) is { } schedule)
        {
            app.Reminders.Add(board, note, selection.Text, schedule.Due, selection.Offset, schedule.Repeat);
            ShowNotice($"Reminder scheduled · {schedule.Due.LocalDateTime:g}");
        }
    }

    private void FocusVisibleNote(Guid noteId)
    {
        if (!cards.TryGetValue(noteId, out NoteCard? card)) return;
        card.BringToFront();
        card.FocusEditor();
    }

    private void StepSlide(int delta) => ShowSlide(new BoardSlide(board.ActiveSlide + delta));

    private void AddSlide()
    {
        if (board.HorizontalSlideCount == int.MaxValue) return;
        CommitTableEdits();
        using var edit = app.BeginEdit(board, "add slide");
        board.HorizontalSlideCount++;
        ShowSlide(new BoardSlide(board.HorizontalSlideCount - 1));
    }

    private void ShowSlide(BoardSlide slide)
    {
        if (!board.HasSlide(slide) || slide == board.CurrentSlide) return;
        if (!IsConnecting) ClearSelectionMode();
        threadTarget = null;
        threadPointer = null;
        focusAfterSlide = null;
        actions.Visible = false;
        StopSlideMotion();
        int direction = Math.Sign(slide.Index - board.ActiveSlide);
        board.CurrentSlide = slide;
        if (Visible && canvas.Width > 0 && AnimationsEnabled)
        {
            ClearSlideCards();
            emptyState.Visible = false;
            RefreshSlideControls();
            slideStartOffset = direction * Math.Min(canvas.Width / 5, Pixels(190));
            slideStartedAt = Stopwatch.GetTimestamp();
            canvas.Left = slideStartOffset;
            slideMotion.Start();
            canvas.Invalidate();
        }
        else LoadSlide();
        app.MarkDirty();
    }

    private void AdvanceSlideTransition()
    {
        const double durationMs = 235;
        double progress = Stopwatch.GetElapsedTime(slideStartedAt).TotalMilliseconds / durationMs;
        if (progress >= 1)
        {
            FinishSlideTransition();
            return;
        }
        double remaining = 1 - progress;
        canvas.Left = (int)Math.Round(slideStartOffset * remaining * remaining * remaining);
    }

    private void AdvanceCardEntrance()
    {
        const double durationMs = 165;
        double progress = Stopwatch.GetElapsedTime(cardStartedAt).TotalMilliseconds / durationMs;
        if (progress >= 1)
        {
            bool startThreads = revealThreadsAfterCards;
            FinishCardEntrance();
            if (startThreads)
            {
                threadStartedAt = Stopwatch.GetTimestamp();
                threadMotion.Start();
            }
            return;
        }
        double remaining = 1 - progress;
        double reveal = 1 - remaining * remaining * remaining;
        foreach (NoteCard card in appearingCards)
            if (!card.IsDisposed) card.RevealEntrance(reveal);
    }

    private void BeginCardEntrance(IEnumerable<NoteCard> items)
    {
        foreach (NoteCard card in items)
        {
            card.BeginEntrance();
            appearingCards.Add(card);
        }
        if (appearingCards.Count == 0) return;
        cardStartedAt = Stopwatch.GetTimestamp();
        cardMotion.Start();
    }

    private void FinishCardEntrance()
    {
        revealThreadsAfterCards = false;
        cardMotion.Stop();
        foreach (NoteCard card in appearingCards)
            if (!card.IsDisposed) card.FinishEntrance();
        appearingCards.Clear();
    }

    private void AdvanceThreadReveal()
    {
        const double durationMs = 245;
        double progress = Stopwatch.GetElapsedTime(threadStartedAt).TotalMilliseconds / durationMs;
        if (progress >= 1)
        {
            FinishThreadReveal();
            return;
        }
        threadReveal = progress * progress * (3 - 2 * progress);
        InvalidateThreadLines();
    }

    private void FinishThreadReveal()
    {
        revealThreadsAfterCards = false;
        threadMotion.Stop();
        if (threadReveal >= 1) return;
        threadReveal = 1;
        canvas.Invalidate();
    }

    private void InvalidateThreadLines()
    {
        foreach (NoteThread thread in board.Threads)
            if (TryEndpoints(thread, out Point start, out Point end))
                canvas.Invalidate(PreviewBounds(start, end));
    }

    private void StopSlideMotion()
    {
        slideMotion.Stop();
        canvas.Left = 0;
    }

    private void FinishSlideTransition(bool animate = true)
    {
        if (slideMotion.Enabled) LoadSlide(animate);
    }

    private void ClearSlideCards()
    {
        FinishCardEntrance();
        FinishThreadReveal();
        canvas.SuspendLayout();
        foreach (NoteCard card in cards.Values)
        {
            card.CommitTableEdit();
            card.Dispose();
            if (!floating.ContainsKey(card.Note.Id) && images.Remove(card.Note.Id, out Image? image)) image?.Dispose();
        }
        cards.Clear();
        canvas.ResumeLayout();
    }

    private void LoadSlide(bool animate = false)
    {
        StopSlideMotion();
        ClearSlideCards();
        bool reveal = animate && AnimationsEnabled;
        canvas.SuspendLayout();
        foreach (StickyNote note in board.Notes.Where(note => note.SlideAddress == board.CurrentSlide))
            AddCard(note, visible: !reveal);
        if (reveal && cards.Count > 0)
        {
            revealThreadsAfterCards = board.Threads.Any(thread => TryEndpoints(thread, out _, out _));
            if (revealThreadsAfterCards) threadReveal = 0;
            BeginCardEntrance(cards.Values);
            foreach (NoteCard card in cards.Values) card.Show();
        }
        canvas.ResumeLayout();
        UpdateEmptyState();
        RefreshSlideControls();
        if (focusAfterSlide is Guid id)
        {
            focusAfterSlide = null;
            FocusVisibleNote(id);
        }
        if (IsConnecting)
        {
            UpdateThreadPointer(canvas.PointToClient(Cursor.Position));
            ShowNotice("Click a card · Esc to cancel", true);
        }
        canvas.Invalidate();
    }

    private void RefreshSlideControls()
    {
        int count = board.HorizontalSlideCount;
        slideCounter.Text = $"{board.ActiveSlide + 1}/{count}";
        slideCounter.AccessibleDescription = $"{board.CurrentSlide.Name} of {count}";
        previousSlide.Enabled = board.ActiveSlide > 0;
        nextSlide.Enabled = board.ActiveSlide < count - 1;
    }

    private void ShowSlideMenu()
    {
        ClearMenu(slideMenu.Items);
        foreach (BoardSlide slide in board.Slides())
        {
            var item = new ToolStripMenuItem(slide.Name, null, (_, _) => ShowSlide(slide)) { Checked = slide == board.CurrentSlide };
            slideMenu.Items.Add(item);
        }
        slideMenu.Items.Add(new ToolStripSeparator());
        slideMenu.Items.Add("Add slide", null, (_, _) => AddSlide());
        BoardSlide current = board.CurrentSlide;
        slideMenu.Items.Add(new ToolStripMenuItem($"Delete {current.Name}…", null, (_, _) => DeleteSlide(current)) { Enabled = !IsLastSlide });
        slideMenu.Show(slideCounter, new Point(0, slideCounter.Height), ToolStripDropDownDirection.BelowLeft);
    }

    private bool IsLastSlide => board.HorizontalSlideCount == 1;

    private void DeleteSlide(BoardSlide slide)
    {
        if (!board.HasSlide(slide) || IsLastSlide) return;
        int count = board.Notes.Count(note => note.SlideAddress == slide);
        string question = count == 0 ? $"Delete empty slide {slide.Name}?"
            : $"Delete {slide.Name}, its {count} {(count == 1 ? "card" : "cards")}, and any connected threads?";
        if (CorkDialog.Confirm(this, "Delete slide", question, "Delete slide")) CommitSlideDeletion(slide);
    }

    private void CommitSlideDeletion(BoardSlide slide)
    {
        if (!board.HasSlide(slide) || IsLastSlide) return;
        CommitTableEdits();
        ClearSelectionMode();
        StickyNote[] previousNotes = board.Notes.ToArray();
        NoteThread[] previousThreads = board.Threads.ToArray();
        BoardSlide previousSlide = board.CurrentSlide;
        int count = board.HorizontalSlideCount;
        StickyNote[] removed = previousNotes.Where(note => note.SlideAddress == slide).ToArray();
        HashSet<Guid> removedIds = removed.Select(note => note.Id).ToHashSet();
        StickyNote[] shifted = previousNotes.Where(note => note.Slide > slide.Index).ToArray();
        var entry = DeletedItem.Capture(board, slide.Index, removed);
        DeletedItem[]? previousArchive = app.ArchiveDeletion(entry);
        using var edit = app.BeginEdit(board, "delete slide", replayArchive: redo => app.SetArchived(entry, redo));

        board.Notes.RemoveAll(note => removedIds.Contains(note.Id));
        board.Threads.RemoveAll(thread => removedIds.Contains(thread.From) || removedIds.Contains(thread.To));
        foreach (StickyNote note in shifted) note.Slide--;
        board.HorizontalSlideCount--;
        if (previousSlide == slide)
            board.CurrentSlide = new BoardSlide(Math.Min(slide.Index, count - 2));
        else if (previousSlide.Index > slide.Index)
            board.ActiveSlide--;

        app.MarkDirty();
        if (!app.Save())
        {
            app.RestoreArchive(previousArchive);
            foreach (StickyNote note in shifted) note.Slide++;
            board.HorizontalSlideCount = count;
            board.CurrentSlide = previousSlide;
            board.Notes.Clear();
            board.Notes.AddRange(previousNotes);
            board.Threads.Clear();
            board.Threads.AddRange(previousThreads);
            app.MarkDirty();
            return;
        }

        foreach (StickyNote note in removed) RemoveNoteView(note);
        LoadSlide();
        RefreshSavedNotes();
        ShowNotice("Slide deleted · Restore from Version history");
    }

    public void ListMoveSlides(StickyNote note, ToolStripMenuItem menu)
    {
        ClearMenu(menu.DropDownItems);
        foreach (BoardSlide target in board.Slides().Where(slide => slide != note.SlideAddress))
            menu.DropDownItems.Add(target.Name, null, (_, _) => BeginInvoke(() => MoveToSlide(note, target)));
        menu.Visible = menu.DropDownItems.Count > 0;
    }

    private void MoveToSlide(StickyNote note, BoardSlide target)
    {
        if (!notes.ContainsKey(note.Id) || !board.HasSlide(target) || note.SlideAddress == target) return;
        CommitTableEdits();
        using var edit = EditNote(note, "move card to slide");
        ClearSelectionMode();
        note.SlideAddress = target;
        if (board.CurrentSlide == target) LoadSlide();
        FocusNote(note);
        RefreshSavedNotes();
        app.MarkDirty();
    }

    public void AddNote(Point? click = null)
    {
        AddNewCard(new StickyNote(), click);
    }

    private void AddTable(Point? click = null) =>
        AddNewCard(new StickyNote { Table = new TableData(), CardWidth = 344, CardHeight = 246 }, click);

    private void ChooseImage(Point? click = null)
    {
        actions.Visible = false;
        using var picker = new OpenFileDialog
        {
            Title = "Add image to corkboard", CheckFileExists = true, RestoreDirectory = true,
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff"
        };
        if (picker.ShowDialog(this) == DialogResult.OK) AddImage(picker.FileName, click);
    }

    public void AddImage(string path, Point? click = null)
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
        AddNewCard(note, click);
        app.Save();
    }

    private void AddNewCard(StickyNote note, Point? click = null)
    {
        CommitTableEdits();
        using var edit = app.BeginEdit(board, note.ImageFile is not null ? "add image" : note.Table is not null ? "add table" : "add note");
        actions.Visible = false;
        ClearSelectionMode();
        FinishSlideTransition(animate: false);
        FinishCardEntrance();
        FinishThreadReveal();
        Size size = LimitCardResize(note, CardArea.Location, PreferredCardSize(note));
        Point? openPosition = click.HasValue ? null : NextOpenPosition(size);
        Point position = click is Point point ? new Point(point.X - Pixels(14), point.Y - Pixels(15))
            : openPosition ?? CardArea.Location + new Size(Pixels(18) * (cards.Count % 7), Pixels(18) * (cards.Count % 7));
        Point fitted = LimitCardMove(position, size);
        if (note.ImageFile is null) note.Color = board.Notes.Count % BoardStyle.Colors.Length;
        note.SlideAddress = board.CurrentSlide;
        board.Notes.Add(note);
        notes.Add(note.Id, note);
        MoveNote(note, fitted.X, fitted.Y);
        bool reveal = Visible && AnimationsEnabled;
        NoteCard card = AddCard(note, visible: !reveal);
        if (reveal)
        {
            BeginCardEntrance([card]);
            card.Show();
        }
        UpdateEmptyState();
        FocusNote(note);
        RefreshSavedNotes();
        app.MarkDirty();
        if (click is null && openPosition is null) ShowNotice("Need more room? Click + to add a slide.");
    }

    public bool CanMoveNote(StickyNote note) => !note.Pinned || movePinned == note.Id;
    public bool CanDragPin => !selectPin && !IsConnecting;

    public void TogglePinnedMove(StickyNote note)
    {
        bool enable = movePinned != note.Id;
        ClearSelectionMode();
        if (!enable || !note.Pinned || !cards.TryGetValue(note.Id, out NoteCard? card)) return;
        movePinned = note.Id;
        card.RefreshPin();
        ShowNotice("Drag the note by its handle · Esc to cancel", true);
    }

    public void FinishPinnedMove(StickyNote note)
    {
        if (movePinned == note.Id) ClearSelectionMode();
    }

    public void MoveNote(StickyNote note, int x, int y, bool byPin = false)
    {
        if (!byPin && !CanMoveNote(note)) return;
        int logicalX = (int)Math.Round(x * 96.0 / DeviceDpi);
        int logicalY = (int)Math.Round(y * 96.0 / DeviceDpi);
        if (note.X == logicalX && note.Y == logicalY) return;
        using var edit = EditNote(note, "move card");
        note.X = logicalX;
        note.Y = logicalY;
        app.MarkDirty();
    }

    public void RedrawCard(NoteCard card, Rectangle previous, Point previousPin)
    {
        if (card.Parent != canvas) return;
        Rectangle dirty = Rectangle.Union(previous, card.Bounds);
        if (previousPin != card.PinPointOnParent)
        {
            foreach (NoteThread thread in board.Threads)
            {
                Guid other = thread.From == card.Note.Id ? thread.To : thread.To == card.Note.Id ? thread.From : Guid.Empty;
                if (cards.TryGetValue(other, out NoteCard? endpoint))
                    dirty = Rectangle.Union(dirty, new Rectangle(endpoint.PinPointOnParent, new Size(1, 1)));
                else if (notes.TryGetValue(other, out StickyNote? remote))
                {
                    dirty = Rectangle.Union(dirty, PreviewBounds(SlideEdge(previousPin, remote.SlideAddress),
                        SlideEdge(card.PinPointOnParent, remote.SlideAddress)));
                }
            }
        }
        dirty.Inflate(Pixels(3), Pixels(3));
        canvas.Invalidate(dirty);
        canvas.Update();
    }

    public void SaveCardSize(StickyNote note, Size size)
    {
        int width = (int)Math.Round(size.Width * 96.0 / DeviceDpi);
        int height = (int)Math.Round(size.Height * 96.0 / DeviceDpi);
        if (note.CardWidth == width && note.CardHeight == height) return;
        using var edit = EditNote(note, "resize card");
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

    public Size LimitCardResize(StickyNote note, Point anchor, Size wanted)
    {
        Rectangle area = CardArea;
        int width = Math.Max(1, Math.Min(Pixels(NoteCard.Largest.Width), area.Right - anchor.X));
        int height = Math.Max(1, Math.Min(Pixels(NoteCard.Largest.Height), area.Bottom - anchor.Y));
        Size minimum = NoteCard.MinimumSizeFor(note);
        return new Size(Math.Clamp(wanted.Width, Math.Min(Pixels(minimum.Width), width), width),
            Math.Clamp(wanted.Height, Math.Min(Pixels(minimum.Height), height), height));
    }

    public void ChangeText(StickyNote note, string text, string rtf, NoteCard sender, bool runTextRules)
    {
        if (note.Text == text && note.FormattedText == rtf) return;
        bool typing = note.Text != text;
        bool hasRules = runTextRules && typing && (NoteRules.WholeRule(note, NoteRules.Trigger.TextAdded) is not null
            || NoteRules.WholeRule(note, NoteRules.Trigger.TextDeleted) is not null
            || note.TextSelectionRules?.Any(rule => rule.Trigger is NoteRules.Trigger.TextAdded or NoteRules.Trigger.TextDeleted) == true);
        using var edit = hasRules ? app.BeginEdit(board, "edit note text")
            : app.BeginEdit(board, typing ? "edit note text" : "format note text", [note], typing ? note : null);
        NoteRules.TextState? after = hasRules ? ApplyTextChangeRules(note, text, rtf) : null;
        ChecklistItemRule.TrackEdit(note, text);
        TextSelectionRule.TrackEdit(note, text);
        note.Text = text;
        note.FormattedText = rtf;
        if (after is { } result) result.Apply(note);
        RefreshNoteText(note, after.HasValue ? null : sender);
        app.MarkDirty();
    }

    private NoteRules.TextState? ApplyTextChangeRules(StickyNote source, string text, string rtf)
    {
        string before = source.Text;
        int limit = Math.Min(before.Length, text.Length);
        int first = 0;
        while (first < limit && before[first] == text[first]) first++;
        int oldEnd = before.Length, newEnd = text.Length;
        while (oldEnd > first && newEnd > first && before[oldEnd - 1] == text[newEnd - 1]) { oldEnd--; newEnd--; }

        if (oldEnd > first && !before.AsSpan(first, oldEnd - first).IsWhiteSpace()
            && Rule(NoteRules.Trigger.TextDeleted, oldEnd) is { Destination: Guid archive } deleted
            && GetRuleDestination(source, archive) is { } target)
        {
            using var oldEditor = new RichTextBox { DetectUrls = false };
            NoteCard.LoadNoteText(oldEditor, source);
            if (CorkboardApp.SelectReminderText(oldEditor, first, before[first..oldEnd]))
            {
                FormatRuleText(oldEditor, source, deleted);
                if (AppendSelection(target, oldEditor) is { } archived)
                {
                    archived.Apply(target);
                    RefreshNoteText(target);
                }
            }
        }
        if (newEnd <= first || text.AsSpan(first, newEnd - first).IsWhiteSpace()
            || Rule(NoteRules.Trigger.TextAdded, oldEnd) is not { } added || added == new NoteRules.Settings(null)) return null;
        using var editor = new RichTextBox { DetectUrls = false, Rtf = rtf };
        if (!CorkboardApp.SelectReminderText(editor, first, text[first..newEnd])) return null;
        FormatRuleText(editor, source, added);
        if (added.Destination is Guid id && GetRuleDestination(source, id) is { } destination
            && AppendSelection(destination, editor) is { } moved)
        {
            editor.SelectedText = "";
            moved.Apply(destination);
            RefreshNoteText(destination);
        }
        return NoteRules.TextState.Read(editor, source);

        NoteRules.Settings? Rule(NoteRules.Trigger trigger, int end) =>
            source.TextSelectionRules?.FirstOrDefault(rule => rule.Trigger == trigger && first >= rule.Start
                && end <= rule.Start + (long)rule.Length)?.Settings ?? NoteRules.WholeRule(source, trigger);
    }

    public void ChangeTimestampFormat(StickyNote note, NoteTimestampFormat format, bool subtle)
    {
        if (note.TimestampFormat == format && note.SubtleTimestamps == subtle) return;
        using var edit = EditNote(note, "change timestamp format");
        note.TimestampFormat = format;
        note.SubtleTimestamps = subtle;
        app.MarkDirty();
    }

    public void ChangeNoteReadingMode(StickyNote note, bool usePages)
    {
        if (note.UsePages == usePages) return;
        using var edit = EditNote(note, "change reading mode");
        note.UsePages = usePages;
        if (cards.TryGetValue(note.Id, out NoteCard? card)) card.RefreshReadingMode();
        if (floating.TryGetValue(note.Id, out FloatingNote? popup)) popup.Card.RefreshReadingMode();
        app.MarkDirty();
    }

    public void RefreshNoteText(StickyNote note, NoteCard? sender = null)
    {
        if (cards.TryGetValue(note.Id, out NoteCard? card) && card != sender) card.ReadText();
        if (floating.TryGetValue(note.Id, out FloatingNote? popup) && popup.Card != sender) popup.Card.ReadText();
        if (drawerClip.Visible) RefreshSavedNotes();
    }

    public void ChangeTable(StickyNote note, NoteCard sender)
    {
        if (cards.TryGetValue(note.Id, out NoteCard? card) && card != sender) card.RefreshTable();
        if (floating.TryGetValue(note.Id, out FloatingNote? popup) && popup.Card != sender) popup.Card.RefreshTable();
        app.MarkDirty();
    }

    public void CommitTableEdits()
    {
        foreach (NoteCard card in cards.Values) card.CommitTableEdit();
        foreach (FloatingNote popup in floating.Values) popup.Card.CommitTableEdit();
    }

    public void TrackChecklistToggle(StickyNote note, int line, bool completed)
    {
        if (ChecklistItemRule.MarkChecked(note, line, completed)) app.UpdateChecklistTimer();
    }

    public void EditNoteRules(StickyNote source, Form owner, NoteRules.Selection? selection = null, int? checklistLine = null)
    {
        NoteRules.Destination[] connected = board.Threads
            .Where(thread => thread.From == source.Id || thread.To == source.Id)
            .Select(thread => thread.From == source.Id ? thread.To : thread.From)
            .Distinct()
            .Select(id => GetRuleDestination(source, id))
            .OfType<StickyNote>()
            .Select(note => new NoteRules.Destination(note.Id, $"{ThreadCaption(note)} · {note.SlideAddress.Name}"))
            .ToArray();
        ChecklistItemRule? existingItem = checklistLine is int line ? ChecklistItemRule.At(source, line) : null;
        NoteRules.Settings? Existing(NoteRules.Trigger trigger) => selection is not { } span ? NoteRules.WholeRule(source, trigger)
            : trigger == NoteRules.Trigger.Check ? existingItem?.Settings
            : trigger == NoteRules.Trigger.Uncheck ? existingItem?.Uncheck : FindTextRule(source, span, trigger)?.Settings;
        if (!NoteRules.Show(owner, connected, source, selection?.Text, checklistLine.HasValue, Existing,
            out var rules)) return;
        using var edit = EditNote(source, "change note rules");
        NoteRules.Settings? choice = rules.GetValueOrDefault(NoteRules.Trigger.Check);
        NoteRules.Settings? uncheck = rules.GetValueOrDefault(NoteRules.Trigger.Uncheck);
        if (checklistLine is int item && (existingItem?.Settings != choice || existingItem?.Uncheck != uncheck))
        {
            string[] lines = source.Text.Split('\n');
            int marker = item < lines.Length ? NoteCard.FindChecklistMarker(lines[item], 0) : -1;
            DateTimeOffset? checkedAt = marker >= 0 && lines[item][marker] == '☑'
                ? ChecklistItemRule.At(source, item)?.CheckedAt ?? DateTimeOffset.Now : null;
            ChecklistItemRule.Update(source, item, choice, checkedAt, uncheck, changeUncheck: true);
        }
        if (selection is { } text)
        {
            foreach (var trigger in Enum.GetValues<NoteRules.Trigger>())
            {
                if (trigger is NoteRules.Trigger.Check or NoteRules.Trigger.Uncheck) continue;
                NoteRules.Settings? rule = rules.GetValueOrDefault(trigger);
                if (Existing(trigger) != rule) TextSelectionRule.Update(source, text.Offset, text.Text.Length, trigger, rule);
            }
        }
        if (selection is null)
        {
            choice ??= new NoteRules.Settings(null);
            source.CheckedItemDestination = choice.Destination;
            source.ChecklistResetDelayMinutes = choice.DelayMinutes;
            app.SetChecklistReset(source, choice.Daily);
            var checkActions = choice with { Destination = null, Daily = null, DelayMinutes = null };
            if (checkActions == new NoteRules.Settings(null)) rules.Remove(NoteRules.Trigger.Check);
            else rules[NoteRules.Trigger.Check] = checkActions;
            source.ContentRules = rules.Count == 0 ? null : rules;
        }
        app.UpdateChecklistTimer();
        app.MarkDirty();
    }

    private StickyNote? GetRuleDestination(StickyNote source, Guid id) =>
        id != source.Id && notes.TryGetValue(id, out StickyNote? target) && target.ImageFile is null && target.Table is null
        && board.Threads.Any(thread => (thread.From == source.Id && thread.To == id) || (thread.To == source.Id && thread.From == id))
        ? target : null;

    private static TextSelectionRule? FindTextRule(StickyNote source, NoteRules.Selection span, NoteRules.Trigger trigger) =>
        source.TextSelectionRules?.FirstOrDefault(rule => rule.Trigger == trigger && rule.Start == span.Offset && rule.Length == span.Text.Length);

    public static NoteRules.Settings? ResolveContentRule(StickyNote source, NoteRules.Trigger trigger,
        NoteRules.Selection active, string nativeText)
    {
        ChecklistItemRule? item = trigger is NoteRules.Trigger.Check or NoteRules.Trigger.Uncheck
            ? ChecklistItemRule.At(source, ChecklistItemRule.LineAt(nativeText, active.NativeStart)) : null;
        return (trigger switch
        {
            NoteRules.Trigger.Check => item?.Settings,
            NoteRules.Trigger.Uncheck => item?.Uncheck,
            _ => FindTextRule(source, active, trigger)?.Settings
        }) ?? NoteRules.WholeRule(source, trigger);
    }

    public bool TryApplyContentRule(StickyNote source, RichTextBox editor, NoteRules.Selection active,
        NoteRules.Settings rule, Action<Action> editText, Action<RichTextBox>? applyTrigger = null,
        bool removeItemLine = false)
    {
        if (rule == new NoteRules.Settings(null)) return false;
        StickyNote? target = rule.Destination is Guid id ? GetRuleDestination(source, id) : null;
        if (rule.Destination.HasValue && target is null) ShowNotice("Note rule paused · reconnect its destination");

        void Format(RichTextBox box)
        {
            box.Select(active.NativeStart, active.NativeEnd - active.NativeStart);
            applyTrigger?.Invoke(box);
            FormatRuleText(box, source, rule);
        }

        if (target is null)
        {
            if (!rule.Bold && !rule.Italic && !rule.Underline && !rule.Strike && !rule.Timestamp) return false;
            using var edit = EditNote(source, "apply note rule");
            editText(() => Format(editor));
            return true;
        }
        using var edited = new RichTextBox { DetectUrls = false, Rtf = editor.Rtf };
        Format(edited);
        if (AppendSelection(target, edited) is { } appended)
        {
            int? itemLine = null;
            if (removeItemLine)
            {
                itemLine = ChecklistItemRule.LineAt(edited.Text, active.NativeStart);
                RemoveChecklistLine(edited, active.NativeStart);
            }
            else edited.SelectedText = "";
            RecordRuleMove(source, target, NoteRules.TextState.Read(edited, source, itemLine), appended);
            return true;
        }
        ShowNotice("Couldn’t move selection · it stayed in this note");
        using var fallback = EditNote(source, "apply note rule");
        editText(() => Format(editor));
        return true;
    }

    internal static void FormatRuleText(RichTextBox box, StickyNote note, NoteRules.Settings rule)
    {
        if (rule.Strike) NoteCard.SetFormat(box, NoteCard.Strikeout, true);
        if (rule.Bold) NoteCard.SetFormat(box, NoteCard.Bold, true);
        if (rule.Italic) NoteCard.SetFormat(box, 2, true);
        if (rule.Underline) NoteCard.SetFormat(box, 4, true);
        if (!rule.Timestamp) return;
        int start = box.SelectionStart, end = start + box.SelectionLength;
        string stamp = $" · {NoteTimestamp.Format(DateTime.Now, note.TimestampFormat)}";
        Color paper = BoardStyle.Colors[Math.Clamp(note.Color, 0, BoardStyle.Colors.Length - 1)].Paper;
        box.Select(end, 0);
        NoteLinks.InsertPlainText(box, stamp, !note.SubtleTimestamps,
            note.SubtleTimestamps ? TextSizePrompt.TextInk(paper, 65) : null);
        box.Select(start, end - start + stamp.Length);
    }

    public bool TryMoveCheckedItem(StickyNote source, int marker, Guid id)
    {
        if (GetRuleDestination(source, id) is not StickyNote target)
        {
            ShowNotice("Note rule paused · reconnect its destination");
            return false;
        }
        using var from = new RichTextBox { DetectUrls = false };
        NoteCard.LoadNoteText(from, source);
        string original = from.Text;
        if (marker < 0 || marker >= original.Length || original[marker] != '☐' || !NoteCard.IsChecklistItem(original, marker))
            return false;
        int end = NoteCard.LineEnd(original, marker);
        NoteCard.SetChecklistState(from, marker, end, complete: true);
        from.Select(marker, end - marker);
        int itemLine = ChecklistItemRule.LineAt(original, marker);
        NoteRules.Settings sourceRule = ChecklistItemRule.GetSettings(source, itemLine);
        FormatRuleText(from, source, sourceRule);
        if (AppendSelection(target, from) is not NoteRules.TextState targetAfter)
        {
            ShowNotice("Couldn’t move item · it stayed in this note");
            return false;
        }
        RemoveChecklistLine(from, marker);
        int targetLine = ChecklistItemRule.LineAt(target.Text, target.Text.Length);
        if (target.Text.Length > 0 && !target.Text.EndsWith('\n')) targetLine++;
        var scheduled = new StickyNote { ChecklistItems = targetAfter.Checklist, DailyChecklistReset = target.DailyChecklistReset,
            ChecklistResetDelayMinutes = target.ChecklistResetDelayMinutes };
        if (sourceRule.Daily.HasValue || sourceRule.DelayMinutes.HasValue)
            ChecklistItemRule.Update(scheduled, targetLine, new NoteRules.Settings(null, sourceRule.Daily, sourceRule.DelayMinutes), DateTimeOffset.Now);
        else ChecklistItemRule.MarkChecked(scheduled, targetLine, true);
        RecordRuleMove(source, target, NoteRules.TextState.Read(from, source, itemLine), targetAfter with { Checklist = scheduled.ChecklistItems });
        return true;
    }

    private static void RemoveChecklistLine(RichTextBox editor, int marker)
    {
        string text = editor.Text;
        marker = NoteCard.LineStart(text, marker);
        int end = NoteCard.LineEnd(text, marker);
        int start = end == text.Length && marker > 0 ? marker - 1 : marker;
        int limit = end < text.Length ? end + 1 : end;
        editor.Select(start, limit - start);
        editor.SelectedText = "";
    }

    internal static NoteRules.TextState? AppendSelection(StickyNote target, RichTextBox source)
    {
        using var to = new RichTextBox { DetectUrls = false };
        NoteCard.LoadNoteText(to, target);
        string expectedItem = NoteParagraphs.RemoveDividerMarkers(NoteLinks.SelectedDisplayText(source));
        bool needsNewline = target.Text.Length > 0 && !target.Text.EndsWith('\n');
        to.Select(to.TextLength, 0);
        if (needsNewline) NoteLinks.InsertPlainText(to, "\n");
        to.SelectedRtf = source.SelectedRtf;
        NoteRules.TextState result = NoteRules.TextState.Read(to, target);
        string expected = target.Text + (needsNewline ? "\n" : "") + expectedItem;
        return expectedItem.Length > 0 && result.Text.TrimEnd('\n') == expected.TrimEnd('\n') ? result : null;
    }

    private void RecordRuleMove(StickyNote source, StickyNote target, NoteRules.TextState sourceAfter, NoteRules.TextState targetAfter)
    {
        using var edit = app.BeginEdit(board, "rule move", [source, target]);
        sourceAfter.Apply(source);
        targetAfter.Apply(target);
        RefreshNoteText(source);
        RefreshNoteText(target);
        app.UpdateChecklistTimer();
        app.MarkDirty();
        ShowNotice($"Moved to {ThreadCaption(target)} · Ctrl+Z to undo");
    }

    public void EditLabel(StickyNote note, Form owner)
    {
        string? label = NamePrompt.Show(owner, "Label note", "Category or label", note.Label, 45);
        if (label is null || label == note.Label) return;
        using var edit = EditNote(note, "edit card label");
        note.Label = label;
        if (cards.TryGetValue(note.Id, out NoteCard? card)) card.RefreshLabel();
        if (floating.TryGetValue(note.Id, out FloatingNote? popup))
        {
            popup.Card.RefreshLabel();
            popup.Text = string.IsNullOrWhiteSpace(label) ? (note.Table is null ? "Sticky note" : "Table") : label;
        }
        RefreshSavedNotes();
        app.MarkDirty();
    }

    public void ChangeColor(StickyNote note, int color)
    {
        if (note.Color == color) return;
        using var edit = EditNote(note, "change card color");
        note.Color = color;
        if (cards.TryGetValue(note.Id, out NoteCard? card)) card.ApplyColor();
        if (floating.TryGetValue(note.Id, out FloatingNote? popup)) popup.ApplyColor(color);
        if (board.Threads.Any(thread => thread.From == note.Id && thread.Color is null)) canvas.Invalidate();
        app.MarkDirty();
    }

    public void ChangePinColor(StickyNote note, int? color)
    {
        if (note.PinColor == color) return;
        using var edit = EditNote(note, "change pin color");
        note.PinColor = color;
        if (cards.TryGetValue(note.Id, out NoteCard? card)) card.RefreshPin();
        app.MarkDirty();
    }

    public void SetPinned(StickyNote note, bool pinned)
    {
        if (note.Pinned == pinned) return;
        using var edit = pinned ? EditNote(note, "pin card") : app.BeginEdit(board, "unpin card");
        note.Pinned = pinned;
        if (cards.TryGetValue(note.Id, out NoteCard? card)) card.RefreshPin();
        if (!pinned)
        {
            FinishPinnedMove(note);
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
                using var edit = app.BeginEdit(board, "add thread");
                FinishThreadReveal();
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
        ShowNotice("Click a card or change slides · Esc to cancel", true);
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
            if (!notes.TryGetValue(other, out StickyNote? destination)) continue;
            var item = new CorkSubmenuItem(ThreadCaption(destination).Replace("&", "&&"));
            AddThreadColors(item.DropDownItems, thread);
            menu.DropDownItems.Add(item);
        }
    }

    public void ListSlideConnections(StickyNote note, ToolStripMenuItem menu)
    {
        ClearMenu(menu.DropDownItems);
        foreach (NoteThread thread in board.Threads)
        {
            Guid other = thread.From == note.Id ? thread.To : thread.To == note.Id ? thread.From : Guid.Empty;
            if (notes.TryGetValue(other, out StickyNote? remote) && remote.SlideAddress != note.SlideAddress)
                menu.DropDownItems.Add(JumpToConnection(remote, fromPin: true));
        }
        menu.Visible = menu.DropDownItems.Count > 0;
    }

    private ToolStripMenuItem JumpToConnection(StickyNote remote, bool fromPin = false) =>
        new($"{remote.SlideAddress.Name} · {ThreadCaption(remote).Replace("&", "&&")}", null, (_, _) =>
        {
            if (fromPin) BeginInvoke(() => FocusNote(remote));
            else FocusNote(remote);
        });

    public void PopOut(StickyNote note)
    {
        using var edit = EditNote(note, "pop out card");
        if (!note.PoppedOut) { note.PoppedOut = true; app.MarkDirty(); }
        OpenFloating(note, true);
    }

    public void ToggleAlwaysOnTop(StickyNote note)
    {
        using var edit = EditNote(note, "change always on top");
        note.AlwaysOnTop = !note.PoppedOut || !note.AlwaysOnTop;
        PopOut(note);
        floating[note.Id].TopMost = note.AlwaysOnTop;
        app.MarkDirty();
    }

    public void SavePopupBounds(StickyNote note, Rectangle bounds, bool record)
    {
        if (closing || bounds == note.PopupBounds) return;
        using var edit = record ? EditNote(note, "move or resize floating card") : null;
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
        notes.Remove(note.Id);
        if (images.Remove(note.Id, out Image? image)) image?.Dispose();
        UpdateEmptyState();
        canvas.Invalidate();
    }

    internal void RefreshFromHistory(Guid[] affected, bool slides)
    {
        ClearSelectionMode();
        FinishCardEntrance();
        FinishThreadReveal();
        foreach (Guid id in affected)
        {
            StickyNote? current = board.Notes.Find(note => note.Id == id);
            if (current is null)
            {
                if (notes.TryGetValue(id, out StickyNote? removed)) RemoveNoteView(removed);
                continue;
            }
            notes[id] = current;
            if (floating.TryGetValue(id, out FloatingNote? popup))
            {
                if (!current.PoppedOut) popup.Close();
                else popup.RefreshFromHistory();
            }
            else if (current.PoppedOut) OpenFloating(current, false);
        }
        if (slides || slideMotion.Enabled) LoadSlide();
        else
        {
            foreach (Guid id in affected)
            {
                if (!notes.TryGetValue(id, out StickyNote? note) || note.SlideAddress != board.CurrentSlide) continue;
                if (!cards.TryGetValue(id, out NoteCard? card)) AddCard(note);
                else
                {
                    card.Bounds = PreferredCardBounds(note);
                    card.RefreshFromHistory();
                }
            }
            UpdateEmptyState();
            canvas.Invalidate();
        }
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

    public void MinimizeBoard()
    {
        if (!Visible || hiding) return;
        hiding = true;
        try
        {
            actions.Visible = false;
            CommitTableEdits();
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

    private BoardActionMenu BuildActions()
    {
        var panel = new BoardActionMenu();
        Add("Add note", "＋", () => AddNote());
        Add("Add image…", "▧", () => ChooseImage());
        Add("Add table", "▤", () => AddTable());
        Add("Add pin", "●", () => { actions.Visible = false; ClearSelectionMode(); selectPin = true; ShowNotice("Click a pin · Esc to cancel", true); });
        Add("Corkboards", "≡", () => { actions.Visible = false; AnimateDrawer(true); });
        Separator();
        Button undo = Add("Undo", "↶", () => { actions.Visible = false; StepHistory(false); });
        Button redo = Add("Redo", "↷", () => { actions.Visible = false; StepHistory(true); });
        Add("Version history…", "◷", () => { actions.Visible = false; app.ShowVersionHistory(this, board); });
        Separator();
        Add("Minimize to tray", "−", MinimizeBoard);
        Add("Maximize / restore", "□", () =>
        {
            actions.Visible = false;
            MaximizedBounds = Screen.FromControl(this).WorkingArea;
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        });
        BoardActionButton startup = Add("Start with Windows", "↥", () =>
        {
            try { app.ToggleStartup(); }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { ShowNotice("Windows couldn’t change the startup setting."); }
        });
        startup.ShowStartupState(false);
        startup.Click += (_, _) => UpdateStartupLabel();
        Add("Close board", "×", Close);
        Separator();
        Add("Exit Corkboard", "⏻", app.Exit);
        panel.VisibleChanged += (_, _) =>
        {
            if (!panel.Visible) return;
            UpdateStartupLabel();
            undo.Enabled = UndoLabel is not null;
            redo.Enabled = RedoLabel is not null;
            tips.SetToolTip(undo, UndoLabel is { } undoLabel ? $"Undo {undoLabel} (Ctrl+Z)" : "Ctrl+Z");
            tips.SetToolTip(redo, RedoLabel is { } redoLabel ? $"Redo {redoLabel} (Ctrl+Y)" : "Ctrl+Y");
        };
        return panel;

        BoardActionButton Add(string text, string glyph, Action action)
        {
            var button = new BoardActionButton(text, glyph);
            button.Click += (_, _) => action();
            panel.Controls.Add(button);
            return button;
        }

        void Separator() => panel.Controls.Add(new Panel { BackColor = BoardStyle.PanelTint, TabStop = false });

        void UpdateStartupLabel()
        {
            try { startup.ShowStartupState(Startup.IsEnabled()); }
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
        if (AnimationsEnabled) drawerMotion.Start();
        else SetDrawerWidth(open ? DrawerWidth : 0);
    }

    private void SlideDrawer()
    {
        int next = Math.Clamp(drawerClip.Width + (drawerOpening ? Pixels(40) : -Pixels(40)), 0, DrawerWidth);
        SetDrawerWidth(next);
    }

    private void SetDrawerWidth(int next)
    {
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
        Rectangle surface = new(inset, TopFrame, Math.Max(0, ClientSize.Width - inset * 2),
            Math.Max(0, ClientSize.Height - TopFrame - inset));
        if (corkViewport.Bounds != surface)
        {
            FinishSlideTransition(animate: false);
            corkViewport.Bounds = surface;
            canvas.Bounds = corkViewport.ClientRectangle;
        }
        if (WindowState != FormWindowState.Minimized && canvas.ClientSize.Width > 0 && canvas.ClientSize.Height > 0)
            foreach (NoteCard card in cards.Values) card.Bounds = PreferredCardBounds(card.Note);
        emptyState.Location = new Point(Math.Max(8, (canvas.ClientSize.Width - emptyState.Width) / 2), Math.Max(8, (canvas.ClientSize.Height - emptyState.Height) / 2));
        fab.Location = new Point(inset + Pixels(6), Pixels(8));
        int navigationWidth = Pixels(132);
        slideControls.Bounds = new Rectangle(corkViewport.Right - Pixels(6) - navigationWidth, Pixels(8), navigationWidth, Pixels(22));
        bell.Bounds = new Rectangle(slideControls.Left - Pixels(34), slideControls.Top, Pixels(28), slideControls.Height);
        BoardStyle.RoundCorners(bell, Pixels(9));
        BoardStyle.RoundCorners(slideControls, Pixels(11));
        previousSlide.Bounds = new Rectangle(Pixels(4), Pixels(1), Pixels(22), Pixels(20));
        slideCounter.Bounds = new Rectangle(Pixels(26), Pixels(1), Pixels(56), Pixels(20));
        nextSlide.Bounds = new Rectangle(Pixels(82), Pixels(1), Pixels(22), Pixels(20));
        addSlide.Bounds = new Rectangle(Pixels(104), Pixels(1), Pixels(24), Pixels(20));
        RefreshMenuButton();
        int menuTop = corkViewport.Top + Pixels(4);
        actions.Fit(Rectangle.FromLTRB(fab.Left, menuTop,
            Math.Max(fab.Left, corkViewport.Right - Pixels(6)), Math.Max(menuTop, corkViewport.Bottom - Pixels(6))));
        drawerClip.Location = new Point(fab.Left, corkViewport.Top + Pixels(4));
        drawerClip.Height = Math.Max(0, corkViewport.Bottom - drawerClip.Top - Pixels(9));
        drawer.Size = new Size(DrawerWidth, drawerClip.Height);
        BoardStyle.RoundCorners(drawer, Pixels(8));
        int hintWidth = TextRenderer.MeasureText(shortcutHint.Text, shortcutHint.Font).Width;
        shortcutHint.Bounds = new Rectangle(inset + Pixels(8), corkViewport.Bottom + Pixels(2),
            Math.Min(hintWidth, Math.Max(0, canvas.Width - Pixels(16))), Pixels(20));
        LayoutNotice();

    }

    private void LayoutNotice()
    {
        int right = corkViewport.Right - Pixels(8);
        int room = Math.Max(0, right - shortcutHint.Right - Pixels(8));
        int measured = TextRenderer.MeasureText(notice.Text, notice.Font).Width + Pixels(24);
        int width = Math.Min(room, measured);
        int height = Pixels(20);
        notice.Bounds = new Rectangle(right - width, corkViewport.Bottom + Pixels(2), width, height);
        BoardStyle.RoundCorners(notice, Pixels(10));
    }

    private void RefreshMenuButton()
    {
        bool expanded = menuHovered || fab.Focused;
        Size size = new(Pixels(expanded ? 105 : 22), Pixels(22));
        if (fab.Size != size)
        {
            fab.Size = size;
            BoardStyle.RoundCorners(fab, Pixels(11));
        }
        fab.Text = expanded ? (actions.Visible || drawerClip.Visible ? "Close menu" : "Board menu") : "";
        fab.Invalidate();
    }

    private void PaintMenuGlyph(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int centerX = Pixels(11);
        int centerY = fab.Height / 2;
        int radius = Pixels(8);
        using var accent = new SolidBrush(BoardStyle.Brick);
        using var line = new Pen(Color.White, Pixels(1)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.FillEllipse(accent, centerX - radius, centerY - radius, radius * 2, radius * 2);
        int stroke = Pixels(3);
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

    private void RefreshBell()
    {
        int count = app.Reminders.Unfinished;
        bell.AccessibleDescription = count == 0 ? "Open notifications and scheduled reminders" : $"{count} unfinished reminders";
        bell.Invalidate();
    }

    private void PaintBell(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TranslateTransform(bell.Width / 2f, bell.Height / 2f);
        float scale = DeviceDpi / 96f;
        e.Graphics.ScaleTransform(scale, scale);
        using var line = new Pen(BoardStyle.Ink, 1.2f);
        e.Graphics.DrawArc(line, -4, -6, 8, 8, 180, 180);
        e.Graphics.DrawLines(line, new Point[] { new(-4, -2), new(-5, 4), new(5, 4), new(4, -2) });
        e.Graphics.DrawArc(line, -1.5f, 4, 3, 2, 0, 180);
        if (app.Reminders.Unfinished > 0)
        {
            using var dot = new SolidBrush(BoardStyle.Brick);
            e.Graphics.FillEllipse(dot, 4, -8, 5, 5);
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
        emptyState.Visible = cards.Count == 0;
        if (emptyState.Visible) LayoutBoard();
    }

    private NoteCard AddCard(StickyNote note, bool visible = true)
    {
        var card = new NoteCard(this, note, floating: false, image: ReadImage(note)) { Bounds = PreferredCardBounds(note), Visible = visible };
        cards.Add(note.Id, card);
        canvas.Controls.Add(card);
        card.BringToFront();
        return card;
    }

    private Point? NextOpenPosition(Size size)
    {
        Rectangle area = CardArea;
        int gap = Pixels(14);
        for (int y = area.Top; y <= area.Bottom - size.Height; y += size.Height + gap)
            for (int x = area.Left; x <= area.Right - size.Width; x += size.Width + gap)
            {
                var slot = new Rectangle(new Point(x, y), size);
                if (!cards.Values.Any(card => card.Bounds.IntersectsWith(slot))) return slot.Location;
            }
        return null;
    }

    private Rectangle CardArea => canvas.ClientRectangle;

    private Size PreferredCardSize(StickyNote note)
    {
        Size minimum = NoteCard.MinimumSizeFor(note);
        return new Size(Pixels(ClampDimension(note.CardWidth, NoteCard.InitialCardSize.Width, minimum.Width, NoteCard.Largest.Width)),
            Pixels(ClampDimension(note.CardHeight, NoteCard.InitialCardSize.Height, minimum.Height, NoteCard.Largest.Height)));
    }

    private Rectangle PreferredCardBounds(StickyNote note)
    {
        var position = new Point(Pixels(Math.Clamp(note.X, 0, 16384)), Pixels(Math.Clamp(note.Y, 0, 16384)));
        return FitPreferredCard(note, position, PreferredCardSize(note));
    }

    private Image? ReadImage(StickyNote note)
    {
        if (note.ImageFile is null) return null;
        if (!images.TryGetValue(note.Id, out Image? image)) images[note.Id] = image = app.Images.Load(note.ImageFile);
        return image;
    }

    private Rectangle FitPreferredCard(StickyNote note, Point location, Size wanted)
    {
        Rectangle area = CardArea;
        Size requestedMinimum = NoteCard.MinimumSizeFor(note);
        var minimum = new Size(Math.Min(Pixels(requestedMinimum.Width), area.Width), Math.Min(Pixels(requestedMinimum.Height), area.Height));
        Point anchor = LimitCardMove(location, minimum);
        return new Rectangle(anchor, LimitCardResize(note, anchor, wanted));
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
                if (closing || !board.Notes.Contains(note) || !note.PoppedOut) return;
                using var edit = EditNote(note, "return card to board");
                if (!cards.ContainsKey(note.Id) && images.Remove(note.Id, out Image? image)) image?.Dispose();
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
        if (slideMotion.Enabled) return;
        if (board.Threads.Count == 0 && threadPointer is null) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.Black, Pixels(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var marker = new SolidBrush(Color.Black);
        foreach (NoteThread thread in board.Threads)
        {
            if (threadReveal <= 0 || !TryEndpoints(thread, out Point start, out Point end)) continue;
            pen.Color = ResolveThreadColor(thread);
            graphics.DrawLine(pen, start, RevealedEndpoint(start, end));
            if (!cards.ContainsKey(thread.From)) DrawEdgeMarker(start);
            if (threadReveal >= 1 && !cards.ContainsKey(thread.To)) DrawEdgeMarker(end);
        }
        if (threadPointer is Point pointer && connectFrom is Guid source && notes.TryGetValue(source, out StickyNote? note))
        {
            pen.Color = BoardStyle.NoteAccent(note.Color);
            pen.DashStyle = DashStyle.Dash;
            graphics.DrawLine(pen, PreviewAnchor(note, pointer), pointer);
        }

        void DrawEdgeMarker(Point point)
        {
            int radius = Pixels(4);
            marker.Color = pen.Color;
            graphics.FillEllipse(marker, point.X - radius, point.Y - radius, radius * 2, radius * 2);
        }
    }

    private void CanvasClick(object? sender, MouseEventArgs e)
    {
        actions.Visible = false;
        if (slideMotion.Enabled || selectPin || connectFrom.HasValue) return;
        if (HitThread(e.Location) is not NoteThread thread)
        {
            if (e.Button == MouseButtons.Right && canvas.ClientRectangle.Contains(e.Location)
                && canvas.GetChildAtPoint(e.Location, GetChildAtPointSkip.Invisible) is null)
            {
                canvasMenuPosition = e.Location;
                canvasMenu.Show(canvas, e.Location);
            }
            return;
        }
        ClearMenu(threadMenu.Items);
        threadMenu.SetSurface(ResolveThreadColor(thread));
        StickyNote source = notes[thread.From];
        StickyNote destination = notes[thread.To];
        string from = ThreadCaption(source);
        string to = ThreadCaption(destination);
        threadMenu.Items.Add(new ToolStripLabel($"{from}  ↔  {to}"));
        if (source.SlideAddress != destination.SlideAddress)
            threadMenu.Items.Add(JumpToConnection(source.SlideAddress == board.CurrentSlide ? destination : source));
        threadMenu.Items.Add(new ToolStripSeparator());
        AddThreadColors(threadMenu.Items, thread);
        threadMenu.Items.Add(new ToolStripSeparator());
        threadMenu.Items.Add("Remove thread", null, (_, _) =>
        {
            using var edit = app.BeginEdit(board, "remove thread");
            board.Threads.Remove(thread);
            canvas.Invalidate();
            app.MarkDirty();
        });
        threadMenu.Show(canvas, e.Location);
    }

    private void AddThreadColors(ToolStripItemCollection items, NoteThread thread)
    {
        var match = new ToolStripMenuItem("Match starting note") { Checked = thread.Color is null };
        match.Click += (_, _) => SetThreadColor(thread, null);
        items.Add(match);
        for (int i = 0; i < BoardStyle.ThreadColors.Length; i++)
        {
            int color = i;
            var item = new ToolStripMenuItem(BoardStyle.ThreadColors[i].Name) { Checked = thread.Color == i, ForeColor = BoardStyle.ThreadColors[i].Color };
            if (i == 4) item.ForeColor = BoardStyle.Ink;
            item.Click += (_, _) => SetThreadColor(thread, color);
            items.Add(item);
        }
    }

    private void SetThreadColor(NoteThread thread, int? color)
    {
        if (thread.Color == color) return;
        using var edit = app.BeginEdit(board, "change thread color");
        thread.Color = color;
        canvas.Invalidate();
        app.MarkDirty();
    }

    private Color ResolveThreadColor(NoteThread thread)
    {
        if (thread.Color is int color)
            return BoardStyle.ThreadColors[Math.Clamp(color, 0, BoardStyle.ThreadColors.Length - 1)].Color;
        return notes.TryGetValue(thread.From, out StickyNote? from)
            ? BoardStyle.NoteAccent(from.Color) : BoardStyle.ThreadColors[0].Color;
    }

    private static string ThreadCaption(StickyNote note)
    {
        string caption = new NoteListItem(note).Caption;
        return caption.Length > 38 ? caption[..37] + "…" : caption;
    }

    private static void ClearMenu(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items.Cast<ToolStripItem>().ToArray()) item.Dispose();
    }

    private NoteThread? HitThread(Point point) => board.Threads.LastOrDefault(thread =>
    {
        if (threadReveal <= 0 || !TryEndpoints(thread, out Point start, out Point end)) return false;
        end = RevealedEndpoint(start, end);
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

    private Point RevealedEndpoint(Point start, Point end) => new(
        start.X + (int)Math.Round((end.X - start.X) * threadReveal),
        start.Y + (int)Math.Round((end.Y - start.Y) * threadReveal));

    private bool TryEndpoints(NoteThread thread, out Point start, out Point end)
    {
        start = end = Point.Empty;
        if (!notes.TryGetValue(thread.From, out StickyNote? source) || !notes.TryGetValue(thread.To, out StickyNote? destination)) return false;
        cards.TryGetValue(thread.From, out NoteCard? from);
        cards.TryGetValue(thread.To, out NoteCard? to);
        if (from is null && to is null) return false;
        start = from?.PinPointOnParent ?? SlideEdge(to!.PinPointOnParent, source.SlideAddress);
        end = to?.PinPointOnParent ?? SlideEdge(start, destination.SlideAddress);
        return true;
    }

    private Point SlideEdge(Point aligned, BoardSlide slide)
    {
        int margin = Pixels(6);
        return new Point(slide.Index > board.ActiveSlide ? canvas.Width - margin : margin, aligned.Y);
    }

    private Point PreviewAnchor(StickyNote source, Point pointer) =>
        cards.TryGetValue(source.Id, out NoteCard? from) ? from.PinPointOnParent : SlideEdge(pointer, source.SlideAddress);

    private void ClearSelectionMode()
    {
        if (selectPin || connectFrom.HasValue || movePinned.HasValue) notice.Visible = false;
        if (movePinned is Guid moving)
        {
            movePinned = null;
            if (cards.TryGetValue(moving, out NoteCard? card)) card.RefreshPin();
        }
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
        if (slideMotion.Enabled || connectFrom is not Guid source || !notes.TryGetValue(source, out StickyNote? from)) return;
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
        if (threadPointer is Point previous) canvas.Invalidate(PreviewBounds(PreviewAnchor(from, previous), previous));
        threadPointer = next;
        canvas.Invalidate(PreviewBounds(PreviewAnchor(from, next), next));
    }

    private NoteCard? TargetAt(Point point)
    {
        if (!canvas.ClientRectangle.Contains(point)) return null;
        return canvas.GetChildAtPoint(point, GetChildAtPointSkip.Invisible) is NoteCard card && card.Note.Id != connectFrom ? card : null;
    }

    private Rectangle PreviewBounds(Point from, Point to) => Rectangle.Inflate(
        Rectangle.FromLTRB(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Max(from.X, to.X) + 1, Math.Max(from.Y, to.Y) + 1),
        Pixels(4), Pixels(4));

    internal void RememberBounds(bool record = false)
    {
        TrackNormalBounds();
        if (!initialized || (hiddenBounds ?? lastNormalBounds) is not Rectangle wanted || board.Bounds == wanted) return;
        Rectangle before = board.Bounds ?? Bounds;
        board.Bounds = wanted;
        app.MarkDirty();
        if (record && before != wanted)
            app.RecordEdit("move or resize board", () => app.RestoreBoardBounds(board, before), () => app.RestoreBoardBounds(board, wanted));
    }

    internal void RestoreSavedBounds(Rectangle bounds)
    {
        Rectangle fitted = FitOnScreen(bounds, MinimumSize, Screen.FromRectangle(bounds));
        if (hiddenBounds.HasValue) hiddenBounds = fitted;
        else if (WindowState == FormWindowState.Normal) Bounds = fitted;
        lastNormalBounds = fitted;
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
                if (control != window.canvas && !window.canvas.Contains(control)) return false;
                if (window.slideMotion.Enabled) return false;
                Cursor.Current = window.threadTarget is null ? Cursors.Cross : Cursors.Hand;
                return true;
            }
            if (control != window.canvas && !window.canvas.Contains(control))
            {
                if (window.slideControls.Contains(control)) return false;
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
            : Note.Table is not null ? "Table" : Note.ImageFile is not null ? "Image"
            : Note.Text.Split(['\r', '\n'], 2)[0].Trim() is { Length: > 0 } first ? first : "Untitled note";
    }
}
