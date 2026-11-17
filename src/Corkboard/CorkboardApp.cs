using System.Runtime.InteropServices;
using System.Text.Json;

namespace Corkboard;

internal sealed class CorkboardApp : ApplicationContext, IMessageFilter
{
    internal const string BoardShortcut = "Ctrl+Alt+B";
    private readonly NoteStore store;
    private readonly BoardData data;
    private readonly Dictionary<Guid, BoardWindow> openBoards = [];
    private readonly ShortcutDispatcher dispatcher;
    private readonly CorkMenu trayMenu = new();
    private readonly TrayIcon tray;
    private readonly System.Windows.Forms.Timer autosave = new() { Interval = 480 };
    private readonly System.Windows.Forms.Timer checklistTimer = new();
    private readonly EditHistory history = new();
    private bool replaying;
    private bool tracking;
    private int editDepth;
    private bool dirty;
    private readonly List<Guid> minimizedBoards = [];
    private Guid? shortcutHiddenBoard;

    public CorkboardApp(NoteStore store, BoardData data, string? warning)
    {
        this.store = store;
        this.data = data;
        Reminders = new Reminders(this, data, store.ReminderSoundFolder);
        dispatcher = new ShortcutDispatcher(ToggleBoardShortcut);
        _ = dispatcher.Handle;
        tray = new TrayIcon(Icon, trayMenu, ReopenBoard, RestoreHiddenBoards);
        trayMenu.Opening += (_, _) => BuildTrayMenu();
        autosave.Tick += (_, _) => Save();
        foreach (StickyNote note in data.Boards.SelectMany(board => board.Notes))
        {
            if (!note.Text.Contains('☐') && !note.Text.Contains('☑')) continue;
            using var editor = new RichTextBox { DetectUrls = false };
            NoteCard.LoadNoteText(editor, note);
            if (!NoteParagraphs.RepairChecklistMarkers(editor)) continue;
            var repaired = NoteRules.TextState.Read(editor, note) with { Checklist = note.ChecklistItems };
            repaired.Apply(note);
            MarkDirty();
        }
        checklistTimer.Tick += (_, _) => RunChecklistResets(DateTime.Now);
        RunChecklistResets(DateTime.Now);

        Guid? preferred = data.LastBoard;
        CorkBoard[] previous = data.Boards.Where(board => board.WasOpen).ToArray();
        foreach (CorkBoard board in previous) Open(board);
        if (previous.Length == 0) Open(data.Boards.FirstOrDefault(board => board.Id == preferred) ?? data.Boards[0]);
        else if (previous.FirstOrDefault(board => board.Id == preferred) is CorkBoard last) Open(last);
        tray.EnsureRegistered();
        if (warning != null) foreach (BoardWindow window in openBoards.Values) window.ShowNotice(warning);
        else if (!dispatcher.Registered && data.LastBoard is Guid lastBoard && openBoards.TryGetValue(lastBoard, out BoardWindow? active))
            active.ShowNotice($"{BoardShortcut} is unavailable. You can still use the tray icon.");
        tracking = true;
        Application.AddMessageFilter(this);
        Reminders.Start();
    }

    public Icon Icon { get; } = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
    public ImageLibrary Images => store.Images;
    public Reminders Reminders { get; }
    public IEnumerable<CorkBoard> Boards => data.Boards;
    public bool Exiting { get; private set; }
    public string? UndoLabel => history.UndoLabel;
    public string? RedoLabel => history.RedoLabel;

    public IDisposable BeginEdit(CorkBoard board, string label, StickyNote[]? notes = null, object? group = null,
        Action<bool>? replayArchive = null)
    {
        if (!tracking || replaying || Exiting || editDepth > 0) return EditScope.Empty;
        editDepth++;
        BoardEditState before = BoardEditState.Read(board, notes);
        return new EditScope(() =>
        {
            editDepth--;
            var after = BoardEditState.Read(board, notes);
            if (BoardEditState.Difference(board, before, after, RefreshAfterEdit) is not { } change) return;
            if (replayArchive is null) history.Record(label, change.Undo, change.Redo, group, change.Size);
            else history.Record(label,
                () => { change.Undo(); replayArchive(false); },
                () => { change.Redo(); replayArchive(true); }, group, change.Size);
        });
    }

