using System.Text.Json;

namespace Corkboard;

internal sealed class BoardEditState
{
    private readonly int slides;
    private readonly BoardSlide slide;
    private readonly NoteState[] notes;
    private readonly ThreadState[]? threads;

    private BoardEditState(CorkBoard board, StickyNote[]? selected)
    {
        slides = board.HorizontalSlideCount;
        slide = board.CurrentSlide;
        notes = (selected is null ? (IEnumerable<StickyNote>)board.Notes : selected).Select(NoteState.Read).ToArray();
        if (selected is null) threads = board.Threads.Select(thread => new ThreadState(thread, thread.Color)).ToArray();
    }

    public static BoardEditState Read(CorkBoard board, StickyNote[]? selected) => new(board, selected);

    public static (Action Undo, Action Redo, long Size)? Difference(CorkBoard board, BoardEditState before, BoardEditState after,
        Action<CorkBoard, Guid[], bool> refresh)
    {
        var oldNotes = before.notes.ToDictionary(state => state.Note);
        var newNotes = after.notes.ToDictionary(state => state.Note);
        StickyNote[] affected = oldNotes.Keys.Union(newNotes.Keys).Where(note =>
            !oldNotes.TryGetValue(note, out var oldState) || !newNotes.TryGetValue(note, out var newState)
            || oldState != newState).ToArray();
        bool membership = before.threads is not null && !before.notes.Select(state => state.Note)
            .SequenceEqual(after.notes.Select(state => state.Note));
        bool slideLayout = before.slides != after.slides || affected.Any(note =>
            oldNotes.TryGetValue(note, out var oldState) && newNotes.TryGetValue(note, out var newState)
            && oldState.Slide != newState.Slide);
        bool threadEdit = before.threads is not null && !before.threads.SequenceEqual(after.threads!);
        if (affected.Length == 0 && !membership && !threadEdit && !slideLayout) return null;
        Guid[] ids = affected.Select(note => note.Id).ToArray();
        NoteState[] oldStates = before.notes.Where(state => affected.Contains(state.Note)).ToArray();
        NoteState[] newStates = after.notes.Where(state => affected.Contains(state.Note)).ToArray();
        StickyNote[]? oldOrder = membership ? before.notes.Select(state => state.Note).ToArray() : null;
        StickyNote[]? newOrder = membership ? after.notes.Select(state => state.Note).ToArray() : null;
        var oldSlide = (before.slides, before.slide);
        var newSlide = (after.slides, after.slide);
        ThreadState[]? oldThreads = threadEdit ? before.threads : null;
        ThreadState[]? newThreads = threadEdit ? after.threads : null;
        long size = oldStates.Concat(newStates).Sum(state => 2L * ((state.Text.Rtf?.Length ?? 0)
            + state.Text.Text.Length + (state.Table?.Length ?? 0)) + 256);
        return (() => Restore(oldStates, oldOrder, oldThreads, oldSlide), () => Restore(newStates, newOrder, newThreads, newSlide), size);

        void Restore(NoteState[] states, StickyNote[]? order, ThreadState[]? connections, (int Count, BoardSlide Current) layout)
        {
            foreach (NoteState state in states) state.Apply();
            if (order is not null)
            {
                board.Notes.Clear();
                board.Notes.AddRange(order);
            }
            if (connections is not null)
            {
                board.Threads.Clear();
                foreach (ThreadState state in connections)
                {
                    state.Thread.Color = state.Color;
                    board.Threads.Add(state.Thread);
                }
            }
            if (slideLayout)
            {
                board.HorizontalSlideCount = layout.Count;
                board.CurrentSlide = layout.Current;
            }
            refresh(board, ids, slideLayout);
        }
    }

    private readonly record struct ThreadState(NoteThread Thread, int? Color);

    private sealed record NoteState(StickyNote Note, NoteRules.TextState Text, NoteRules.Settings Checklist,
        DateOnly? LastReset, Dictionary<NoteRules.Trigger, NoteRules.Settings>? Rules, string Label, string? Table,
        bool Pinned, int? PinColor, int Color, Rectangle CardBounds, int Slide, bool PoppedOut, bool AlwaysOnTop,
        Rectangle? PopupBounds, NoteTimestampFormat Timestamp, bool Subtle, bool Pages)
    {
        public static NoteState Read(StickyNote note) => new(note, NoteRules.TextState.Read(note), NoteRules.Settings.For(note),
            note.LastChecklistReset, note.ContentRules, note.Label, note.Table is null ? null : JsonSerializer.Serialize(note.Table.Rows),
            note.Pinned, note.PinColor, note.Color, new(note.X, note.Y, note.CardWidth, note.CardHeight), note.Slide,
            note.PoppedOut, note.AlwaysOnTop, note.PopupBounds, note.TimestampFormat, note.SubtleTimestamps, note.UsePages);

        public void Apply()
        {
            Text.Apply(Note);
            Note.CheckedItemDestination = Checklist.Destination;
            Note.DailyChecklistReset = Checklist.Daily;
            Note.ChecklistResetDelayMinutes = Checklist.DelayMinutes;
            Note.LastChecklistReset = LastReset;
            Note.ContentRules = Rules;
            Note.Label = Label;
            if (Note.Table is { } table && Table is not null)
            {
                table.Rows.Clear();
                table.Rows.AddRange(JsonSerializer.Deserialize<List<List<string>>>(Table)!);
            }
            Note.Pinned = Pinned;
            Note.PinColor = PinColor;
            Note.Color = Color;
            Note.X = CardBounds.X;
            Note.Y = CardBounds.Y;
            Note.CardWidth = CardBounds.Width;
            Note.CardHeight = CardBounds.Height;
            Note.Slide = Slide;
            Note.PoppedOut = PoppedOut;
            Note.AlwaysOnTop = AlwaysOnTop;
            Note.PopupBounds = PopupBounds;
            Note.TimestampFormat = Timestamp;
            Note.SubtleTimestamps = Subtle;
            Note.UsePages = Pages;
        }
    }
}
