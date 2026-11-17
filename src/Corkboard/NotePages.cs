using System.Drawing.Drawing2D;

namespace Corkboard;

internal sealed class NotePages
{
    private readonly NoteTextEditor editor;
    private readonly Panel host;
    private Panel? footer;
    private Button? previous;
    private Button? next;
    private Label? count;
    private Label? corner;
    private readonly List<int> starts = [];
    private bool enabled;
    private bool queued;
    private bool dirty;
    private bool trackCaret;
    private bool moving;
    private int current;
    private Color paper;
    private Color accent;

    public NotePages(NoteTextEditor editor, Panel host)
    {
        this.editor = editor;
        this.host = host;
    }

    public void SetEnabled(bool value)
    {
        if (enabled == value) return;
        enabled = value;
        if (value)
        {
            EnsureFooter();
            editor.TextChanged += TextChanged;
            editor.SizeChanged += TextChanged;
            editor.SelectionChanged += SelectionChanged;
            editor.HandleCreated += TextChanged;
            editor.TurnPage = Move;
            Queue(reflow: true, followCaret: true);
        }
        else
        {
            editor.TextChanged -= TextChanged;
            editor.SizeChanged -= TextChanged;
            editor.SelectionChanged -= SelectionChanged;
            editor.HandleCreated -= TextChanged;
            editor.TurnPage = null;
            editor.PageBottom = null;
            footer!.Visible = false;
            editor.ScrollToCaret();
        }
    }

    public void ApplyColor(Color background, Color highlight)
    {
        paper = background;
        accent = highlight;
        if (footer is null) return;
        footer.BackColor = paper;
        count!.BackColor = count.Parent!.BackColor = previous!.BackColor = next!.BackColor = accent;
        corner!.Invalidate();
    }

    public void Reflow() => Queue(reflow: true, followCaret: true);

    private void EnsureFooter()
    {
        if (footer is not null) return;
        var bar = new Panel { Dock = DockStyle.Bottom, Height = host.LogicalToDeviceUnits(27), Visible = false };
        footer = bar;
        var navigation = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, Width = host.LogicalToDeviceUnits(110), WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(2)
        };
        navigation.Resize += (_, _) => BoardStyle.RoundCorners(navigation, navigation.LogicalToDeviceUnits(10));
        navigation.VisibleChanged += (_, _) => BoardStyle.RoundCorners(navigation, navigation.LogicalToDeviceUnits(10));
        next = PageButton("›", "Next note page", 1);
        previous = PageButton("‹", "Previous note page", -1);
        count = new Label
        {
            Width = host.LogicalToDeviceUnits(58), Height = next.Height, TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = "Note page", ForeColor = BoardStyle.Ink, Margin = Padding.Empty
        };
        navigation.Controls.AddRange([next, count, previous]);
        corner = new Label
        {
            Dock = DockStyle.Right, Width = host.LogicalToDeviceUnits(26), Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = BoardStyle.Muted, AccessibleName = "More note pages"
        };
        corner.Paint += (_, e) =>
        {
            float unit = corner.DeviceDpi / 96f;
            float x = corner.Width - 4 * unit, y = corner.Height - 4 * unit;
            using var fold = new GraphicsPath();
            fold.AddBezier(x - 17 * unit, y, x - 6 * unit, y - 2 * unit, x - 3 * unit, y - 7 * unit, x, y - 17 * unit);
            fold.AddLine(x, y - 17 * unit, x, y - 4 * unit);
            fold.AddBezier(x, y - 4 * unit, x, y - unit, x - unit, y, x - 4 * unit, y);
            fold.CloseFigure();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(accent);
            using var crease = new Pen(Color.FromArgb(120, BoardStyle.Muted), unit);
            e.Graphics.FillPath(fill, fold);
            e.Graphics.DrawBezier(crease, x - 17 * unit, y, x - 6 * unit, y - 2 * unit, x - 3 * unit, y - 7 * unit, x, y - 17 * unit);
        };
        bar.Controls.Add(navigation);
        bar.Controls.Add(corner);
        navigation.Visible = false;
        bar.MouseEnter += (_, _) => Reveal();
        corner.MouseEnter += (_, _) => Reveal();
        corner.Click += (_, _) => Reveal();
        foreach (Control control in new Control[] { bar, navigation, corner, previous, count, next })
            control.MouseLeave += (_, _) =>
            {
                if (!bar.ClientRectangle.Contains(bar.PointToClient(Cursor.Position)) && !bar.ContainsFocus)
                {
                    navigation.Hide();
                    corner!.Show();
                }
            };
        bar.Leave += (_, _) =>
        {
            if (bar.ClientRectangle.Contains(bar.PointToClient(Cursor.Position))) return;
            navigation.Hide();
            corner!.Show();
        };
        host.Controls.Add(bar);
        ApplyColor(paper, accent);