    public void RecordEdit(string label, Action undo, Action redo, long size = 0)
    {
        if (tracking && !replaying && !Exiting && editDepth == 0) history.Record(label, undo, redo, size: size);
    }

    public bool HandleHistoryKey(Keys keys, Control? origin)
    {
        bool undo = keys == (Keys.Control | Keys.Z);
        bool redo = keys is (Keys.Control | Keys.Y) or (Keys.Control | Keys.Shift | Keys.Z);
        if (!undo && !redo) return false;
        StepHistory(redo, origin);
        return true;
    }

    bool IMessageFilter.PreFilterMessage(ref Message message)
    {
        if (message.Msg != 0x0100 || (Keys)(int)message.WParam is not (Keys.Z or Keys.Y)) return false;
        Control? origin = Control.FromChildHandle(message.HWnd);
        if (origin?.FindForm() is not (BoardWindow or FloatingNote)) return false;
        return HandleHistoryKey((Keys)(int)message.WParam | Control.ModifierKeys, origin);
    }

    public bool StepHistory(bool redo, Control? origin = null)
    {
        if (editDepth > 0 || replaying || Exiting) return false;
        foreach (BoardWindow window in openBoards.Values.ToArray()) window.CommitTableEdits();
        replaying = true;
        try
        {
            string? label = history.Step(redo);
            if (label is null) return false;
            MarkDirty();
            UpdateChecklistTimer();
            BoardWindow? notice = origin as BoardWindow ?? origin?.FindForm() as BoardWindow
                ?? openBoards.Values.FirstOrDefault(window => window.Visible);
            if (notice is { IsDisposed: false }) notice.ShowNotice($"{(redo ? "Redo" : "Undo")} · {label}");
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            (origin?.FindForm() as BoardWindow)?.ShowNotice("Windows couldn’t restore that setting.");
            return false;
        }
        finally { replaying = false; }
    }

    private void RefreshAfterEdit(CorkBoard board, Guid[] notes, bool slides)
    {
        if (openBoards.TryGetValue(board.Id, out BoardWindow? window)) window.RefreshFromHistory(notes, slides);
        RefreshLists();
    }

    internal void RestoreBoardBounds(CorkBoard board, Rectangle bounds)
    {
        board.Bounds = bounds;
        if (openBoards.TryGetValue(board.Id, out BoardWindow? window)) window.RestoreSavedBounds(bounds);
    }

    internal void ToggleStartup()
    {
        string? before = Startup.ReadCommand();
        bool enable = !Startup.IsEnabled();
        Startup.SetEnabled(enable);
        RecordEdit("change Windows startup", () => Startup.WriteCommand(before), () => Startup.SetEnabled(enable));
    }

    private sealed class EditScope(Action complete) : IDisposable
    {
        public static readonly EditScope Empty = new(() => { });
        public void Dispose() => complete();
    }

    public void ReopenFromOtherInstance() => dispatcher.BeginInvoke(ReopenBoard);

    public void OpenReminder(NoteReminder item)
    {
        CorkBoard? board = data.Boards.Find(board => board.Id == item.Board);
        StickyNote? note = board?.Notes.Find(note => note.Id == item.Note);
        if (board is not null && note is not null) Open(board).FocusNote(note);
        else openBoards.Values.FirstOrDefault(window => window.Visible)?.ShowNotice("The reminder’s source note is unavailable.");
    }

