namespace Corkboard;

internal sealed class CorkboardApp : ApplicationContext
{
    private readonly NoteStore store;
    private readonly BoardData data;
    private readonly Dictionary<Guid, BoardWindow> openBoards = [];
    private readonly Control dispatcher = new();
    private readonly ContextMenuStrip trayMenu = new();
    private readonly TrayIcon tray;
    private readonly System.Windows.Forms.Timer autosave = new() { Interval = 480 };
    private bool dirty;
    private readonly List<Guid> minimizedBoards = [];

    public CorkboardApp(NoteStore store, BoardData data, string? warning)
    {
        this.store = store;
        this.data = data;
        _ = dispatcher.Handle;
        tray = new TrayIcon(Icon, trayMenu, ReopenBoard, RestoreHiddenBoards);
        trayMenu.Opening += (_, _) => BuildTrayMenu();
        autosave.Tick += (_, _) => Save();

        Guid? preferred = data.LastBoard;
        CorkBoard[] previous = data.Boards.Where(board => board.WasOpen).ToArray();
        foreach (CorkBoard board in previous) Open(board);
        if (previous.Length == 0) Open(data.Boards.FirstOrDefault(board => board.Id == preferred) ?? data.Boards[0]);
        else if (previous.FirstOrDefault(board => board.Id == preferred) is CorkBoard last) Open(last);
        tray.EnsureRegistered();
        if (warning != null) foreach (BoardWindow window in openBoards.Values) window.ShowNotice(warning);
    }

    public Icon Icon { get; } = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
    public IEnumerable<CorkBoard> Boards => data.Boards;
    public bool Exiting { get; private set; }

    public void ReopenFromOtherInstance() => dispatcher.BeginInvoke(ReopenBoard);

    public BoardWindow Open(CorkBoard board)
    {
        minimizedBoards.Remove(board.Id);
        if (!openBoards.TryGetValue(board.Id, out BoardWindow? window))
        {
            window = new BoardWindow(this, board);
            if (board.Bounds is null && openBoards.Values.LastOrDefault(other => other.Visible) is BoardWindow previous)
            {
                Rectangle next = previous.Bounds;
                next.Offset(56, 42);
                window.StartPosition = FormStartPosition.Manual;
                window.Bounds = ResizableWindow.FitOnScreen(next, window.MinimumSize, Screen.FromControl(previous));
            }
            openBoards.Add(board.Id, window);
            window.Activated += (_, _) => SetLastBoard(board.Id);
            window.FormClosed += (_, _) => BoardClosed(board);
            board.WasOpen = true;
            MarkDirty();
        }
        window.ShowBoard();
        SetLastBoard(board.Id);
        return window;
    }

    public void CreateBoard(IWin32Window owner)
    {
        string? name = NamePrompt.Show(owner, "New corkboard", "Board name", "", 48);
        if (string.IsNullOrWhiteSpace(name)) return;
        var board = new CorkBoard { Name = name };
        data.Boards.Add(board);
        Open(board);
        RefreshLists();
    }

    public void RenameBoard(BoardWindow owner, CorkBoard board)
    {
        string? name = NamePrompt.Show(owner, "Rename corkboard", "Board name", board.Name, 48);
        if (string.IsNullOrWhiteSpace(name) || name == board.Name) return;
        board.Name = name;
        if (openBoards.TryGetValue(board.Id, out BoardWindow? window)) window.Text = name;
        MarkDirty();
        RefreshLists();
    }

