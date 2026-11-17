namespace Corkboard;

internal static class NoteListLayout
{
    private const char ItemBoundary = '\u2060';
    private const string ColumnBreak = "\u2060\t";
    private const string RowGap = "\u2060\u2003";

    internal static bool CanArrange(RichTextBox editor, bool restoring = false) =>
        ReadItems(editor) is { } items && (!restoring || items.Any(item => item.BreakLength == 2));

    internal static string VisibleText(string text) => text.Replace(ColumnBreak, "\t").Replace(RowGap, "\u2003");

    internal static bool Arrange(RichTextBox editor, int columns)
    {
        if (ReadItems(editor) is not { } items) return false;
        int selectedStart = editor.SelectionStart, selectedLength = editor.SelectionLength;

        string text = editor.Text;
        int first = items[0].Start;
        int end = items[^1].End;
        int available = Math.Max(1, editor.ClientSize.Width - editor.LogicalToDeviceUnits(6));
        int columnWidth = columns > 1 ? available / columns : available;
        var numbering = NoteParagraphs.LayoutNumbering(editor, first);
        int ordinal = numbering.Start;
        if (numbering.Kind == 2 && !numbering.Restart)
        {
            // A partially selected numbered list starts at its existing place in the sequence.
            for (int previous = first - 1; previous >= 0;)
            {
                int previousStart = NoteCard.LineStart(text, previous);
                var previousNumbering = NoteParagraphs.LayoutNumbering(editor, previousStart);
                if (previousNumbering.Kind != 2 || previousNumbering.Indent != numbering.Indent) break;
                ordinal += previousNumbering.Restart ? previousNumbering.Start : 1;
                if (previousNumbering.Restart) break;
                previous = previousStart - 1;
            }
        }

        var prefixes = new string[items.Count];
        var breaks = new string[items.Count - 1];
        int column = 0;
        for (int i = 0; i < items.Count; i++)
        {
            Item item = items[i];
            bool paragraphStart = item.Start == 0 || text[item.Start - 1] == '\n';
            if (paragraphStart)
            {
                var format = NoteParagraphs.LayoutNumbering(editor, item.Start);
                if (format.Kind == 2 && (numbering.Kind != 2 || numbering.Indent != format.Indent || i > 0 && format.Restart))
                    ordinal = format.Start;
                numbering = format;
            }

            string prefix = paragraphStart ? numbering.Kind switch
            {
                1 => "• ",
                2 => FormattableString.Invariant($"{ordinal++}. "),
                _ => ""
            } : "";
            prefixes[i] = prefix;

            if (columns <= 1)
            {
                if (i > 0) breaks[i - 1] = columns == 0 ? RowGap : "\n";
                continue;
            }

            editor.Select(item.Start, item.End - item.Start);
            string label = NoteLinks.SelectedDisplayText(editor);
            using Font? itemFont = editor.SelectionFont;
            int width = TextRenderer.MeasureText(prefix + label, itemFont ?? editor.Font).Width;
            bool wide = width > columnWidth - editor.LogicalToDeviceUnits(7);
            if (i > 0) breaks[i - 1] = column == 0 || wide ? "\n" : ColumnBreak;
            column = wide ? 0 : (column + 1) % columns;
        }

        if (columns > 1 && !breaks.Contains(ColumnBreak))
        {
            editor.Select(selectedStart, selectedLength);
            return false;
        }

        bool continuesNumbering = false;
        if (end + 1 < text.Length && numbering.Kind == 2)
        {
            var next = NoteParagraphs.LayoutNumbering(editor, end + 1);
            continuesNumbering = next.Kind == 2 && next.Indent == numbering.Indent && !next.Restart;
        }

        // Edit from the end so each item's saved position remains valid.
        for (int i = items.Count - 1; i >= 0; i--)
        {
            Item item = items[i];
            if (prefixes[i].Length > 0) NoteParagraphs.ClearLayoutNumbering(editor, item.Start);
            if (i < breaks.Length && !text.AsSpan(item.End, item.BreakLength).SequenceEqual(breaks[i]))
            {
                editor.Select(item.End, item.BreakLength);
                NoteLinks.InsertPlainText(editor, breaks[i]);
                end += breaks[i].Length - item.BreakLength;
            }
            if (prefixes[i].Length == 0) continue;
            editor.Select(item.Start, 0);
            NoteLinks.InsertPlainText(editor, prefixes[i]);
            end += prefixes[i].Length;
        }

        if (continuesNumbering) NoteParagraphs.ContinueLayoutNumbering(editor, end + 1, ordinal);
        editor.Select(first, end - first);
        editor.SelectionTabs = columns > 1 ? Enumerable.Range(1, columns - 1).Select(i => i * columnWidth).ToArray() : [];
        return true;
    }

    private static List<Item>? ReadItems(RichTextBox editor)
    {
        if (editor.SelectionLength == 0) return null;
        string text = editor.Text;
        int first = NoteCard.LineStart(text, editor.SelectionStart);
        int end = NoteCard.LineEnd(text, editor.SelectionStart + editor.SelectionLength - 1);
        var items = new List<Item>();
        int start = first;
        for (int position = first; position <= end;)
        {
            int length = 0;
            if (position < end)
            {
                if (text[position] == '\n') length = 1;
                else if (text[position] == ItemBoundary && text[position + 1] is '\t' or '\u2003') length = 2;
            }
            if (length == 0 && position < end)
            {
                position++;
                continue;
            }

            ReadOnlySpan<char> contents = text.AsSpan(start, position - start);
            if (contents.IsWhiteSpace() || NoteParagraphs.DividerStyle(contents) > 0
                || NoteCard.FindChecklistMarker(text, start) >= 0) return null;

            items.Add(new Item(start, position, length));
            if (position == end) break;
            position += length;
            start = position;
        }
        return items.Count >= 2 ? items : null;
    }

    private readonly record struct Item(int Start, int End, int BreakLength);
}