    public (Action Undo, Action Redo, long Size)? ApplyReminderRule(NoteReminder item, NoteRules.Trigger trigger)
    {
        CorkBoard? board = data.Boards.Find(candidate => candidate.Id == item.Board);
        StickyNote? source = board?.Notes.Find(note => note.Id == item.Note);
        if (board is null || source is null || source.ImageFile is not null || source.Table is not null) return null;

        // Keep the selected occurrence where possible. If text has moved, only use a unique match.
        int offset = item.Offset ?? -1;
        if (offset < 0 || offset > source.Text.Length || !source.Text.AsSpan(offset).StartsWith(item.Text, StringComparison.Ordinal))
        {
            offset = source.Text.IndexOf(item.Text, StringComparison.Ordinal);
            if (offset < 0 || source.Text.IndexOf(item.Text, offset + 1, StringComparison.Ordinal) >= 0) return null;
        }
        NoteRules.Settings? rule = source.TextSelectionRules?.FirstOrDefault(candidate =>
            candidate.Trigger == trigger && candidate.Start == offset && candidate.Length == item.Text.Length)?.Settings
            ?? NoteRules.WholeRule(source, trigger);
        if (rule is null || rule == new NoteRules.Settings(null)) return null;
        StickyNote? target = rule.Destination is Guid destination && board.Threads.Any(thread =>
            (thread.From == source.Id && thread.To == destination) || (thread.To == source.Id && thread.From == destination))
            ? board.Notes.Find(note => note.Id == destination && note.Id != source.Id && note.Table is null && note.ImageFile is null)
            : null;
        StickyNote[] affected = target is null ? [source] : [source, target];
        var before = BoardEditState.Read(board, affected);
        using var editor = new RichTextBox { DetectUrls = false };
        NoteCard.LoadNoteText(editor, source);
        if (!SelectReminderText(editor, offset, item.Text)) return null;
        BoardWindow.FormatRuleText(editor, source, rule);
        if (target is not null && BoardWindow.AppendSelection(target, editor) is { } appended)
        {
            editor.SelectedText = "";
            appended.Apply(target);
        }
        NoteRules.TextState.Read(editor, source).Apply(source);
        var after = BoardEditState.Read(board, affected);
        var change = BoardEditState.Difference(board, before, after, RefreshAfterEdit);
        if (change.HasValue)
        {
            MarkDirty();
            RefreshAfterEdit(board, affected.Select(note => note.Id).ToArray(), false);
            if (trigger == NoteRules.Trigger.ReminderDue)
                RecordEdit("apply reminder rule", change.Value.Undo, change.Value.Redo, change.Value.Size);
        }
        return change;
    }

    internal static bool SelectReminderText(RichTextBox editor, int visibleStart, string text)
    {
        if (text.Length == 0) return false;
        string raw = editor.Text;
        for (int start = visibleStart; start <= raw.Length - text.Length; start++)
        {
            if (!raw.AsSpan(start).StartsWith(text, StringComparison.Ordinal)) continue;
            if (start == 0) return Select(0);
            editor.Select(0, start);
            if (NoteParagraphs.RemoveDividerMarkers(NoteLinks.SelectedDisplayText(editor)).Length == visibleStart)
                return Select(start);
        }
        return false;

        bool Select(int start)
        {
            (int first, int last) = NoteLinks.IncludeWholeLinks(editor, raw, start, start + text.Length);
            editor.Select(first, last - first);
            return NoteParagraphs.RemoveDividerMarkers(NoteLinks.SelectedDisplayText(editor)) == text;
        }
    }

    public BoardWindow Open(CorkBoard board)
    {
        if (shortcutHiddenBoard == board.Id) shortcutHiddenBoard = null;
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
        int index = data.Boards.Count;
        data.Boards.Add(board);
        Open(board);
        RefreshLists();
        RecordEdit("create board", () => RemoveBoardFromHistory(board), () => RestoreBoardFromHistory(board, index));
    }

    public void RenameBoard(BoardWindow owner, CorkBoard board)
    {
        string? name = NamePrompt.Show(owner, "Rename corkboard", "Board name", board.Name, 48);
        if (string.IsNullOrWhiteSpace(name) || name == board.Name) return;
        string before = board.Name;
        ApplyName(name);
        RecordEdit("rename board", () => ApplyName(before), () => ApplyName(name));

        void ApplyName(string value)
        {
            board.Name = value;
            if (openBoards.TryGetValue(board.Id, out BoardWindow? window)) window.Text = value;
            MarkDirty();
            RefreshLists();
        }
    }

