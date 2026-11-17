namespace Corkboard;

internal sealed record ChecklistItemRule(int Line, NoteRules.Settings? Settings, DateTimeOffset? CheckedAt, NoteRules.Settings? Uncheck = null)
{
    public static ChecklistItemRule? At(StickyNote note, int line) =>
        note.ChecklistItems?.FirstOrDefault(item => item.Line == line);

    public static NoteRules.Settings GetSettings(StickyNote note, int line) =>
        At(note, line)?.Settings ?? NoteRules.WholeRule(note, NoteRules.Trigger.Check)!;

    public static void Update(StickyNote note, int line, NoteRules.Settings? settings, DateTimeOffset? checkedAt,
        NoteRules.Settings? uncheck = null, bool changeUncheck = false)
    {
        if (!changeUncheck) uncheck = At(note, line)?.Uncheck;
        var items = note.ChecklistItems?.Where(item => item.Line != line).ToList() ?? [];
        if (settings is not null || checkedAt.HasValue || uncheck is not null) items.Add(new(line, settings, checkedAt, uncheck));
        note.ChecklistItems = items.Count == 0 ? null : items.ToArray();
    }

    public static int LineAt(string text, int start)
    {
        int line = 0;
        for (int index = 0; index < start; index++)
            if (text[index] == '\n') line++;
        return line;
    }

    public static bool MarkChecked(StickyNote note, int line, bool completed)
    {
        ChecklistItemRule? tracked = At(note, line);
        NoteRules.Settings? local = tracked?.Settings;
        NoteRules.Settings settings = local ?? NoteRules.Settings.For(note);
        DateTimeOffset? checkedAt = completed && (settings.DelayMinutes.HasValue || settings.Daily.HasValue)
            ? DateTimeOffset.Now : null;
        if (checkedAt == tracked?.CheckedAt) return false;
        Update(note, line, local, checkedAt);
        return true;
    }

    public static void RemoveLine(StickyNote note, int line)
    {
        if (note.ChecklistItems is not { } existing) return;
        ChecklistItemRule[] remaining = existing.Where(item => item.Line != line)
            .Select(item => item.Line > line ? item with { Line = item.Line - 1 } : item).ToArray();
        note.ChecklistItems = remaining.Length == 0 ? null : remaining;
    }

    public static void TrackEdit(StickyNote note, string text)
    {
        if (note.ChecklistItems is not { Length: > 0 } items || note.Text == text) return;
        string[] before = note.Text.Split('\n');
        string[] after = text.Split('\n');
        int prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        int oldEnd = before.Length, newEnd = after.Length;
        while (oldEnd > prefix && newEnd > prefix && before[oldEnd - 1] == after[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }

        var remaining = new List<ChecklistItemRule>();
        foreach (ChecklistItemRule item in items)
        {
            if (item.Line < 0 || item.Line >= before.Length) continue;
            int line = item.Line;
            if (line >= oldEnd) line += newEnd - oldEnd;
            // Keep an edited item in place for a single-line edit. A replaced block has no reliable item identity.
            else if (line >= prefix && oldEnd - prefix != 1) continue;
            else if (line >= prefix && newEnd - prefix != 1) continue;
            if (line >= after.Length) continue;
            int marker = NoteCard.FindChecklistMarker(after[line], 0);
            if (marker < 0) continue;
            DateTimeOffset? checkedAt = after[line][marker] == '☑' ? item.CheckedAt : null;
            if (item.Settings is not null || checkedAt.HasValue || item.Uncheck is not null)
                remaining.Add(item with { Line = line, CheckedAt = checkedAt });
        }
        note.ChecklistItems = remaining.Count == 0 ? null : remaining.ToArray();
    }
}
