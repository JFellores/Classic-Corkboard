using System.Runtime.InteropServices;

namespace Corkboard;

internal static class NoteParagraphs
{
    private const ushort Bullets = 1, Numbers = 2;
    private const uint GetParagraphFormat = 0x043D, SetParagraphFormat = 0x0447;
    private const uint NumberingMask = 0xE020, StartIndentMask = 0x0001;
    private const uint NumberingTabMask = 0x4000;
    private const int CompactIndent = 180, StandardIndent = 300;
    private const ushort CompactTab = 120, StandardTab = 180;
    private const string Dash = "– ";
    internal const char DividerMarker = '\u2063';
    private const string DividerMarkers = "\u2063\u2062\u2064\u2061";

    public static void CycleList(RichTextBox editor)
    {
        string text = editor.Text;
        int start = editor.SelectionStart;
        int end = start + editor.SelectionLength;
        int first = LineStart(text, start);
        int last = LineEnd(text, Math.Max(start, end - 1));
        var lines = new List<(int Offset, int Style)>();
        bool standard = false;
        for (int line = first; line <= last; line = LineEnd(text, line) + 1)
        {
            editor.Select(line, 0);
            ParagraphFormat format = ReadFormat(editor);
            if (DividerStyle(text.AsSpan(line, LineEnd(text, line) - line)) > 0) continue;
            int style = format.Numbering switch
            {
                Bullets => 1,
                Numbers => 2,
                _ => HasDash(text, line) ? 3 : 0
            };
            if (lines.Count == 0 && style is 1 or 2) standard = format.StartIndent >= StandardIndent;
            lines.Add((line, style));
        }
        if (lines.Count == 0)
        {
            editor.Select(start, end - start);
            return;
        }
        int next = lines.All(line => line.Style == lines[0].Style) ? (lines[0].Style + 1) % 4 : 1;
        for (int index = lines.Count - 1; index >= 0; index--)
        {
            (int offset, int style) = lines[index];
            if (style != 3 && next != 3) continue;
            editor.Select(offset, style == 3 ? Dash.Length : 0);
            if (style == 3) editor.SelectedText = "";
            else NoteLinks.InsertPlainText(editor, Dash);
            int delta = style == 3 ? -Dash.Length : Dash.Length;
            if (start == end && start == offset && delta > 0) start = end = offset + delta;
            else
            {
                if (start > offset) start = Math.Max(offset, start + delta);
                if (end > offset) end = Math.Max(offset, end + delta);
            }
        }
        if (next is 1 or 2 || lines.Any(line => line.Style is 1 or 2))
        {
            editor.Select(first, Math.Max(0, LineEnd(editor.Text, Math.Max(first, end - 1)) - first));
            var format = new ParagraphFormat
            {
                Mask = NumberingMask | StartIndentMask,
                Numbering = next == 1 ? Bullets : next == 2 ? Numbers : (ushort)0,
                StartIndent = next is 1 or 2 ? (standard ? StandardIndent : CompactIndent) : 0,
                NumberingStart = 1,
                NumberingStyle = 0x0200,
                NumberingTab = standard ? StandardTab : CompactTab
            };
            SendParagraphFormat(editor.Handle, SetParagraphFormat, IntPtr.Zero, format);
        }
        editor.Select(start, end - start);
    }

    public static void SetListIndent(RichTextBox editor, bool compact)
    {
        string text = editor.Text;
        int start = editor.SelectionStart;
        int length = editor.SelectionLength;
        int last = LineEnd(text, Math.Max(start, start + length - 1));
        var indent = new ParagraphFormat
        {
            Mask = StartIndentMask | NumberingTabMask,
            StartIndent = compact ? CompactIndent : StandardIndent,
            NumberingTab = compact ? CompactTab : StandardTab
        };
        for (int line = LineStart(text, start); line <= last; line = LineEnd(text, line) + 1)
        {
            editor.Select(line, 0);
            if (ReadFormat(editor).Numbering is Bullets or Numbers)
                SendParagraphFormat(editor.Handle, SetParagraphFormat, IntPtr.Zero, indent);
        }
        editor.Select(start, length);
    }