    public void DeleteBoard(IWin32Window owner, CorkBoard board)
    {
        string count = board.Notes.Count == 1 ? "its 1 note" : $"all {board.Notes.Count} notes on it";
        string question = board.Notes.Count == 0 ? $"Delete “{board.Name}”?" : $"Delete “{board.Name}” and {count}?";
        if (MessageBox.Show(owner, question, "Delete corkboard", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes) CommitBoardDeletion(board);
    }

    public void DeleteNote(IWin32Window owner, CorkBoard board, StickyNote note)
    {
        string name = string.IsNullOrWhiteSpace(note.Label) ? note.Text.Split(['\r', '\n'], 2)[0].Trim() : note.Label;
        if (name.Length > 52) name = name[..51] + "…";
        string target = name.Length == 0 ? "this untitled note" : $"“{name}”";
        if (MessageBox.Show(owner, $"Delete {target} from “{board.Name}”?", "Delete note", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes) CommitNoteDeletion(board, note);
    }

    private void CommitBoardDeletion(CorkBoard board)
    {
        int index = data.Boards.IndexOf(board);
        if (index < 0) return;
        Guid? previouslyActive = data.LastBoard;
        data.Boards.RemoveAt(index);
        var replacement = data.Boards.Count == 0 ? new CorkBoard { Name = "My corkboard" } : null;
        if (replacement is not null) data.Boards.Add(replacement);
        if (data.LastBoard == board.Id) data.LastBoard = data.Boards[0].Id;
        MarkDirty();
        if (!Save())
        {
            if (replacement is not null) data.Boards.Remove(replacement);
            data.Boards.Insert(index, board);
            data.LastBoard = previouslyActive;
            MarkDirty();
            return;
        }

        if (openBoards.TryGetValue(board.Id, out BoardWindow? removed))
        {
            if (openBoards.Count == 1) Open(data.Boards[0]);
            removed.Close();
        }
        RefreshLists();
    }

    private void CommitNoteDeletion(CorkBoard board, StickyNote note)
    {
        int index = board.Notes.IndexOf(note);
        if (index < 0) return;
        NoteThread[] previousThreads = board.Threads.ToArray();
        board.Notes.RemoveAt(index);
        board.Threads.RemoveAll(thread => thread.From == note.Id || thread.To == note.Id);
        MarkDirty();
        if (!Save())
        {
            board.Notes.Insert(index, note);
            board.Threads.Clear();
            board.Threads.AddRange(previousThreads);
            MarkDirty();
            return;
        }

        if (openBoards.TryGetValue(board.Id, out BoardWindow? window)) window.RemoveNoteView(note);
        RefreshLists();
    }

    public bool CanHideInTray(CorkBoard board)
    {
        Save();
        if (!tray.EnsureRegistered()) return false;
        minimizedBoards.Remove(board.Id);
        minimizedBoards.Add(board.Id);
        return true;
    }

    public void MarkDirty()
    {
        if (Exiting) return;
        dirty = true;
        autosave.Stop();
        autosave.Start();
    }

    public bool Save()
    {
        autosave.Stop();
        if (!dirty) return true;
        try
        {
            store.Save(data);
            dirty = false;
            return true;
        }
        catch (Exception error) when (NoteStore.IsStorageError(error))
        {
            foreach (BoardWindow board in openBoards.Values) board.ShowNotice("Couldn’t save. Your changes are still open.");
            return false;
        }
    }

    public void Exit()
    {
        if (!Save() && MessageBox.Show("Corkboard couldn’t save the latest changes. Exit anyway?", "Corkboard",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        Exiting = true;
        foreach (BoardWindow board in openBoards.Values.ToArray()) board.Close();
        tray.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (BoardWindow board in openBoards.Values.ToArray()) board.Dispose();
            tray.Dispose();
            trayMenu.Dispose();
            autosave.Dispose();
            dispatcher.Dispose();
            Icon.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ReopenBoard()
    {
        if (Exiting) return;
        Guid? preferred = minimizedBoards.Count == 0 ? data.LastBoard : minimizedBoards[^1];
        Open(data.Boards.FirstOrDefault(board => board.Id == preferred) ?? data.Boards[0]);
    }

    private void SetLastBoard(Guid id)
    {
        if (data.LastBoard == id) return;
        data.LastBoard = id;
        MarkDirty();
    }

    private void BoardClosed(CorkBoard board)
    {
        openBoards.Remove(board.Id);
        minimizedBoards.Remove(board.Id);
        if (Exiting || !data.Boards.Contains(board)) return;
        board.WasOpen = false;
        MarkDirty();
        Save();
        RefreshLists();
        if (openBoards.Count == 0 && !tray.EnsureRegistered()) Exit();
    }

    private void RestoreHiddenBoards()
    {
        minimizedBoards.Clear();
        foreach (BoardWindow window in openBoards.Values.Where(window => !window.Visible).ToArray())
        {
            window.ShowBoard();
            window.ShowNotice("Tray unavailable. Corkboard is on the taskbar.");
        }
    }

    private void RefreshLists()
    {
        foreach (BoardWindow window in openBoards.Values) window.RefreshBoardList();
    }

    private void BuildTrayMenu()
    {
        foreach (ToolStripItem item in trayMenu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        trayMenu.Items.Add("Open Corkboard", null, (_, _) => ReopenBoard());
        var boards = new ToolStripMenuItem("Corkboards");
        foreach (CorkBoard board in data.Boards)
        {
            var item = new ToolStripMenuItem(board.Name, null, (_, _) => Open(board)) { Checked = openBoards.ContainsKey(board.Id) };
            boards.DropDownItems.Add(item);
        }
        trayMenu.Items.Add(boards);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit Corkboard", null, (_, _) => Exit());
    }
}