    public void DeleteBoard(IWin32Window owner, CorkBoard board)
    {
        string count = board.Notes.Count == 1 ? "its 1 note" : $"all {board.Notes.Count} notes on it";
        string question = board.Notes.Count == 0 ? $"Delete “{board.Name}”?" : $"Delete “{board.Name}” and {count}?";
        if (CorkDialog.Confirm(owner, "Delete corkboard", question, "Delete board")) CommitBoardDeletion(board);
    }

    public void DeleteNote(IWin32Window owner, CorkBoard board, StickyNote note)
    {
        string name = string.IsNullOrWhiteSpace(note.Label) ? note.Text.Split(['\r', '\n'], 2)[0].Trim() : note.Label;
        if (name.Length > 52) name = name[..51] + "…";
        string kind = note.Table is null ? "note" : "table";
        string target = name.Length == 0 ? $"this untitled {kind}" : $"“{name}”";
        if (CorkDialog.Confirm(owner, $"Delete {kind}", $"Delete {target} from “{board.Name}”?", $"Delete {kind}")) CommitNoteDeletion(board, note);
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
        UpdateChecklistTimer();
        RefreshLists();
        RecordEdit("delete board", () =>
        {
            RestoreBoardFromHistory(board, index);
            if (replacement is not null) RemoveBoardFromHistory(replacement);
        }, () =>
        {
            if (replacement is not null) RestoreBoardFromHistory(replacement, 0, show: false);
            RemoveBoardFromHistory(board);
        });
    }

    private void RestoreBoardFromHistory(CorkBoard board, int index, bool show = true)
    {
        data.Boards.Insert(Math.Min(index, data.Boards.Count), board);
        if (show) Open(board);
        RefreshLists();
    }

    private void RemoveBoardFromHistory(CorkBoard board)
    {
        data.Boards.Remove(board);
        if (data.LastBoard == board.Id) data.LastBoard = data.Boards[0].Id;
        if (openBoards.TryGetValue(board.Id, out BoardWindow? window))
        {
            if (openBoards.Count == 1) Open(data.Boards[0]);
            window.Close();
        }
        RefreshLists();
    }

    private void CommitNoteDeletion(CorkBoard board, StickyNote note)
    {
        int index = board.Notes.IndexOf(note);
        if (index < 0) return;
        if (openBoards.TryGetValue(board.Id, out BoardWindow? editing)) editing.CommitTableEdits();
        var entry = DeletedItem.Capture(board, slideIndex: null, [note]);
        DeletedItem[]? previousArchive = ArchiveDeletion(entry);
        using var edit = BeginEdit(board, "delete card", replayArchive: redo => SetArchived(entry, redo));
        NoteThread[] previousThreads = board.Threads.ToArray();
        board.Notes.RemoveAt(index);
        board.Threads.RemoveAll(thread => thread.From == note.Id || thread.To == note.Id);
        MarkDirty();
        if (!Save())
        {
            RestoreArchive(previousArchive);
            board.Notes.Insert(index, note);
            board.Threads.Clear();
            board.Threads.AddRange(previousThreads);
            MarkDirty();
            return;
        }

        if (openBoards.TryGetValue(board.Id, out BoardWindow? window))
        {
            window.RemoveNoteView(note);
            window.ShowNotice("Card deleted · Restore from Version history");
        }
        UpdateChecklistTimer();
        RefreshLists();
    }

    internal DeletedItem[]? ArchiveDeletion(DeletedItem entry)
    {
        var previous = data.DeletedItems;
        SetArchived(entry, true);
        return previous;
    }

    internal void RestoreArchive(DeletedItem[]? previous) => data.DeletedItems = previous;

