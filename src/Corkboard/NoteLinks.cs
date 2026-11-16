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

    public static void InsertPlainText(RichTextBox editor, string text)
    {
        // Plain formatting keeps inserts at a link boundary outside the native hyperlink field.
        editor.SelectedRtf = @"{\rtf1\ansi\uc1\plain " + Escape(text).Replace("\n", @"\par ") + "}";
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
                // RichEdit expands a selection in the hidden field instruction to cover its whole link.
                editor.Select(field + 1, 1);
                if (editor.SelectionLength <= 1) continue;
                int linkStart = editor.SelectionStart;
                int linkEnd = linkStart + editor.SelectionLength;
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
    private const int MouseMoveMessage = 0x0200, SetCursor = 0x0020, ReflectedNotify = 0x204E;
    private const uint LinkNotification = 0x070B, SetEditStyle = 0x04CC;
    private const int NotifyUnfocusedLinks = 32;
    private bool overLink;
    private readonly List<(int Position, int Style, int Flags)> dividers = [];

    protected override void OnTextChanged(EventArgs e)
    {
        dividers.Clear();
        string text = Text;
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
        base.OnSelectionChanged(e);
        if (dividers.Count > 0) Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
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
        const int Paint = 0x000F, PrintClient = 0x0318;
        const int LeftDown = 0x0201, Character = 0x0102, Paste = 0x0302;
        if (message.Msg == LeftDown && dividers.Count > 0)
        {
            int y = (short)(message.LParam.ToInt64() >> 16);
            foreach (var divider in dividers)
            {
                int top = GetPositionFromCharIndex(divider.Position).Y;
                if (top < 0 || y < top || y >= top + Font.Height) continue;
                Focus();
                Select(divider.Position, divider.Style);
                message.Result = IntPtr.Zero;
                return;
            }
        }
        if (message.Msg is Character or Paste)
        {
            int key = (int)message.WParam;
            if (message.Msg == Paste || key >= ' ' || key is '\r' or '\n' or '\t')
                MovePastDivider();
        }
        if ((message.Msg == Paint || message.Msg == PrintClient) && dividers.Count > 0)
        {
            base.WndProc(ref message);
            using Graphics graphics = message.Msg == PrintClient && message.WParam != IntPtr.Zero
                ? Graphics.FromHdc(message.WParam) : CreateGraphics();
            DrawDividers(graphics);
            return;
        }
        if (message.Msg == MouseMoveMessage)
        {
            bool wasOverLink = overLink;
            overLink = false;
            base.WndProc(ref message);
            if (overLink && MouseButtons == MouseButtons.None) Cursor.Current = Cursors.Hand;
            else if (wasOverLink && !overLink) Cursor.Current = Cursor;
            return;
        }
        if (message.Msg == ReflectedNotify && message.LParam != IntPtr.Zero
            && Marshal.PtrToStructure<NotificationHeader>(message.LParam).Code == LinkNotification)
        {
            var notification = Marshal.PtrToStructure<EditorNotification>(message.LParam);
            if (notification.Message == MouseMoveMessage) overLink = true;
            if (notification.Message == SetCursor && MouseButtons == MouseButtons.None)
            {
                overLink = true;
                Cursor.Current = Cursors.Hand;
                message.Result = new IntPtr(1);
                return;
            }
        }
        base.WndProc(ref message);
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NotificationHeader
    {
        public IntPtr Window, Id;
        public uint Code;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EditorNotification
    {
        public NotificationHeader Header;
        public uint Message;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
