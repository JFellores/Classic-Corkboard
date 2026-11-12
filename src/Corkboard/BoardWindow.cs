namespace Corkboard;

internal sealed class BoardWindow : Form
{
    private readonly NoteStore store;
    private readonly BoardData data;
    private readonly Panel canvas;
    private readonly Panel emptyState;
    private readonly Label count;
    private readonly Label saveStatus;
    private readonly Bitmap cork = BoardStyle.CreateCorkTile();
    private readonly Icon appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
    private readonly Dictionary<Guid, NoteCard> cards = [];
    private readonly Dictionary<Guid, FloatingNote> floating = [];
    private readonly System.Windows.Forms.Timer autosave = new() { Interval = 480 };
    private readonly TrayIcon tray;
    private readonly ContextMenuStrip trayMenu = new();
    private readonly ContextMenuStrip options = new();
    private readonly ToolStripMenuItem startWithWindows = new("Start with Windows");
    private bool dirty;
    private bool exiting;
    private bool initialized;
    private bool hidingToTray;

    public BoardWindow(NoteStore store, BoardData data, string? warning)
    {
        this.store = store;
        this.data = data;
        Text = "Corkboard";
        Icon = appIcon;
        Font = new Font("Segoe UI", 10F);
        ForeColor = BoardStyle.Ink;
        BackColor = BoardStyle.Paper;
        ClientSize = new Size(1000, 640);
        MinimumSize = new Size(450, 340);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 66, Padding = new Padding(20, 13, 18, 13) };
        var title = new Label
        {
            Text = "Corkboard", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Georgia", 22F, FontStyle.Bold), AutoEllipsis = true
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, Width = 212, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = new Padding(0, 3, 0, 0)
        };
        Button more = BoardStyle.Button("Options ▾", Color.FromArgb(237, 227, 210), BoardStyle.Ink);
        more.Size = new Size(86, 34);
        more.Click += (_, _) => options.Show(more, new Point(more.Width, more.Height), ToolStripDropDownDirection.BelowLeft);
        Button add = BoardStyle.Button("+  New note", BoardStyle.Brick, Color.White);
        add.Size = new Size(108, 34);
        add.Margin = new Padding(0, 0, 8, 0);
        add.Click += (_, _) => AddNote();
        actions.Controls.AddRange([more, add]);
        toolbar.Controls.Add(title);
        toolbar.Controls.Add(actions);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 31, Padding = new Padding(16, 0, 16, 0) };
        count = new Label { Dock = DockStyle.Left, Width = 120, TextAlign = ContentAlignment.MiddleLeft, ForeColor = BoardStyle.Muted };
        saveStatus = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = BoardStyle.Muted,
            AutoEllipsis = true, Text = warning ?? "Stored on this PC"
        };
        footer.Controls.Add(saveStatus);
        footer.Controls.Add(count);

        canvas = new Panel
        {
            Name = "Board", AccessibleName = "Corkboard surface", Dock = DockStyle.Fill,
            AutoScroll = true, BackColor = Color.FromArgb(174, 125, 86),
            BackgroundImage = cork, BackgroundImageLayout = ImageLayout.Tile
        };
        canvas.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) AddNote(e.Location); };
        emptyState = CreateEmptyState();
        canvas.Controls.Add(emptyState);
        canvas.Resize += (_, _) => CenterEmptyState();
        Controls.Add(canvas);
        Controls.Add(footer);
        Controls.Add(toolbar);

        startWithWindows.Click += (_, _) => ChangeStartup();
        options.Items.AddRange([startWithWindows, new ToolStripMenuItem("Minimize to tray", null, (_, _) => HideBoard()),
            new ToolStripSeparator(), new ToolStripMenuItem("Exit Corkboard", null, (_, _) => ExitApp())]);
        options.Opening += (_, _) => RefreshStartup();

        trayMenu.Items.AddRange([new ToolStripMenuItem("Open Corkboard", null, (_, _) => ShowBoard()),
            new ToolStripMenuItem("New note", null, (_, _) => { ShowBoard(); AddNote(); }),
            new ToolStripSeparator(), new ToolStripMenuItem("Exit Corkboard", null, (_, _) => ExitApp())]);
        tray = new TrayIcon(appIcon, trayMenu, ShowBoard, RestoreIfTrayUnavailable);
        autosave.Tick += (_, _) => SaveNotes();
        RefreshCount();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (initialized) return;
        initialized = true;
        tray.EnsureRegistered();
        canvas.SuspendLayout();
        foreach (StickyNote note in data.Notes) AddCard(note);
        canvas.ResumeLayout();
        foreach (StickyNote note in data.Notes.Where(note => note.PoppedOut)) OpenFloatingNote(note, activate: false);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && IsHandleCreated && !hidingToTray)
            BeginInvoke(() => { if (WindowState == FormWindowState.Minimized) HideBoard(); });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideBoard();
        }
        else
        {
            exiting = true;
            SaveNotes();
            tray.Dispose();
        }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData != (Keys.Control | Keys.N)) return base.ProcessCmdKey(ref msg, keyData);
        AddNote();
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (FloatingNote window in floating.Values.ToArray()) window.Dispose();
            tray.Dispose();
            trayMenu.Dispose();
            options.Dispose();
            autosave.Dispose();
            cork.Dispose();
            appIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    public void ShowBoard()
    {
        if (exiting) return;
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    public void MoveNote(StickyNote note, int x, int y)
    {
        int logicalX = (int)Math.Round(x * 96.0 / DeviceDpi);
        int logicalY = (int)Math.Round(y * 96.0 / DeviceDpi);
        if (note.X == logicalX && note.Y == logicalY) return;
        note.X = logicalX;
        note.Y = logicalY;
        MarkDirty();
    }

    public void ChangeText(StickyNote note, string text, NoteCard sender)
    {
        if (note.Text == text) return;
        note.Text = text;
        if (cards.TryGetValue(note.Id, out NoteCard? card) && card != sender) card.SetText(text);
        if (floating.TryGetValue(note.Id, out FloatingNote? popup) && popup.Card != sender) popup.Card.SetText(text);
        MarkDirty();
    }

    public void ChangeColor(StickyNote note, int color)
    {
        if (note.Color == color) return;
        note.Color = color;
        cards[note.Id].ApplyColor();
        if (floating.TryGetValue(note.Id, out FloatingNote? popup)) popup.Card.ApplyColor();
        MarkDirty();
    }

    public void PopOut(StickyNote note)
    {
        if (!note.PoppedOut)
        {
            note.PoppedOut = true;
            MarkDirty();
        }
        OpenFloatingNote(note, activate: true);
    }

    public void TogglePin(StickyNote note)
    {
        note.AlwaysOnTop = !note.PoppedOut || !note.AlwaysOnTop;
        PopOut(note);
        floating[note.Id].TopMost = note.AlwaysOnTop;
        MarkDirty();
    }

    public void ReturnToBoard(StickyNote note)
    {
        if (floating.TryGetValue(note.Id, out FloatingNote? popup)) popup.Close();
        ShowBoard();
        canvas.ScrollControlIntoView(cards[note.Id]);
        cards[note.Id].BringToFront();
        cards[note.Id].FocusEditor();
    }

    public void DeleteNote(StickyNote note, Form parent)
    {
        if (MessageBox.Show(parent, "Delete this note?", "Corkboard", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (floating.TryGetValue(note.Id, out FloatingNote? popup)) popup.Close();
        cards.Remove(note.Id, out NoteCard? card);
        card?.Dispose();
        data.Notes.Remove(note);
        RefreshCount();
        MarkDirty();
    }

    public void SavePopupBounds(StickyNote note, Rectangle bounds)
    {
        if (exiting || bounds == note.PopupBounds) return;
        note.PopupBounds = bounds;
        MarkDirty();
    }

    private void OpenFloatingNote(StickyNote note, bool activate)
    {
        if (!floating.TryGetValue(note.Id, out FloatingNote? window))
        {
            window = new FloatingNote(this, note, appIcon, activate);
            floating.Add(note.Id, window);
            window.FormClosed += (_, _) =>
            {
                floating.Remove(note.Id);
                if (exiting) return;
                note.PoppedOut = false;
                MarkDirty();
            };
            window.Show();
        }
        if (!activate) return;
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.Activate();
        window.Card.FocusEditor();
    }

    private void AddNote(Point? click = null)
    {
        Point scroll = canvas.AutoScrollPosition;
        Point position = click is Point point
            ? new Point(Math.Max(8, point.X - scroll.X - 16), Math.Max(8, point.Y - scroll.Y - 15))
            : NextOpenPosition();
        var note = new StickyNote { Color = data.Notes.Count % BoardStyle.Colors.Length };
        data.Notes.Add(note);
        MoveNote(note, position.X, position.Y);
        NoteCard card = AddCard(note);
        RefreshCount();
        canvas.ScrollControlIntoView(card);
        card.FocusEditor();
        MarkDirty();
    }

    private NoteCard AddCard(StickyNote note)
    {
        Point scroll = canvas.AutoScrollPosition;
        var card = new NoteCard(this, note, floating: false)
        {
            Size = new Size(Pixels(231), Pixels(172)),
            Location = new Point(Pixels(Math.Clamp(note.X, 8, 12000)) + scroll.X, Pixels(Math.Clamp(note.Y, 8, 12000)) + scroll.Y)
        };
        cards.Add(note.Id, card);
        canvas.Controls.Add(card);
        card.BringToFront();
        return card;
    }

    private Point NextOpenPosition()
    {
        int inset = Pixels(24);
        int stepX = Pixels(251);
        int stepY = Pixels(192);
        int columns = Math.Max(1, (canvas.ClientSize.Width - inset + Pixels(20)) / stepX);
        Point scroll = canvas.AutoScrollPosition;
        for (int row = 0; ; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                var slot = new Rectangle(inset + column * stepX, inset + row * stepY, Pixels(231), Pixels(172));
                if (!cards.Values.Any(card => new Rectangle(card.Left - scroll.X, card.Top - scroll.Y, card.Width, card.Height)
                    .IntersectsWith(slot))) return slot.Location;
            }
        }
    }

    private int Pixels(int logical) => LogicalToDeviceUnits(logical);

    private Panel CreateEmptyState()
    {
        var panel = new Panel { Size = new Size(336, 176), BackColor = BoardStyle.Paper, BorderStyle = BorderStyle.FixedSingle };
        var heading = new Label
        {
            Text = "Pin your first note", Font = new Font("Georgia", 17F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 31), Size = new Size(334, 37)
        };
        var hint = new Label
        {
            Text = "Double-click the board to add a note.", ForeColor = BoardStyle.Muted,
            TextAlign = ContentAlignment.MiddleCenter, Location = new Point(0, 70), Size = new Size(334, 25)
        };
        Button first = BoardStyle.Button("New note", BoardStyle.Brick, Color.White);
        first.Bounds = new Rectangle(114, 112, 106, 34);
        first.Click += (_, _) => AddNote();
        panel.Controls.AddRange([heading, hint, first]);
        panel.Paint += (_, e) => BoardStyle.DrawPin(e.Graphics, new Point(panel.Width / 2, 18));
        return panel;
    }

    private void CenterEmptyState() => emptyState.Location = new Point(Math.Max(8, (canvas.ClientSize.Width - emptyState.Width) / 2),
        Math.Max(8, (canvas.ClientSize.Height - emptyState.Height) / 2));

    private void RefreshCount()
    {
        count.Text = data.Notes.Count == 1 ? "1 note" : $"{data.Notes.Count} notes";
        emptyState.Visible = data.Notes.Count == 0;
        if (emptyState.Visible) CenterEmptyState();
    }

    private void HideBoard()
    {
        if (!Visible || hidingToTray) return;
        hidingToTray = true;
        try
        {
            SaveNotes();
            if (!tray.EnsureRegistered())
            {
                ShowInTaskbar = true;
                WindowState = FormWindowState.Minimized;
                ShowTrayUnavailable();
                return;
            }

            Hide();
            ShowInTaskbar = false;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        }
        finally { hidingToTray = false; }
    }

    private void RestoreIfTrayUnavailable()
    {
        if (exiting || Visible) return;
        ShowBoard();
        ShowTrayUnavailable();
    }

    private void ShowTrayUnavailable()
    {
        saveStatus.ForeColor = BoardStyle.Brick;
        saveStatus.Text = "Tray icon unavailable. Corkboard is on the taskbar.";
    }

    private void MarkDirty()
    {
        if (exiting) return;
        dirty = true;
        saveStatus.ForeColor = BoardStyle.Muted;
        saveStatus.Text = "Saving…";
        autosave.Stop();
        autosave.Start();
    }

    private bool SaveNotes()
    {
        autosave.Stop();
        if (!dirty) return true;
        try
        {
            store.Save(data);
            dirty = false;
            saveStatus.ForeColor = BoardStyle.Muted;
            saveStatus.Text = "Saved on this PC";
            return true;
        }
        catch (Exception error) when (NoteStore.IsStorageError(error))
        {
            saveStatus.ForeColor = BoardStyle.Brick;
            saveStatus.Text = "Save failed. Changes are still open.";
            return false;
        }
    }

    private void ExitApp()
    {
        if (!SaveNotes() && MessageBox.Show("Corkboard couldn't save the latest changes. Exit anyway?", "Corkboard",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        exiting = true;
        Close();
    }

    private void RefreshStartup()
    {
        try { startWithWindows.Checked = Startup.IsEnabled(); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            startWithWindows.Enabled = false;
        }
    }

    private void ChangeStartup()
    {
        try
        {
            Startup.SetEnabled(!startWithWindows.Checked);
            RefreshStartup();
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show(this, "Windows couldn't change the startup setting.", "Corkboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
