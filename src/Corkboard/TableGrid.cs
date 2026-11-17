using System.Runtime.InteropServices;

namespace Corkboard;

internal sealed class TableGrid : UserControl
{
    private readonly TableData table;
    private readonly Action changed;
    private readonly Func<IDisposable> beginEdit;
    private readonly CellGrid grid;
    private readonly Button addRow;
    private readonly Button deleteRow;
    private readonly Button more;
    private readonly Panel commands;
    private readonly CorkMenu menu = new();
    private readonly ToolTip tips = new();
    private string[,] display = new string[0, 0];
    private bool refreshing;
    private bool selecting;
    private Point? dragAnchor;
    private Point? dragEnd;
    private Point? referenceAnchor;
    private Rectangle? referenceArea;
    private string? pickedFormula;
    private int referenceStart;
    private int referenceLength;

    public TableGrid(TableData table, Action changed, Func<IDisposable> beginEdit)
    {
        this.table = table;
        this.changed = changed;
        this.beginEdit = beginEdit;
        AccessibleName = "Table card controls";
        Dock = DockStyle.Fill;

        grid = new CellGrid(HandleGridKey, HandleReferencePointer)
        {
            AccessibleName = "Table cells",
            AccessibleDescription = "Drag or hold Shift to select cells. While typing a formula, click or drag cells to insert their reference.",
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            BackgroundColor = BoardStyle.Paper, GridColor = BoardStyle.PanelTint,
            EnableHeadersVisualStyles = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, AllowUserToResizeColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = true,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable,
            StandardTab = false, ShowCellErrors = false, ShowRowErrors = false,
            RowHeadersWidth = 36, RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing,
            RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            ColumnHeadersHeight = 27, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        };
        grid.RowTemplate.Height = 27;
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White, ForeColor = BoardStyle.Ink,
            SelectionBackColor = BoardStyle.PanelTint, SelectionForeColor = BoardStyle.Ink,
            Padding = new Padding(4, 0, 4, 0), NullValue = ""
        };
        grid.AlternatingRowsDefaultCellStyle.BackColor = BoardStyle.Paper;
        grid.RowHeadersDefaultCellStyle.ForeColor = BoardStyle.Muted;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = BoardStyle.Ink;
        grid.RowHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = BoardStyle.PanelTint;
        grid.RowHeadersDefaultCellStyle.SelectionBackColor = BoardStyle.PanelTint;
        grid.RowHeadersDefaultCellStyle.SelectionForeColor = BoardStyle.Ink;
        grid.CellFormatting += FormatCell;
        grid.CellPainting += PaintSelection;
        grid.EditingControlShowing += ShowRawValue;
        grid.CellEndEdit += (_, _) => ClearPickedReference();
        grid.MouseCaptureChanged += (_, _) => { if (!grid.Capture) referenceAnchor = null; };
        grid.CellValueChanged += SaveCell;
        grid.SelectionChanged += (_, _) => { if (!refreshing && !selecting) RefreshSelection(); };
        grid.RowHeaderMouseClick += (_, e) =>
            SelectRange(new Rectangle(0, e.RowIndex, table.ColumnCount, 1), moveCurrent: true);
        grid.ColumnHeaderMouseClick += (_, e) =>
            SelectRange(new Rectangle(e.ColumnIndex, 0, 1, table.Rows.Count), moveCurrent: true);
        grid.MouseDown += StartSelectionDrag;
        grid.MouseMove += ContinueSelectionDrag;
        grid.MouseMove += (_, e) => UpdateGridCursor(e.Location);
        grid.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && dragAnchor is Point start && dragEnd is Point end)
                SelectRange(RangeBetween(start, end));
            dragAnchor = dragEnd = null;
        };

        commands = new Panel { Dock = DockStyle.Top, Height = 35 };
        addRow = Command("+ Add row", "Add row", 0, 88, () => Append(column: false));
        deleteRow = Command("Delete row", "Delete selected row", 93, 94, () => Remove(column: false));
        deleteRow.ForeColor = BoardStyle.Brick;
        more = Command("⋯", "Table options and formulas", 192, 32,
            () => menu.Show(more!, new Point(0, more!.Height)));
        ToolStripItem addColumn = menu.Items.Add("Add column", null, (_, _) => Append(column: true));
        ToolStripItem deleteColumn = menu.Items.Add("Delete selected column", null, (_, _) => Remove(column: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Copy", null, (_, _) => CopySelection(cut: false)) { ShortcutKeyDisplayString = "Ctrl+C" });
        menu.Items.Add(new ToolStripMenuItem("Cut", null, (_, _) => CopySelection(cut: true)) { ShortcutKeyDisplayString = "Ctrl+X" });
        menu.Items.Add(new ToolStripMenuItem("Paste", null, (_, _) => PasteSelection()) { ShortcutKeyDisplayString = "Ctrl+V" });
        menu.Items.Add(new ToolStripMenuItem("Clear cells", null, (_, _) => ClearSelection()) { ShortcutKeyDisplayString = "Del" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Formula examples…", null, (_, _) => ShowFormulaHelp());
        menu.Opening += (_, _) =>
        {
            addColumn.Enabled = table.CanAdd(column: true);
            int columns = SelectedIndices(column: true).Length;
            deleteColumn.Enabled = columns > 0 && columns < table.ColumnCount;
            deleteColumn.Text = columns > 1 ? "Delete selected columns" : "Delete selected column";
        };
        grid.ContextMenuStrip = menu;
        Controls.Add(grid);
        Controls.Add(commands);
        Reload();
    }

    public void FocusCells() => grid.Focus();
    public void CommitEdit()
    {
        if (grid.IsCurrentCellInEditMode) grid.EndEdit();
    }

    public void ApplyColor(Color paper, Color edge)
    {
        commands.BackColor = paper;
        grid.BackgroundColor = paper;
        grid.RowHeadersDefaultCellStyle.BackColor = grid.ColumnHeadersDefaultCellStyle.BackColor = edge;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb((edge.R * 3 + 255) / 4, (edge.G * 3 + 255) / 4, (edge.B * 3 + 255) / 4);
        addRow.BackColor = edge;
        deleteRow.BackColor = more.BackColor = paper;
        menu.SetSurface(paper);
    }

    public void Reload()
    {
        refreshing = true;
        try
        {
            display = table.Calculate();
            while (grid.Columns.Count > table.ColumnCount) grid.Columns.RemoveAt(grid.Columns.Count - 1);
            while (grid.Columns.Count < table.ColumnCount)
            {
                grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = ((char)('A' + grid.Columns.Count)).ToString(),
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                    MinimumWidth = 54, MaxInputLength = TableData.MaximumCellLength
                });
            }
            if (grid.Rows.Count != table.Rows.Count) grid.RowCount = table.Rows.Count;
            for (int row = 0; row < table.Rows.Count; row++)
            {
                grid.Rows[row].HeaderCell.Value = (row + 1).ToString();
                for (int column = 0; column < table.ColumnCount; column++)
                    grid[column, row].Value = table.Rows[row][column];
            }
        }
        finally { refreshing = false; }
        RefreshSelection();
    }

    private Button Command(string text, string label, int left, int width, Action action)
    {
        Button button = BoardStyle.Button(text, BoardStyle.Paper, BoardStyle.Ink, compactText: true);
        button.Bounds = new Rectangle(left, 0, width, 28);
        button.AccessibleName = label;
        tips.SetToolTip(button, label);
        button.Click += (_, _) => action();
        commands.Controls.Add(button);
        return button;
    }

    private void RefreshSelection()
    {
        grid.Invalidate();
        addRow.Enabled = table.CanAdd(column: false);
        int rows = SelectedIndices(column: false).Length;
        deleteRow.Text = rows > 1 ? "Delete rows" : "Delete row";
        deleteRow.AccessibleName = rows > 1 ? "Delete selected rows" : "Delete selected row";
        deleteRow.Enabled = rows > 0 && rows < table.Rows.Count;
        tips.SetToolTip(addRow, addRow.Enabled ? "Add row" : $"Up to {TableData.MaximumRows} rows per table");
    }

    private void FormatCell(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.RowIndex >= display.GetLength(0) || e.ColumnIndex >= display.GetLength(1))
            return;
        if (e.CellStyle is not { } style) return;
        if (referenceArea?.Contains(e.ColumnIndex, e.RowIndex) == true)
            style.BackColor = grid.DefaultCellStyle.SelectionBackColor;
        if (e.Value is not string raw) return;
        bool formula = raw.TrimStart().StartsWith('=');
        if (formula)
        {
            e.Value = display[e.RowIndex, e.ColumnIndex];
            e.FormattingApplied = true;
        }
        if (e.Value is not string value) return;
        if (TableData.TryReadNumber(value, out _)) style.Alignment = DataGridViewContentAlignment.MiddleRight;
        if (formula && value.StartsWith('#')) style.ForeColor = BoardStyle.Brick;
    }

    private void PaintSelection(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.Graphics is null) return;
        bool rowHeader = e.ColumnIndex == -1 && e.RowIndex >= 0;
        bool columnHeader = e.RowIndex == -1 && e.ColumnIndex >= 0;
        if (rowHeader || columnHeader)
        {
            bool selected = rowHeader
                ? grid.Rows[e.RowIndex].Cells.Cast<DataGridViewCell>().Any(cell => cell.Selected)
                : grid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells[e.ColumnIndex].Selected);
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border);
            string text = rowHeader ? (e.RowIndex + 1).ToString() : ((char)('A' + e.ColumnIndex)).ToString();
            TextRenderer.DrawText(e.Graphics, text, grid.Font, e.CellBounds, selected ? BoardStyle.Brick : BoardStyle.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        else if (referenceArea is Rectangle range && range.Contains(e.ColumnIndex, e.RowIndex))
        {
            e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.Focus);
            Rectangle bounds = Rectangle.Inflate(e.CellBounds, -1, -1);
            using var pen = new Pen(BoardStyle.Brick, grid.LogicalToDeviceUnits(2));
            if (e.ColumnIndex == range.Left) e.Graphics.DrawLine(pen, bounds.Left, bounds.Top, bounds.Left, bounds.Bottom);
            if (e.ColumnIndex == range.Right - 1) e.Graphics.DrawLine(pen, bounds.Right, bounds.Top, bounds.Right, bounds.Bottom);
            if (e.RowIndex == range.Top) e.Graphics.DrawLine(pen, bounds.Left, bounds.Top, bounds.Right, bounds.Top);
            if (e.RowIndex == range.Bottom - 1) e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
        }
        else
        {
            if (grid.IsCurrentCellInEditMode || grid.CurrentCell is not { Selected: true } current
                || current.RowIndex != e.RowIndex || current.ColumnIndex != e.ColumnIndex) return;
            e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.Focus);
            using var pen = new Pen(BoardStyle.Brick, grid.LogicalToDeviceUnits(2));
            e.Graphics.DrawRectangle(pen, Rectangle.Inflate(e.CellBounds, -1, -1));
        }
        e.Handled = true;
    }

    private void ShowRawValue(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        if (e.Control is not DataGridViewTextBoxEditingControl input) return;
        input.TextChanged -= FormulaTextChanged;
        input.TextChanged += FormulaTextChanged;
        input.KeyPress -= FinishPickedParenthesis;
        input.KeyPress += FinishPickedParenthesis;
        if (grid.CurrentCell?.Value is not string raw || !raw.TrimStart().StartsWith('=')) return;
        input.Text = raw;
        input.SelectionStart = input.TextLength;
        input.EditingControlValueChanged = false;
        grid.NotifyCurrentCellDirty(false);
    }

    private void FinishPickedParenthesis(object? sender, KeyPressEventArgs e)
    {
        if (e.KeyChar != ')' || sender is not TextBox input || pickedFormula != input.Text
            || input.SelectionLength != 0 || input.SelectionStart >= input.TextLength
            || input.Text[input.SelectionStart] != ')') return;
        input.SelectionStart++;
        e.Handled = true;
    }

    private void FormulaTextChanged(object? sender, EventArgs e)
    {
        if (pickedFormula is not null && sender is TextBox input && input.Text != pickedFormula)
            ClearPickedReference();
    }

    private void ClearPickedReference()
    {
        if (referenceArea is null) return;
        bool wasDragging = referenceAnchor.HasValue;
        referenceAnchor = null;
        referenceArea = null;
        pickedFormula = null;
        if (wasDragging) grid.Capture = false;
        grid.Invalidate();
    }

    private void SaveCell(object? sender, DataGridViewCellEventArgs e)
    {
        if (refreshing || e.RowIndex < 0 || e.ColumnIndex < 0) return;
        string raw = grid[e.ColumnIndex, e.RowIndex].Value as string ?? "";
        if (table.Rows[e.RowIndex][e.ColumnIndex] == raw) return;
        using var edit = beginEdit();
        table.Rows[e.RowIndex][e.ColumnIndex] = raw;
        display = table.Calculate();
        grid.Invalidate();
        changed();
    }

    private bool HandleGridKey(Keys key)
    {
        if (grid.IsCurrentCellInEditMode) return false;
        switch (key)
        {
            case Keys.Control | Keys.C: CopySelection(cut: false); return true;
            case Keys.Control | Keys.X: CopySelection(cut: true); return true;
            case Keys.Control | Keys.V: PasteSelection(); return true;
            case Keys.Control | Keys.A:
                SelectRange(new Rectangle(0, 0, table.ColumnCount, table.Rows.Count));
                return true;
            case Keys.Delete: ClearSelection(); return true;
            default: return false;
        }
    }

    private DataGridViewCell[] SelectedCells() => grid.SelectedCells.Cast<DataGridViewCell>().ToArray();

    private int[] SelectedIndices(bool column) => grid.SelectedCells.Cast<DataGridViewCell>()
        .Select(cell => column ? cell.ColumnIndex : cell.RowIndex).Distinct().Order().ToArray();

    private static Rectangle SelectionBounds(DataGridViewCell[] cells) => Rectangle.FromLTRB(
        cells.Min(cell => cell.ColumnIndex), cells.Min(cell => cell.RowIndex),
        cells.Max(cell => cell.ColumnIndex) + 1, cells.Max(cell => cell.RowIndex) + 1);

    private static Rectangle RangeBetween(Point start, Point end) => Rectangle.FromLTRB(
        Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X) + 1, Math.Max(start.Y, end.Y) + 1);

    private static string RangeAddress(Rectangle area)
    {
        string first = $"{(char)('A' + area.Left)}{area.Top + 1}";
        return area.Width == 1 && area.Height == 1 ? first : $"{first}:{(char)('A' + area.Right - 1)}{area.Bottom}";
    }

    private bool CanPickReference(TextBox input)
    {
        if (!input.Text.TrimStart().StartsWith('=')) return false;
        if (pickedFormula == input.Text && input.SelectionStart == referenceStart + referenceLength && input.SelectionLength == 0)
            return true;
        string before = input.Text[..input.SelectionStart].TrimEnd();
        return before.EndsWith('=') || before.EndsWith('(');
    }

    private void UpdateGridCursor(Point location)
    {
        var hit = grid.HitTest(location.X, location.Y);
        Cursor cursor = Cursors.Default;
        if (hit.Type is DataGridViewHitTestType.RowHeader or DataGridViewHitTestType.ColumnHeader)
            cursor = Cursors.Hand;
        else if (hit.Type == DataGridViewHitTestType.Cell && grid.EditingControl is TextBox input
            && grid.CurrentCell is { } current && (hit.ColumnIndex != current.ColumnIndex || hit.RowIndex != current.RowIndex)
            && CanPickReference(input))
            cursor = Cursors.Cross;
        if (grid.Cursor != cursor) grid.Cursor = cursor;
    }

    private bool HandleReferencePointer(ReferencePointer action, Point location)
    {
        if (action == ReferencePointer.Up && referenceAnchor is not null)
        {
            referenceAnchor = null;
            grid.Capture = false;
            return true;
        }
        if (!grid.IsCurrentCellInEditMode || grid.EditingControl is not TextBox input
            || grid.CurrentCell is not { } current) return false;

        DataGridView.HitTestInfo hit = grid.HitTest(location.X, location.Y);
        if (action == ReferencePointer.Down)
        {
            if (hit.Type != DataGridViewHitTestType.Cell || (hit.ColumnIndex == current.ColumnIndex && hit.RowIndex == current.RowIndex)
                || !CanPickReference(input)) return false;
            if (pickedFormula != input.Text || input.SelectionStart != referenceStart + referenceLength || input.SelectionLength != 0)
            {
                referenceStart = input.SelectionStart;
                referenceLength = input.SelectionLength;
            }
            referenceAnchor = new Point(hit.ColumnIndex, hit.RowIndex);
            grid.Capture = true;
        }
        if (referenceAnchor is not Point start) return false;
        if (hit.Type == DataGridViewHitTestType.Cell)
        {
            Rectangle area = RangeBetween(start, new Point(hit.ColumnIndex, hit.RowIndex));
            if (area != referenceArea || action == ReferencePointer.Down)
            {
                string address = RangeAddress(area);
                string prefix = input.Text[..referenceStart];
                string suffix = input.Text[(referenceStart + referenceLength)..];
                if (suffix.Length == 0 && prefix.TrimEnd().EndsWith('(')) suffix = ")";
                referenceArea = area;
                referenceLength = address.Length;
                pickedFormula = prefix + address + suffix;
                input.Text = pickedFormula;
                input.Select(referenceStart + referenceLength, 0);
                grid.Invalidate();
            }
        }
        return true;
    }

    private void SelectRange(Rectangle range, bool moveCurrent = false)
    {
        selecting = true;
        try
        {
            if (moveCurrent) grid.CurrentCell = grid[range.Left, range.Top];
            grid.ClearSelection();
            for (int row = range.Top; row < range.Bottom; row++)
                for (int column = range.Left; column < range.Right; column++)
                    grid[column, row].Selected = true;
        }
        finally { selecting = false; }
        RefreshSelection();
    }

    private void StartSelectionDrag(object? sender, MouseEventArgs e)
    {
        dragAnchor = dragEnd = null;
        DataGridView.HitTestInfo hit = grid.HitTest(e.X, e.Y);
        if (hit.Type != DataGridViewHitTestType.Cell) return;
        if (e.Button == MouseButtons.Left && ModifierKeys == Keys.None)
            dragAnchor = new Point(hit.ColumnIndex, hit.RowIndex);
        else if (e.Button == MouseButtons.Right && !grid[hit.ColumnIndex, hit.RowIndex].Selected)
            SelectRange(new Rectangle(hit.ColumnIndex, hit.RowIndex, 1, 1), moveCurrent: true);
    }

    private void ContinueSelectionDrag(object? sender, MouseEventArgs e)
    {
        if (dragAnchor is not Point start || e.Button != MouseButtons.Left) return;
        DataGridView.HitTestInfo hit = grid.HitTest(e.X, e.Y);
        if (hit.Type != DataGridViewHitTestType.Cell) return;
        var end = new Point(hit.ColumnIndex, hit.RowIndex);
        if (dragEnd == end || (dragEnd is null && end == start)) return;
        dragEnd = end;
        SelectRange(RangeBetween(start, end));
    }

    private void CopySelection(bool cut)
    {
        if (!grid.EndEdit()) return;
        DataGridViewCell[] selected = SelectedCells();
        if (selected.Length == 0) return;
        Rectangle area = SelectionBounds(selected);
        if (selected.Length != area.Width * area.Height)
        {
            ShowFeedback("Select one rectangle of cells to copy or cut.");
            return;
        }

        string[][] raw = ReadBlock((row, column) => table.Rows[row][column]);
        string[][] visible = ReadBlock((row, column) => display[row, column]);
        var payload = new TableClipboard(raw, cut ? null : area.Top, cut ? null : area.Left);
        try { Clipboard.SetDataObject(payload.ToDataObject(visible), true, 5, 30); }
        catch (ExternalException) { ShowFeedback("The clipboard is busy. Try again."); return; }
        if (cut) ClearSelection();

        string[][] ReadBlock(Func<int, int, string> value) => Enumerable.Range(area.Top, area.Height)
            .Select(row => Enumerable.Range(area.Left, area.Width).Select(column => value(row, column)).ToArray()).ToArray();
    }

    private void ClearSelection()
    {
        if (!grid.EndEdit()) return;
        DataGridViewCell[] populated = SelectedCells().Where(cell => table.Rows[cell.RowIndex][cell.ColumnIndex].Length > 0).ToArray();
        if (populated.Length == 0) return;
        using var edit = beginEdit();
        foreach (DataGridViewCell cell in populated) table.Rows[cell.RowIndex][cell.ColumnIndex] = "";
        Reload();
        changed();
    }

    private void PasteSelection()
    {
        TableClipboard? payload;
        try { payload = TableClipboard.Read(Clipboard.GetDataObject()); }
        catch (ExternalException) { ShowFeedback("The clipboard is busy. Try again."); return; }
        if (payload is not null) PasteBlock(payload);
    }

    private void PasteBlock(TableClipboard payload)
    {
        if (!grid.EndEdit() || grid.CurrentCell is not { } current) return;
        string[][] values = payload.Cells;
        if (values.Length == 0 || values.Any(row => row is null || row.Length == 0
            || row.Any(value => value is null || value.Length > TableData.MaximumCellLength)))
        {
            ShowFeedback("Clipboard data exceeds the table’s cell or size limits.");
            return;
        }

        DataGridViewCell[] selection = SelectedCells();
        Rectangle start = selection.Length > 0 ? SelectionBounds(selection) : new Rectangle(current.ColumnIndex, current.RowIndex, 1, 1);
        using var edit = beginEdit();
        if (values.Length == 1 && values[0].Length == 1 && selection.Length > 1)
        {
            foreach (DataGridViewCell cell in selection) Put(cell.RowIndex, cell.ColumnIndex, values[0][0]);
            Reload();
            changed();
            return;
        }

        var area = new Rectangle(start.Left, start.Top, values.Max(row => row.Length), values.Length);
        if (area.Bottom > TableData.MaximumRows || area.Right > TableData.MaximumColumns)
        {
            ShowFeedback($"Paste needs room within {TableData.MaximumRows} rows and {TableData.MaximumColumns} columns.");
            return;
        }
        while (table.Rows.Count < area.Bottom) table.Append(column: false);
        while (table.ColumnCount < area.Right) table.Append(column: true);
        for (int row = 0; row < values.Length; row++)
            for (int column = 0; column < values[row].Length; column++)
                Put(area.Top + row, area.Left + column, values[row][column], row, column);
        Reload();
        SelectRange(area, moveCurrent: true);
        changed();

        void Put(int row, int column, string value, int rowOffset = 0, int columnOffset = 0)
        {
            if (payload.SourceRow is >= 0 and < TableData.MaximumRows && payload.SourceColumn is >= 0 and < TableData.MaximumColumns)
                value = TableData.ShiftCopiedFormula(value, row - payload.SourceRow.Value - rowOffset, column - payload.SourceColumn.Value - columnOffset);
            table.Rows[row][column] = value;
        }
    }

    private void ShowFeedback(string message) => tips.Show(message, grid, 8, 28, 3500);

    private void Append(bool column)
    {
        if (!table.CanAdd(column) || !grid.EndEdit()) return;
        using var edit = beginEdit();
        int row = grid.CurrentCell?.RowIndex ?? 0;
        int cell = grid.CurrentCell?.ColumnIndex ?? 0;
        table.Append(column);
        Reload();
        SelectRange(column ? new Rectangle(table.ColumnCount - 1, row, 1, 1) : new Rectangle(cell, table.Rows.Count - 1, 1, 1), moveCurrent: true);
        grid.Focus();
        changed();
    }

    private void Remove(bool column)
    {
        if (!grid.EndEdit() || grid.CurrentCell is not { } cell) return;
        int[] indices = SelectedIndices(column);
        if (indices.Length == 0 || indices.Length >= (column ? table.ColumnCount : table.Rows.Count)) return;
        int row = cell.RowIndex;
        int col = cell.ColumnIndex;
        string kind = column ? "column" : "row";
        string target = indices.Length == 1
            ? column ? $"column {(char)('A' + indices[0])}" : $"row {indices[0] + 1}"
            : $"{indices.Length} {kind}s";
        if (indices.Any(index => table.HasValues(column, index))
            && !CorkDialog.Confirm(FindForm()!, "Delete " + target, $"Delete {target} and its contents?", "Delete")) return;
        using var edit = beginEdit();
        foreach (int index in indices.Reverse()) table.Remove(column, index);
        Reload();
        SelectRange(new Rectangle(Math.Min(column ? indices[0] : col, table.ColumnCount - 1),
            Math.Min(column ? row : indices[0], table.Rows.Count - 1), 1, 1), moveCurrent: true);
        grid.Focus();
        changed();
    }

    private void ShowFormulaHelp()
    {
        using var dialog = new CorkDialog("Table formulas", 441);
        using var headingFont = new Font("Segoe UI Semibold", 10F);
        using var formulaFont = new Font("Consolas", 10F);
        dialog.Controls.Add(new Label
        {
            Text = "Click a formula to copy it.", ForeColor = BoardStyle.Muted,
            Bounds = new Rectangle(22, 53, 328, 22)
        });
        var feedback = new Label
        {
            Bounds = new Rectangle(22, 389, 200, 34),
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = BoardStyle.Muted
        };
        AddSection("Cell references", 82,
        [
            ("Read cell B1", "=B1"),
            ("Keep column B fixed", "=$B1")
        ]);
        AddSection("Functions", 183,
        [
            ("Add numbers", "=SUM(B2:B4)"),
            ("Highest value", "=MAX(B2:B4)"),
            ("Lowest value", "=MIN(B2:B4)"),
            ("Average numbers", "=AVERAGE(B2:B4)"),
            ("Count numbers", "=COUNT(B2:B4)")
        ]);

        Button close = BoardStyle.Button("Done", BoardStyle.Brick, Color.White);
        close.Bounds = new Rectangle(232, 389, 118, 34);
        close.DialogResult = DialogResult.OK;
        dialog.Controls.AddRange([feedback, close]);
        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        dialog.Shown += (_, _) => close.Focus();
        dialog.ShowDialog(FindForm());

        void AddSection(string title, int top, (string Description, string Formula)[] examples)
        {
            dialog.Controls.Add(new Label
            {
                Text = title, Font = headingFont, Bounds = new Rectangle(22, top, 328, 21)
            });
            var panel = new Panel
            {
                BackColor = Color.FromArgb(245, 235, 219),
                Bounds = new Rectangle(22, top + 25, 328, examples.Length * 32 + 8)
            };
            panel.Resize += (_, _) => BoardStyle.RoundCorners(panel, panel.LogicalToDeviceUnits(8));
            BoardStyle.RoundCorners(panel, 8);
            for (int index = 0; index < examples.Length; index++)
            {
                (string description, string formula) = examples[index];
                int rowTop = 4 + index * 32;
                var label = new Label
                {
                    Text = description, TextAlign = ContentAlignment.MiddleLeft,
                    Bounds = new Rectangle(10, rowTop, 142, 32)
                };
                Button copy = BoardStyle.Button(formula, BoardStyle.Paper, BoardStyle.Brick, compactText: true);
                copy.AccessibleName = $"Copy {formula}";
                copy.Font = formulaFont;
                copy.Bounds = new Rectangle(158, rowTop + 3, 160, 26);
                copy.Click += (_, _) =>
                {
                    try
                    {
                        Clipboard.SetText(formula);
                        feedback.ForeColor = BoardStyle.Muted;
                        feedback.Text = $"Copied {formula}";
                    }
                    catch (ExternalException)
                    {
                        feedback.ForeColor = BoardStyle.Brick;
                        feedback.Text = "Clipboard busy. Try again.";
                    }
                };
                panel.Controls.AddRange([label, copy]);
            }
            dialog.Controls.Add(panel);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            menu.Dispose();
            tips.Dispose();
        }
        base.Dispose(disposing);
    }

    private enum ReferencePointer { Down, Move, Up }

    private sealed class CellGrid(Func<Keys, bool> handleKey, Func<ReferencePointer, Point, bool> pickReference) : DataGridView
    {
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
            handleKey(keyData) || base.ProcessCmdKey(ref msg, keyData);

        protected override void WndProc(ref Message message)
        {
            ReferencePointer? pointer = message.Msg switch
            {
                0x0201 or 0x0203 => ReferencePointer.Down,
                0x0200 => ReferencePointer.Move,
                0x0202 => ReferencePointer.Up,
                _ => null
            };
            if (pointer is ReferencePointer action && (action != ReferencePointer.Move || Capture))
            {
                int position = unchecked((int)message.LParam.ToInt64());
                if (pickReference(action, new Point((short)position, (short)(position >> 16)))) return;
            }
            base.WndProc(ref message);
        }
    }
}
