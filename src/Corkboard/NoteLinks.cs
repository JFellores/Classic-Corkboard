using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Corkboard;

internal static class NoteLinks
{
    public static Uri? ParseWebAddress(string text, bool allowBareHost = false)
    {
        string value = text.Trim();
        if (value.Length == 0 || value.Any(char.IsControl)) return null;
        if (allowBareHost && !value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http"
            && uri.Host.Length > 0 && uri.UserInfo.Length == 0 ? uri : null;
    }

    public static void Insert(RichTextBox editor, string alias, Uri destination, Color color)
    {
        string ink = FormattableString.Invariant($@"\red{color.R}\green{color.G}\blue{color.B};");
        editor.SelectedRtf = @"{\rtf1\ansi\uc1{\colortbl ;" + ink + @"}{\field{\*\fldinst{HYPERLINK """
            + Escape(destination.AbsoluteUri) + @"""}}{\fldrslt{\cf1 " + Escape(alias) + "}}}}";
    }

    public static string DisplayText(RichTextBox editor, string raw, string rtf) =>
        NoteParagraphs.RemoveDividerMarkers(
            rtf.Contains(@"\fldinst", StringComparison.Ordinal) ? ReadVisibleText(editor, raw.Length, selection: false) : raw);

    public static string SelectedDisplayText(RichTextBox editor) =>
        editor.SelectionLength == 0 ? "" : ReadVisibleText(editor, editor.SelectionLength, selection: true);

    public static void InsertPlainText(RichTextBox editor, string text, bool bold = false, Color? color = null)
    {
        // Plain formatting keeps inserts at a link boundary outside the native hyperlink field.
        using Font? selectionFont = editor.SelectionFont;
        Font font = selectionFont ?? editor.Font;
        string palette = color is Color ink
            ? FormattableString.Invariant($@"{{\colortbl ;\red{ink.R}\green{ink.G}\blue{ink.B};}}") : "";
        string face = FormattableString.Invariant($@"{{\fonttbl{{\f0 {Escape(font.Name)};}}}}\plain\f0\fs{Math.Round(font.SizeInPoints * 2)} ");
        editor.SelectedRtf = @"{\rtf1\ansi\uc1" + palette + face + (bold ? @"\b " : "")
            + (color.HasValue ? @"\cf1 " : "") + Escape(text).Replace("\n", @"\par ").Replace("\t", @"\tab ") + "}";
    }

    public static (int Start, int End) IncludeWholeLinks(RichTextBox editor, string text, int start, int end)
    {
        int selectionStart = editor.SelectionStart;
        int selectionLength = editor.SelectionLength;
        try
        {
            for (int field = text.IndexOf("HYPERLINK", StringComparison.OrdinalIgnoreCase);
                field >= 0 && field <= end;
                field = text.IndexOf("HYPERLINK", field + 1, StringComparison.OrdinalIgnoreCase))
            {
                if (!TrySelectLink(editor, field, out int linkStart, out int linkEnd)) continue;
                bool overlaps = start == end ? linkStart <= start && start < linkEnd : start < linkEnd && linkStart < end;
                if (!overlaps) continue;
                start = Math.Min(start, linkStart);
                end = Math.Max(end, linkEnd);
                field = linkEnd - 1;
            }
            return (start, end);
        }
        finally { editor.Select(selectionStart, selectionLength); }
    }

    internal static List<(int Start, int End)> FindVisibleLinks(RichTextBox editor)
    {
        var links = new List<(int, int)>();
        string text = editor.Text;
        int field = text.IndexOf("HYPERLINK", StringComparison.OrdinalIgnoreCase);
        if (field < 0) return links;

        int selectionStart = editor.SelectionStart;
        int selectionLength = editor.SelectionLength;
        try
        {
            for (; field >= 0; field = text.IndexOf("HYPERLINK", field + 1, StringComparison.OrdinalIgnoreCase))
            {
                if (!TrySelectLink(editor, field, out int start, out int end)) continue;
                int visibleLength = SelectedDisplayText(editor).Length;
                if (visibleLength > 0 && visibleLength <= end - start)
                    links.Add((end - visibleLength, end));
                field = end - 1;
            }
        }
        finally { editor.Select(selectionStart, selectionLength); }
        return links;
    }

    private static bool TrySelectLink(RichTextBox editor, int field, out int start, out int end)
    {
        // RichEdit expands a selection in a hidden instruction to cover the whole hyperlink.
        editor.Select(field + 1, 1);
        start = editor.SelectionStart;
        end = start + editor.SelectionLength;
        return end - start > 1;
    }

    private static string Escape(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch is '\\' or '{' or '}') result.Append('\\').Append(ch);
            else if (ch > 127) result.Append(@"\u").Append(((short)ch).ToString(CultureInfo.InvariantCulture)).Append('?');
            else result.Append(ch);
        }
        return result.ToString();
    }

    private static string ReadVisibleText(RichTextBox editor, int length, bool selection)
    {
        const uint NoHiddenText = 8, Selection = 2;
        const uint GetTextEx = 0x045E, Unicode = 1200;
        var output = new StringBuilder(length + 1);
        var options = new TextOptions
        {
            Bytes = (uint)(output.Capacity * sizeof(char)),
            Flags = NoHiddenText | (selection ? Selection : 0),
            CodePage = Unicode
        };
        SendText(editor.Handle, GetTextEx, ref options, output);
        return output.ToString().Replace('\r', '\n');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextOptions
    {
        public uint Bytes, Flags, CodePage;
        public IntPtr DefaultCharacter, UsedDefaultCharacter;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendText(IntPtr window, uint message, ref TextOptions options, StringBuilder output);
}

internal sealed class NoteTextEditor : RichTextBox
{
    internal Action<int>? TurnPage { get; set; }
    internal Action<int>? ScrollNote { get; set; }
    internal Func<Point, bool>? IsOverChecklist { get; set; }
    internal int? PageBottom { get; set; }
    internal Point ScrollOffset
    {
        get
        {
            Point point = default;
            SendPoint(Handle, 0x04DD, IntPtr.Zero, ref point);
            return point;
        }
    }

    internal int NativeLineCount => (int)SendMessage(Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero);

    internal int ContentBottom
    {
        get
        {
            int last = GetFirstCharIndexFromLine(Math.Max(0, NativeLineCount - 1));
            return (last >= 0 ? GetPositionFromCharIndex(last).Y + ScrollOffset.Y : 0) + Font.Height;
        }
    }

    internal void ScrollToOffset(int y)
    {
        Point point = ScrollOffset;
        if (point.Y == y) return;
        point.Y = y;
        SendPoint(Handle, 0x04DE, IntPtr.Zero, ref point);
        Invalidate();
    }
    private const int MouseMoveMessage = 0x0200, SetCursor = 0x0020;
    private const uint SetEditStyle = 0x04CC;
    private const int NotifyUnfocusedLinks = 32;
    private bool readingLinks;
    private bool hasChecklist;
    private List<(int Start, int End)>? visibleLinks;
    private readonly List<(int Position, int Style, int Flags)> dividers = [];

    protected override void OnTextChanged(EventArgs e)
    {
        visibleLinks = null;
        dividers.Clear();
        string text = Text;
        hasChecklist = text.AsSpan().IndexOfAny('☐', '☑') >= 0;
        for (int index = 0; index < text.Length;)
        {
            int end = text.IndexOf('\n', index);
            if (end < 0) end = text.Length;
            int style = NoteParagraphs.DividerStyle(text.AsSpan(index, end - index));
            if (style > 0) dividers.Add((index, style, NoteParagraphs.DividerFlags(text[index])));
            index = end + 1;
        }
        base.OnTextChanged(e);
        Invalidate();
    }

    internal bool TryGetDivider(out (int Position, int Style, int Flags) divider)
    {
        int start = SelectionStart;
        int end = start + SelectionLength;
        foreach (var item in dividers)
        {
            if (start < item.Position || end > item.Position + item.Style) continue;
            divider = item;
            return true;
        }
        divider = default;
        return false;
    }

    internal void ToggleDividerFormat(int flag)
    {
        if (!TryGetDivider(out var divider)) return;
        Select(divider.Position, divider.Style);
        SelectedText = new string(NoteParagraphs.StyledDividerMarker(divider.Flags ^ flag), divider.Style);
        Select(divider.Position, divider.Style);
    }

    protected override void OnSelectionChanged(EventArgs e)
    {
        if (readingLinks) return;
        base.OnSelectionChanged(e);
        if (dividers.Count > 0) Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        visibleLinks = null;
        base.OnHandleCreated(e);
        SendMessage(Handle, SetEditStyle, new IntPtr(NotifyUnfocusedLinks), new IntPtr(NotifyUnfocusedLinks));
    }

    internal void MovePastDivider()
    {
        if (!TryGetDivider(out var selected)) return;
        int next = selected.Position + selected.Style;
        if (next == TextLength)
        {
            Select(next, 0);
            NoteLinks.InsertPlainText(this, "\n");
        }
        Select(next + 1, 0);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x020A && (TurnPage is not null || ScrollNote is not null))
        {
            int delta = (short)(message.WParam.ToInt64() >> 16);
            if (delta != 0)
            {
                if (TurnPage is { } turnPage) turnPage(delta > 0 ? -1 : 1);
                else ScrollNote?.Invoke(delta);
            }
            return;
        }
        const int Paint = 0x000F, PrintClient = 0x0318;
        const int LeftDown = 0x0201, Character = 0x0102, Paste = 0x0302;
        if (message.Msg == LeftDown && DividerAt(ClientPoint(message.LParam).Y) is { } divider)
        {
            Focus();
            Select(divider.Position, divider.Style);
            message.Result = IntPtr.Zero;
            return;
        }
        if (message.Msg is Character or Paste)
        {
            int key = (int)message.WParam;
            if (message.Msg == Paste || key >= ' ' || key is '\r' or '\n' or '\t')
                MovePastDivider();
        }
        if ((message.Msg == Paint || message.Msg == PrintClient) && (dividers.Count > 0 || PageBottom.HasValue))
        {
            base.WndProc(ref message);
            using Graphics graphics = message.Msg == PrintClient && message.WParam != IntPtr.Zero
                ? Graphics.FromHdc(message.WParam) : CreateGraphics();
            DrawDividers(graphics);
            if (PageBottom is int bottom && bottom >= 0 && bottom < ClientSize.Height)
            {
                using var background = new SolidBrush(BackColor);
                graphics.FillRectangle(background, 0, bottom, ClientSize.Width, ClientSize.Height - bottom);
            }
            return;
        }
        if (message.Msg == SetCursor && message.WParam == Handle && (short)message.LParam.ToInt64() == 1)
        {
            Cursor.Current = PointerAt(PointToClient(Cursor.Position), MouseButtons != MouseButtons.None);
            message.Result = new IntPtr(1);
            return;
        }

        base.WndProc(ref message);
        if (message.Msg == MouseMoveMessage)
        {
            const int buttonFlags = 0x0073;
            Cursor.Current = PointerAt(ClientPoint(message.LParam), (message.WParam.ToInt64() & buttonFlags) != 0);
        }
    }

    private Cursor PointerAt(Point point, bool dragging)
    {
        if (dragging) return Cursors.IBeam;
        if (!ClientRectangle.Contains(point) || PageBottom is int bottom && point.Y >= bottom) return Cursors.Default;
        return (hasChecklist && IsOverChecklist?.Invoke(point) == true) || DividerAt(point.Y).HasValue || IsOverLink(point)
            ? Cursors.Hand : Cursors.IBeam;
    }

    private (int Position, int Style, int Flags)? DividerAt(int y)
    {
        foreach (var item in dividers)
        {
            int top = GetPositionFromCharIndex(item.Position).Y;
            if (top >= 0 && y >= top && y < top + Font.Height) return item;
        }
        return null;
    }

    private bool IsOverLink(Point point)
    {
        if (visibleLinks is null)
        {
            readingLinks = true;
            try { visibleLinks = NoteLinks.FindVisibleLinks(this); }
            finally { readingLinks = false; }
        }
        if (visibleLinks.Count == 0) return false;

        int index = GetCharIndexFromPosition(point);
        int line = GetLineFromCharIndex(index);
        int lineStart = GetFirstCharIndexFromLine(line);
        if (lineStart < 0) return false;
        int nextLine = GetFirstCharIndexFromLine(line + 1);
        foreach ((int start, int end) in visibleLinks)
        {
            int first = Math.Max(start, lineStart);
            int limit = nextLine >= 0 ? Math.Min(end, nextLine) : end;
            if (first >= limit) continue;

            Point lower = GetPositionFromCharIndex(first);
            Point upper = GetPositionFromCharIndex(limit);
            int bottom = nextLine >= 0 ? GetPositionFromCharIndex(nextLine).Y : lower.Y + Font.Height;
            if (point.Y < lower.Y || point.Y >= bottom || point.X < lower.X) continue;

            // At a wrap, the range end belongs to the following visual line.
            if (upper.Y != lower.Y || upper.X <= lower.X)
            {
                Point last = GetPositionFromCharIndex(limit - 1);
                int width = TextRenderer.MeasureText(Text.Substring(limit - 1, 1), Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                upper.X = last.X + width;
            }
            if (point.X < upper.X) return true;
        }
        return false;
    }

    private static Point ClientPoint(IntPtr coordinates) =>
        new((short)coordinates.ToInt64(), (short)(coordinates.ToInt64() >> 16));

    private void DrawDividers(Graphics graphics)
    {
        using var pen = new Pen(BoardStyle.Muted, LogicalToDeviceUnits(1));
        int inset = LogicalToDeviceUnits(2);
        int right = ClientSize.Width - inset;
        foreach ((int position, int style, int flags) in dividers)
        {
            int top = GetPositionFromCharIndex(position).Y;
            int y = top + Font.Height / 2;
            if (y < 0 || y >= ClientSize.Height || right <= inset) continue;
            if (Focused && SelectionLength > 0 && SelectionStart <= position && SelectionStart + SelectionLength >= position + style)
            {
                using var highlight = new SolidBrush(Color.FromArgb(40, BoardStyle.Brick));
                graphics.FillRectangle(highlight, inset, top, right - inset, Font.Height);
            }
            pen.Width = LogicalToDeviceUnits((flags & 1) != 0 ? 2 : 1);
            pen.DashStyle = style == 2 ? DashStyle.Dash : DashStyle.Solid;
            graphics.DrawLine(pen, inset, y, right, y);
            if (style == 3) graphics.DrawLine(pen, inset, y + LogicalToDeviceUnits(3), right, y + LogicalToDeviceUnits(3));
            if ((flags & 2) != 0)
            {
                pen.DashStyle = DashStyle.Solid;
                pen.Width = LogicalToDeviceUnits(1);
                int underline = y + LogicalToDeviceUnits(style == 3 ? 6 : 3);
                graphics.DrawLine(pen, inset, underline, right, underline);
            }
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendPoint(IntPtr window, uint message, IntPtr wParam, ref Point point);
}