    internal void SetArchived(DeletedItem entry, bool archived)
    {
        const int entryLimit = 50;
        const int sizeBudget = 8 * 1024 * 1024;
        IEnumerable<DeletedItem> others = (data.DeletedItems ?? []).Where(item => item is not null && item.Id != entry.Id);
        if (archived) others = others.Prepend(entry);
        var recent = new List<DeletedItem>();
        long size = 0;
        foreach (DeletedItem item in others.OrderByDescending(item => item.DeletedAt).Take(entryLimit))
        {
            size += Math.Max(0, item.Size);
            if (recent.Count > 0 && size > sizeBudget) break;
            recent.Add(item);
        }
        data.DeletedItems = recent.Count == 0 ? null : recent.ToArray();
    }

    internal void ShowVersionHistory(BoardWindow owner, CorkBoard fallback)
    {
        DeletedItem[] entries = (data.DeletedItems ?? []).Where(item => item is not null)
            .OrderByDescending(item => item.DeletedAt).ToArray();
        var entry = RecoveryView.Choose(owner, entries, item =>
        {
            CorkBoard target = RecoveryBoard(item, fallback);
            return item.SlideIndex.HasValue ? $"Restore as a slide in {target.Name}"
                : $"Restore to {target.Name} · {target.CurrentSlide.Name}";
        });
        if (entry is null) return;
        try
        {
            if (!RestoreDeletedItem(entry, fallback)) owner.ShowNotice("Couldn’t restore that item. It’s still in Version history.");
        }
        catch (JsonException)
        {
            owner.ShowNotice("Couldn’t read that recovery entry.");
        }
    }

    private CorkBoard RecoveryBoard(DeletedItem entry, CorkBoard fallback) => data.Boards.Find(board => board.Id == entry.BoardId)
        ?? (data.Boards.Contains(fallback) ? fallback : data.Boards[0]);