    public static void MakeFreeFormList(RichTextBox editor, int style)
    {
        string text = editor.Text;
        int start = editor.SelectionStart;
        int length = editor.SelectionLength;
        int first = LineStart(text, start);
        int last = LineEnd(text, Math.Max(start, start + length - 1));
        var lines = new List<(int Offset, int PrefixLength)>();
        for (int line = first; line <= last; line = LineEnd(text, line) + 1)
        {
            int end = LineEnd(text, line);
            ReadOnlySpan<char> contents = text.AsSpan(line, end - line);
            if (DividerStyle(contents) > 0) continue;
            int prefix = FreeFormPrefixLength(contents);
            lines.Add((line, prefix));
        }
        int finalEnd = last;
        var plain = new ParagraphFormat { Mask = NumberingMask | StartIndentMask };
        for (int index = lines.Count - 1; index >= 0; index--)
        {
            (int line, int prefixLength) = lines[index];
            editor.Select(line, 0);
            if (ReadFormat(editor).Numbering is Bullets or Numbers)
                SendParagraphFormat(editor.Handle, SetParagraphFormat, IntPtr.Zero, plain);
            string prefix = style switch { 1 => "• ", 2 => FormattableString.Invariant($"{index + 1}. "), _ => "- " };
            editor.Select(line, prefixLength);
            NoteLinks.InsertPlainText(editor, prefix);
            finalEnd += prefix.Length - prefixLength;
        }
        if (length != 0) editor.Select(first, finalEnd - first);
        else if (lines.Count != 0)
        {
            int prefixLength = FreeFormPrefixLength(editor.Text.AsSpan(first));
            editor.Select(Math.Clamp(start - lines[0].PrefixLength, first, last) + prefixLength, 0);
        }
    }

    private static int FreeFormPrefixLength(ReadOnlySpan<char> line)
    {
        if (line.StartsWith("• ") || line.StartsWith("- ") || line.StartsWith(Dash)) return 2;
        int digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;
        return digits > 0 && line[digits..].StartsWith(". ") ? digits + 2 : 0;
    }

    private static ParagraphFormat ReadFormat(RichTextBox editor)
    {
        var format = new ParagraphFormat();
        SendParagraphFormat(editor.Handle, GetParagraphFormat, IntPtr.Zero, format);
        return format;
    }

    public static bool HandleDashEnter(RichTextBox editor)
    {
        if (editor.SelectionLength != 0) return false;
        string text = editor.Text;
        int caret = editor.SelectionStart;
        int start = LineStart(text, caret);
        if (!HasDash(text, start) || caret < start + Dash.Length) return false;

        if (text.AsSpan(start + Dash.Length, LineEnd(text, caret) - start - Dash.Length).IsWhiteSpace())
        {
            editor.Select(start, Dash.Length);
            editor.SelectedText = "";
        }
        else NoteLinks.InsertPlainText(editor, "\n" + Dash);
        return true;
    }

    public static void CycleDivider(RichTextBox editor)
    {
        string text = editor.Text;
        int caret = editor.SelectionStart;
        int line = LineStart(text, caret);
        int end = LineEnd(text, caret);
        int style = DividerStyle(text.AsSpan(line, end - line));
        if (style == 3)
        {
            editor.Select(line, end - line + (end < text.Length ? 1 : 0));
            editor.SelectedText = "";
            editor.Select(line, 0);
            return;
        }

        if (style > 0)
        {
            editor.Select(line, end - line);
            editor.SelectedText = new string(text[line], style + 1);
        }
        else
        {
            // Insert between paragraphs so existing text and links stay intact.
            string leadingBreak = "";
            if (caret != line)
            {
                line = end + 1;
                if (end == text.Length) leadingBreak = "\n";
            }
            editor.Select(line - leadingBreak.Length, 0);
            NoteLinks.InsertPlainText(editor, leadingBreak + DividerMarker + "\n");
        }
        editor.Select(line, 0);
    }

    internal static int DividerStyle(ReadOnlySpan<char> line) =>
        line.Length is >= 1 and <= 3 && DividerMarkers.Contains(line[0]) && line.IndexOfAnyExcept(line[0]) < 0 ? line.Length : 0;

    internal static int DividerFlags(char marker) => DividerMarkers.IndexOf(marker);

    internal static char StyledDividerMarker(int flags) => DividerMarkers[flags];

    internal static string RemoveDividerMarkers(string text) =>
        text.Replace("\u2063", "").Replace("\u2062", "").Replace("\u2064", "").Replace("\u2061", "");

    private static bool HasDash(string text, int start) => text.AsSpan(start).StartsWith(Dash, StringComparison.Ordinal);
    private static int LineStart(string text, int position) => position == 0 ? 0 : text.LastIndexOf('\n', position - 1) + 1;
    private static int LineEnd(string text, int position)
    {
        int next = text.IndexOf('\n', position);
        return next < 0 ? text.Length : next;
    }

    [StructLayout(LayoutKind.Explicit, Size = 188)]
    private sealed class ParagraphFormat
    {
        [FieldOffset(0)] public uint Size = 188;
        [FieldOffset(4)] public uint Mask;
        [FieldOffset(8)] public ushort Numbering;
        [FieldOffset(12)] public int StartIndent;
        [FieldOffset(176)] public ushort NumberingStart;
        [FieldOffset(178)] public ushort NumberingStyle;
        [FieldOffset(180)] public ushort NumberingTab;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendParagraphFormat(IntPtr window, uint message, IntPtr wParam, [In, Out] ParagraphFormat format);
}
