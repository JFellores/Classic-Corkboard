namespace Corkboard;

internal sealed record TextSelectionRule(int Start, int Length, NoteRules.Settings Settings, NoteRules.Trigger Trigger = NoteRules.Trigger.Strike)
{
    public static void Update(StickyNote note, int start, int length, NoteRules.Trigger trigger, NoteRules.Settings? settings)
    {
        var rules = note.TextSelectionRules?.Where(rule => rule.Trigger != trigger || rule.Start >= start + length || rule.Start + (long)rule.Length <= start)
            .ToList() ?? [];
        if (settings is not null) rules.Add(new(start, length, settings, trigger));
        note.TextSelectionRules = rules.Count == 0 ? null : rules.ToArray();
    }

    public static void TrackEdit(StickyNote note, string text)
    {
        if (note.TextSelectionRules is not { Length: > 0 } rules || text == note.Text) return;
        int start = 0;
        while (start < text.Length && start < note.Text.Length && text[start] == note.Text[start]) start++;
        int oldEnd = note.Text.Length, newEnd = text.Length;
        while (oldEnd > start && newEnd > start && note.Text[oldEnd - 1] == text[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }
        var remaining = new List<TextSelectionRule>();
        foreach (TextSelectionRule rule in rules)
        {
            if (rule.Start + (long)rule.Length <= start) remaining.Add(rule);
            else if (rule.Start >= oldEnd) remaining.Add(rule with { Start = rule.Start + newEnd - oldEnd });
            else if (rule.Start < start && rule.Start + (long)rule.Length > oldEnd)
                remaining.Add(rule with { Length = rule.Length + newEnd - oldEnd });
        }
        note.TextSelectionRules = remaining.Count == 0 ? null : remaining.ToArray();
    }
}