    internal bool RestoreDeletedItem(DeletedItem entry, CorkBoard fallback)
    {
        if (data.DeletedItems?.Contains(entry) != true) return false;
        CorkBoard target = RecoveryBoard(entry, fallback);
        if (openBoards.TryGetValue(target.Id, out BoardWindow? window)) window.CommitTableEdits();
        var contents = entry.Read();
        var existing = target.Notes.ToDictionary(note => note.Id);
        if (contents.Cards.Any(card => existing.ContainsKey(card.Id))) return false;
        bool slide = entry.SlideIndex.HasValue;
        BoardEditState before = BoardEditState.Read(target, null);
        if (entry.SlideIndex is int index)
        {
            int position = Math.Clamp(index, 0, target.HorizontalSlideCount);
            foreach (StickyNote card in target.Notes)
                if (card.Slide >= position) card.Slide++;
            target.HorizontalSlideCount++;
            target.CurrentSlide = new BoardSlide(position);
        }

        foreach (StickyNote card in contents.Cards)
        {
            card.SlideAddress = target.CurrentSlide;
            target.Notes.Add(card);
            existing.Add(card.Id, card);
        }
        foreach (NoteThread thread in contents.Threads)
        {
            if (thread.From == thread.To || !existing.TryGetValue(thread.From, out StickyNote? from) || !from.Pinned
                || !existing.TryGetValue(thread.To, out StickyNote? to) || !to.Pinned
                || target.Threads.Any(other => (other.From == thread.From && other.To == thread.To)
                    || (other.From == thread.To && other.To == thread.From))) continue;
            target.Threads.Add(thread);
        }
        BoardEditState after = BoardEditState.Read(target, null);
        if (BoardEditState.Difference(target, before, after, RefreshAfterEdit) is not { } change) return false;

        DeletedItem[]? previousArchive = data.DeletedItems;
        SetArchived(entry, false);
        MarkDirty();
        if (!Save())
        {
            RestoreArchive(previousArchive);
            change.Undo();
            MarkDirty();
            return false;
        }

        RefreshAfterEdit(target, contents.Cards.Select(card => card.Id).ToArray(), slides: slide);
        UpdateChecklistTimer();
        RecordEdit(slide ? "restore slide" : "restore card",
            () => { change.Undo(); SetArchived(entry, true); },
            () => { change.Redo(); SetArchived(entry, false); }, change.Size);
        Open(target).ShowNotice(slide ? "Slide restored" : "Card restored");
        return true;
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

    public void SetChecklistReset(StickyNote note, TimeOnly? time)
    {
        if (note.DailyChecklistReset == time) return;
        DateTime now = DateTime.Now;
        note.DailyChecklistReset = time;
        note.LastChecklistReset = time is TimeOnly reset ? LatestResetDate(now, reset) : null;
        MarkDirty();
    }

    public void UpdateChecklistTimer() => RunChecklistResetsAt(DateTime.Now, applyDue: !replaying && editDepth == 0);

    private static DateOnly LatestResetDate(DateTime now, TimeOnly time)
    {
        DateOnly today = DateOnly.FromDateTime(now);
        return TimeOnly.FromDateTime(now) >= time ? today : today.AddDays(-1);
    }

    private void RunChecklistResets(DateTime now) => RunChecklistResetsAt(now, applyDue: editDepth == 0);

    private void RunChecklistResetsAt(DateTime now, bool applyDue)
    {
        checklistTimer.Stop();
        DateTimeOffset? nextReset = null;
        var instant = new DateTimeOffset(now);
        foreach (CorkBoard board in data.Boards)
        {
            foreach (StickyNote note in board.Notes)
            {
                bool dailyDue = false;
                DateOnly? handledDate = null;
                if (note.DailyChecklistReset is TimeOnly time)
                {
                    DateOnly due = LatestResetDate(now, time);
                    if (note.LastChecklistReset is null || note.LastChecklistReset < due)
                    {
                        dailyDue = applyDue;
                        if (applyDue) handledDate = due;
                        else Schedule(instant);
                    }
                    Schedule(new DateTimeOffset((handledDate ?? note.LastChecklistReset ?? due).AddDays(1).ToDateTime(time)));
                }
                HashSet<int>? timed = null;
                if (note.ChecklistItems is { } items)
                {
                    foreach (ChecklistItemRule item in items)
                    {
                        if (item.CheckedAt is not DateTimeOffset checkedAt) continue;
                        NoteRules.Settings settings = item.Settings ?? NoteRules.Settings.For(note);
                        DateTimeOffset? due = settings.DelayMinutes is >= 1 and <= 43200
                            ? checkedAt.AddMinutes(settings.DelayMinutes.Value)
                            : item.Settings?.Daily is TimeOnly local
                                ? new DateTimeOffset(LatestResetDate(checkedAt.LocalDateTime, local).AddDays(1).ToDateTime(local))
                                : null;
                        if (due is not { } deadline) continue;
                        if (deadline > instant || !applyDue) Schedule(deadline);
                        else (timed ??= []).Add(item.Line);
                    }
                }
                if (!dailyDue && timed is null) continue;
                NoteRules.TextState before = NoteRules.TextState.Read(note);
                if (handledDate.HasValue) note.LastChecklistReset = handledDate;
                bool changed = NoteCard.ResetCheckedItems(note, line => timed?.Contains(line) == true
                    || (dailyDue && ChecklistItemRule.At(note, line)?.Settings is null));
                if (note.ChecklistItems is { } tracked)
                {
                    foreach (ChecklistItemRule item in tracked)
                        if (item.CheckedAt.HasValue && (timed?.Contains(item.Line) == true || (dailyDue && item.Settings is null)))
                            ChecklistItemRule.Update(note, item.Line, item.Settings, null);
                }
                if (timed is not null || changed || handledDate.HasValue) MarkDirty();
                if (changed && openBoards.TryGetValue(board.Id, out BoardWindow? window)) window.RefreshNoteText(note);
                if (changed && tracking)
                {
                    NoteRules.TextState after = NoteRules.TextState.Read(note);
                    // A reset that was undone has already used its scheduled occurrence.
                    before = before with { Checklist = after.Checklist };
                    RecordEdit("checklist reset", () => Restore(before), () => Restore(after),
                        2L * (before.Text.Length + after.Text.Length + (before.Rtf?.Length ?? 0) + (after.Rtf?.Length ?? 0)));

                    void Restore(NoteRules.TextState state)
                    {
                        state.Apply(note);
                        RefreshAfterEdit(board, [note.Id], false);
                    }
                }
            }
        }
        if (nextReset is DateTimeOffset scheduled)
        {
            // Recheck the local clock after sleep or a clock change.
            checklistTimer.Interval = (int)Math.Clamp(Math.Ceiling((scheduled - instant).TotalMilliseconds), 1, 60_000);
            checklistTimer.Start();
        }

        void Schedule(DateTimeOffset due)
        {
            if (nextReset is null || due < nextReset) nextReset = due;
        }
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
        foreach (BoardWindow board in openBoards.Values)
        {
            board.CommitTableEdits();
            board.RememberBounds();
        }
        if (!Save() && MessageBox.Show("Corkboard couldn’t save the latest changes. Exit anyway?", "Corkboard",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        Exiting = true;
        checklistTimer.Stop();
        foreach (BoardWindow board in openBoards.Values.ToArray()) board.Close();
        tray.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            foreach (BoardWindow board in openBoards.Values.ToArray()) board.Dispose();
            Reminders.Dispose();
            tray.Dispose();
            trayMenu.Dispose();
            autosave.Dispose();
            checklistTimer.Dispose();
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

    private void ToggleBoardShortcut()
    {
        if (Exiting || Application.OpenForms.OfType<Form>().Any(form => form.Modal)) return;

        Guid? id = shortcutHiddenBoard ?? data.LastBoard;
        CorkBoard? board = data.Boards.Find(candidate => candidate.Id == id);
        if (board is null || !openBoards.TryGetValue(board.Id, out BoardWindow? window))
        {
            ReopenBoard();
            return;
        }

        if (!window.Visible || window.WindowState == FormWindowState.Minimized)
        {
            Open(board);
            return;
        }

        if (!ShortcutDispatcher.IsWindowEnabled(window.Handle)) return;
        shortcutHiddenBoard = board.Id;
        window.MinimizeBoard();
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
        if (shortcutHiddenBoard == board.Id) shortcutHiddenBoard = null;
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
        trayMenu.Items.Add(new ToolStripMenuItem("Open Corkboard", null, (_, _) => ReopenBoard())
        {
            ShortcutKeyDisplayString = dispatcher.Registered ? BoardShortcut : ""
        });
        var boards = new CorkSubmenuItem("Corkboards");
        foreach (CorkBoard board in data.Boards)
        {
            var item = new ToolStripMenuItem(board.Name, null, (_, _) => Open(board)) { Checked = openBoards.ContainsKey(board.Id) };
            boards.DropDownItems.Add(item);
        }
        trayMenu.Items.Add(boards);
        trayMenu.Items.Add("Version history…", null, (_, _) =>
        {
            CorkBoard board = data.Boards.Find(item => item.Id == data.LastBoard) ?? data.Boards[0];
            ShowVersionHistory(Open(board), board);
        });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit Corkboard", null, (_, _) => Exit());
    }

    private sealed class ShortcutDispatcher(Action toggle) : Control
    {
        private const int ShortcutId = 1;
        private const int HotkeyMessage = 0x0312;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModNoRepeat = 0x4000;
        public bool Registered { get; private set; }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Registered = RegisterHotKey(Handle, ShortcutId, ModAlt | ModControl | ModNoRepeat, (uint)Keys.B);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (Registered) UnregisterHotKey(Handle, ShortcutId);
            Registered = false;
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == HotkeyMessage && message.WParam == new IntPtr(ShortcutId))
            {
                toggle();
                return;
            }
            base.WndProc(ref message);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowEnabled(IntPtr window);
    }

}