        Button PageButton(string caption, string name, int step)
        {
            Button button = BoardStyle.Button(caption, accent, BoardStyle.Ink);
            button.Size = host.LogicalToDeviceUnits(new Size(24, 23));
            button.AccessibleName = name;
            button.Click += (_, _) => Move(step);
            return button;
        }

        void Reveal()
        {
            navigation.Show();
            corner!.Hide();
        }
    }

    private void TextChanged(object? sender, EventArgs e) => Reflow();
    private void SelectionChanged(object? sender, EventArgs e)
    {
        if (!moving) Queue(reflow: false, followCaret: true);
    }

    private void Queue(bool reflow, bool followCaret)
    {
        if (!enabled || !editor.IsHandleCreated || editor.IsDisposed) return;
        dirty |= reflow;
        trackCaret |= followCaret;
        if (queued) return;
        queued = true;
        editor.BeginInvoke(() =>
        {
            queued = false;
            if (enabled && !editor.IsDisposed) UpdatePages();
        });
    }

    private void UpdatePages()
    {
        Panel bar = footer!;
        Point offset = editor.ScrollOffset;
        if (dirty)
        {
            dirty = false;
            int lineCount = editor.NativeLineCount;
            int contentBottom = editor.ContentBottom;
            int fullHeight = editor.ClientSize.Height + (bar.Visible ? bar.Height : 0);
            bool overflow = contentBottom > fullHeight && fullHeight > editor.Font.Height;
            bar.Height = Math.Min(host.LogicalToDeviceUnits(27), Math.Max(0, fullHeight - editor.Font.Height));
            if (bar.Visible != overflow)
            {
                bar.Visible = overflow;
                offset = editor.ScrollOffset;
            }
            starts.Clear();
            starts.Add(0);
            if (overflow)
            {
                // Start each page on a wrapped display line, keeping the rich text in one editor.
                int pageHeight = Math.Max(1, editor.ClientSize.Height - editor.LogicalToDeviceUnits(2));
                int line = 1;
                while (line < lineCount)
                {
                    int lo = line, hi = lineCount;
                    int threshold = starts[^1] + pageHeight;
                    while (lo < hi)
                    {
                        int mid = lo + (hi - lo) / 2;
                        if (LineTop(mid, offset.Y) <= threshold) lo = mid + 1;
                        else hi = mid;
                    }
                    // The line just below the viewport may only have been partially visible.
                    line = Math.Max(line, lo - 1);
                    if (lo == lineCount && contentBottom <= threshold) break;
                    starts.Add(LineTop(line, offset.Y));
                    line++;
                }
            }
        }
        if (starts.Count == 0) return;
        if (trackCaret)
        {
            trackCaret = false;
            int caret = editor.GetPositionFromCharIndex(editor.SelectionStart).Y + offset.Y;
            int found = starts.BinarySearch(caret);
            current = Math.Max(0, found >= 0 ? found : ~found - 1);
        }
        GoTo(Math.Clamp(current, 0, starts.Count - 1));
    }

    private int LineTop(int line, int offset) =>
        editor.GetPositionFromCharIndex(editor.GetFirstCharIndexFromLine(line)).Y + offset - 1;

    private void Move(int delta)
    {
        if (starts.Count == 0) return;
        int target = Math.Clamp(current + delta, 0, starts.Count - 1);
        if (target == current) return;
        moving = true;
        try
        {
            GoTo(target);
            editor.Select(editor.GetCharIndexFromPosition(new Point(1, editor.LogicalToDeviceUnits(3))), 0);
            trackCaret = false;
            GoTo(target);
        }
        finally { moving = false; }
    }

    private void GoTo(int page)
    {
        current = page;
        count!.Text = $"{current + 1} / {starts.Count}";
        previous!.Enabled = current > 0;
        next!.Enabled = current < starts.Count - 1;
        editor.ScrollToOffset(starts[page]);
        editor.PageBottom = page + 1 < starts.Count ? starts[page + 1] - editor.ScrollOffset.Y + 1 : null;
        editor.Invalidate();
    }

}
