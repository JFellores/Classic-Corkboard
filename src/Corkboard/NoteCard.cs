using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Corkboard;

internal sealed class NoteCard : UserControl
{
    public static readonly Size InitialCardSize = new(248, 184);
    public static readonly Size Largest = new(1400, 1100);
    private static readonly Size MinimumTextSize = new(224, 144);
    private static readonly Size MinimumImageSize = new(110, 112);
    private static readonly Size MinimumTableSize = new(252, 192);
    private const char UncheckedItem = '☐';
    private const char CheckedItem = '☑';
    private const uint Strikeout = 8;
    private const uint FontSizeMask = 0x80000000;
    private const int MaximumTextSize = 24;
    private readonly BoardWindow board;
    private readonly StickyNote note;
    private readonly RichTextBox? editor;
    private readonly TableGrid? tableGrid;
    private readonly Button label;
    private readonly Button options;
    private readonly Button? pin;
    private readonly Button? photoPop;
    private readonly Label grip;
    private readonly Panel header;
    private readonly Panel? photoCaption;
    private readonly PictureBox? photoPrint;
    private readonly Panel? toolbar;
    private readonly Panel writingArea;
    private readonly Panel? resizeFooter;
    private readonly Button? resizeGrip;
    private readonly List<Button> headerButtons = [];
    private readonly Dictionary<uint, Button> styleButtons = [];
    private readonly CorkMenu menu = new();
    private readonly CorkMenu pinMenu = new();
    private readonly CorkMenu? colorMenu;
    private readonly CorkMenu? formattingMenu;
    private readonly ToolTip tips = new();
    private Control? dragHandle;
    private Point dragStart;
    private Point dragOrigin;
    private bool pinDragged;
    private Point? resizeStart;
    private Size resizeOrigin;
    private bool refreshing;
    private string editorText = "";
    private bool connectionTarget;
    private bool stamp;
    private bool entering;
    private Region? entranceFrame;

    public static Size MinimumSizeFor(StickyNote note) =>
        note.Table is not null ? MinimumTableSize : note.ImageFile is null ? MinimumTextSize : MinimumImageSize;

    public NoteCard(BoardWindow board, StickyNote note, bool floating, Action? moveWindow = null, Image? image = null)
    {
        this.board = board;
        this.note = note;
        Font = board.Font;
        ForeColor = BoardStyle.Ink;
        Padding = new Padding(1, 1, 3, 3);
        BackColor = Color.FromArgb(142, 96, 63);
        bool isImage = note.ImageFile is not null;
        bool isTable = note.Table is not null;
        AccessibleName = isTable ? "Table card" : isImage ? "Image card" : floating ? "Floating sticky note" : "Board sticky note";

        var paper = new Panel { Dock = DockStyle.Fill };
        writingArea = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 10, floating ? 8 : 0) };
        if (isImage)
        {
            writingArea.Padding = new Padding(11, 0, 11, 0);
            if (image is not null)
            {
                photoPrint = new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, AccessibleName = "Attached image" };
                photoPrint.Paint += DrawPhotoEdge;
                writingArea.Controls.Add(photoPrint);
            }
            else writingArea.Controls.Add(new Label { Text = "Image unavailable", ForeColor = BoardStyle.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
        }
        else if (note.Table is TableData table)
        {
            writingArea.Padding = new Padding(9, 6, 9, floating ? 8 : 0);
            tableGrid = new TableGrid(table, () => board.ChangeTable(note, this));
            writingArea.Controls.Add(tableGrid);
        }
        else
        {
            editor = new NoteTextEditor
            {
                Multiline = true, AcceptsTab = false, ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false,
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, ForeColor = BoardStyle.Ink,
                AccessibleName = "Note text", Margin = Padding.Empty, HideSelection = false
            };
            editor.TextChanged += (_, _) => PublishEdit();
            editor.SelectionChanged += (_, _) => RefreshFormattingButtons();
            editor.LinkClicked += (_, e) => OpenLink(e.LinkText);
            editor.MouseClick += ClickChecklistItem;
            editor.MouseMove += (_, e) =>
            {
                if (ChecklistMarkerAt(e.Location) >= 0) editor.Cursor = Cursors.Hand;
                else if (editor.Cursor == Cursors.Hand) editor.ResetCursor();
            };
            writingArea.Controls.Add(editor);
        }

        header = new Panel { Dock = DockStyle.Top, Height = isImage ? (floating ? 11 : 25) : 32 };
        if (isImage) photoCaption = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(7, 3, 3, 0) };
        Panel commands = photoCaption ?? header;
        label = BoardStyle.Button("", BoardStyle.PanelTint, BoardStyle.Ink);
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.AutoEllipsis = true;
        label.Font = isImage ? new Font("Segoe UI", 9.5F, FontStyle.Italic) : new Font("Segoe UI Semibold", 9.5F);
        label.AccessibleName = "Edit note label";
        label.Click += (_, _) => board.EditLabel(note, FindForm() ?? board);
        headerButtons.Add(label);
        commands.Controls.Add(label);

        grip = new Label
        {
            Dock = DockStyle.Right, Width = 24, Text = "⠿", TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = floating ? "Drag sticky window" : "Drag note"
        };
        if (floating)
        {
            grip.Cursor = Cursors.SizeAll;
            grip.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) moveWindow?.Invoke(); };
        }
        else
        {
            WireDragHandle(grip);
        }
        commands.Controls.Add(grip);

        options = HeaderButton("⋯", "Note options", DockStyle.Right);
        options.Click += (_, _) => menu.Show(options, new Point(0, options.Height));
        commands.Controls.Add(options);
        if (!floating)
        {
            Button pop = HeaderButton("↗", "Pop out note", DockStyle.Right);
            if (isImage) photoPop = pop;
            pop.Click += (_, _) => board.PopOut(note);
            commands.Controls.Add(pop);
            pin = HeaderButton("", "Pin note", isImage ? DockStyle.None : DockStyle.Left);
            pin.Cursor = Cursors.Hand;
            WireDragHandle(pin);
            if (isImage)
            {
                pin.Height = 24;
                pin.Left = (header.ClientSize.Width - pin.Width) / 2;
                header.Resize += (_, _) => pin.Left = (header.ClientSize.Width - pin.Width) / 2;
            }
            pin.Paint += (_, e) => BoardStyle.DrawPin(e.Graphics, new Point(pin.Width / 2, pin.Height / 2), note.Pinned,
                note.PinColor is int color ? BoardStyle.PinColors[Math.Clamp(color, 0, BoardStyle.PinColors.Length - 1)].Color
                    : BoardStyle.NoteAccent(note.Color), connectionTarget);
            pin.Click += (_, e) =>
            {
                if (e is MouseEventArgs && pinDragged) return;
                if (board.HandlePinSelection(note)) return;
                if (!note.Pinned) board.SetPinned(note, true);
                else pinMenu.Show(pin, new Point(0, pin.Height));
            };
            header.Controls.Add(pin);
            var move = new ToolStripMenuItem("Move with pin", null, (_, _) => board.TogglePinnedMove(note));
            pinMenu.Items.Add(move);
            var moveSlide = new CorkSubmenuItem("Move to slide");
            pinMenu.Items.Add(moveSlide);
            pinMenu.Items.Add("Remove pin", null, (_, _) => board.SetPinned(note, false));
            pinMenu.Items.Add("Add thread", null, (_, _) => board.StartThread(note));
            var pinColors = new CorkSubmenuItem("Pin color");
            var matchNote = new ToolStripMenuItem("Match note", null, (_, _) => board.ChangePinColor(note, null));
            pinColors.DropDownItems.Add(matchNote);
            var choices = new List<ToolStripMenuItem>();
            for (int i = 0; i < BoardStyle.PinColors.Length; i++)
            {
                int index = i;
                var choice = new ToolStripMenuItem(BoardStyle.PinColors[i].Name) { ForeColor = BoardStyle.PinColors[i].Color };
                choice.Click += (_, _) => board.ChangePinColor(note, index);
                choices.Add(choice);
                pinColors.DropDownItems.Add(choice);
            }
            pinMenu.Items.Add(pinColors);
            var threadColors = new CorkSubmenuItem("Thread color");
            pinMenu.Items.Add(threadColors);
            var slideConnections = new CorkSubmenuItem("Other slides");
            pinMenu.Items.Add(slideConnections);
            pinMenu.Opening += (_, _) =>
            {
                pinMenu.SetSurface(writingArea.BackColor);
                move.Checked = board.CanMoveNote(note);
                board.ListMoveSlides(note, moveSlide);
                matchNote.Checked = note.PinColor is null;
                for (int i = 0; i < choices.Count; i++) choices[i].Checked = note.PinColor == i;
                board.ListThreadColors(note, threadColors);
                board.ListSlideConnections(note, slideConnections);
            };
        }

        if (editor is not null)
        {
            colorMenu = new CorkMenu();
            formattingMenu = new CorkMenu();
            toolbar = new Panel { Dock = DockStyle.Top, Height = 31, Padding = new Padding(7, 3, 6, 0) };
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            AddFormat(tools, "B", "Bold (Ctrl+B)", FontStyle.Bold, 1);
            AddFormat(tools, "I", "Italic (Ctrl+I)", FontStyle.Italic, 2);
            AddFormat(tools, "U", "Underline (Ctrl+U)", FontStyle.Underline, 4);
            AddFormat(tools, "ab", "Strikethrough", FontStyle.Strikeout, Strikeout);
            Button checklist = BoardStyle.Button("", BoardStyle.Paper, BoardStyle.Ink);
            checklist.Size = new Size(25, 27);
            checklist.AccessibleName = "Checklist";
            checklist.AccessibleDescription = "Make selected text a checklist item. Click a box or press Ctrl+Shift+Enter to check it.";
            checklist.Click += (_, _) => AddChecklistItems();
            checklist.Paint += PaintChecklistButton;
            tips.SetToolTip(checklist, "Make checklist item (Ctrl+Shift+L)");
            headerButtons.Add(checklist);
            tools.Controls.Add(checklist);
            Button textSize = BoardStyle.Button("Aa", BoardStyle.Paper, BoardStyle.Ink, compactText: true);
            textSize.Size = new Size(29, 27);
            textSize.AccessibleName = "Text size";
            textSize.Click += (_, _) => EditTextSize();
            tips.SetToolTip(textSize, "Text size");
            headerButtons.Add(textSize);
            tools.Controls.Add(textSize);
            Button link = BoardStyle.Button("", BoardStyle.Paper, BoardStyle.Ink);
            link.Size = new Size(27, 27);
            link.AccessibleName = "Insert link";
            link.Click += (_, _) => InsertLink();
            link.Paint += PaintLinkButton;
            tips.SetToolTip(link, "Insert link (Ctrl+K)");
            headerButtons.Add(link);
            tools.Controls.Add(link);
            Button list = AddParagraphButton("☷", "List style", "Cycle bullets, numbering, dashes, and plain text", CycleList);
            Button divider = AddParagraphButton("⎯", "Divider", "Cycle solid, dashed, double, and no divider", CycleDivider);
            Button timestamp = AddParagraphButton("", "Insert timestamp", "Insert the current date and time", InsertTimestamp);
            timestamp.Paint += NoteTimestamp.PaintIcon;
            Button overflow = BoardStyle.Button("⋯", BoardStyle.Paper, BoardStyle.Ink, compactText: true);
            overflow.AccessibleName = "More formatting";
            tips.SetToolTip(overflow, "More formatting");
            Button[] formatButtons = tools.Controls.OfType<Button>().ToArray();
            tools.Controls.Add(overflow);
            overflow.Click += (_, _) =>
            {
                foreach (ToolStripItem item in formattingMenu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
                formattingMenu.SetSurface(toolbar.BackColor);
                foreach (Button button in formatButtons.Where(button => !button.Visible))
                {
                    if (button == checklist) formattingMenu.Items.Add("Checklist", null, (_, _) => AddChecklistItems());
                    else if (button == textSize) formattingMenu.Items.Add("Text size…", null, (_, _) => EditTextSize());
                    else if (button == link) formattingMenu.Items.Add("Insert link…", null, (_, _) => InsertLink());
                    else if (button == list) formattingMenu.Items.Add("Cycle list style", null, (_, _) => CycleList());
                    else if (button == divider) formattingMenu.Items.Add("Cycle divider", null, (_, _) => CycleDivider());
                    else if (button == timestamp) formattingMenu.Items.Add("Insert timestamp", null, (_, _) => InsertTimestamp());
                    else
                    {
                        uint effect = styleButtons.Single(pair => pair.Value == button).Key;
                        formattingMenu.Items.Add(button.AccessibleName!, null, (_, _) => ToggleFormat(effect));
                    }
                }
                formattingMenu.Show(overflow, new Point(0, overflow.Height));
            };
            headerButtons.Add(overflow);
            Button color = BoardStyle.Button("●", BoardStyle.PanelTint, BoardStyle.Ink);
            color.Dock = DockStyle.Right;
            color.Width = 29;
            color.AccessibleName = "Note color";
            color.Click += (_, _) => colorMenu.Show(color, new Point(0, color.Height));
            tips.SetToolTip(color, "Note color");
            headerButtons.Add(color);
            toolbar.Controls.Add(tools);
            toolbar.Controls.Add(color);
            tools.SizeChanged += (_, _) => FitTools();
            FitTools();

            Button AddParagraphButton(string glyph, string name, string hint, Action action)
            {
                Button button = BoardStyle.Button(glyph, BoardStyle.Paper, BoardStyle.Ink, compactText: glyph.Length != 0);
                button.Size = new Size(27, 27);
                button.AccessibleName = name;
                button.Click += (_, _) => action();
                tips.SetToolTip(button, hint);
                headerButtons.Add(button);
                tools.Controls.Add(button);
                return button;
            }

            void FitTools()
            {
                int slots = tools.ClientSize.Width / Math.Max(1, tools.LogicalToDeviceUnits(22));
                bool needsOverflow = slots < formatButtons.Length;
                int shown = needsOverflow ? Math.Clamp(slots - 1, 0, formatButtons.Length) : formatButtons.Length;
                int count = shown + (needsOverflow ? 1 : 0);
                int width = Math.Max(1, Math.Min(tools.LogicalToDeviceUnits(27), tools.ClientSize.Width / count));
                tools.SuspendLayout();
                for (int index = 0; index < formatButtons.Length; index++)
                {
                    formatButtons[index].Visible = index < shown;
                    formatButtons[index].Width = width;
                }
                overflow.Visible = needsOverflow;
                overflow.Width = width;
                tools.ResumeLayout();
            }
        }

        var colorOptions = new CorkSubmenuItem(isImage ? "Frame color" : isTable ? "Table color" : "Note color");
        for (int i = 0; i < BoardStyle.Colors.Length; i++)
        {
            int chosen = i;
            colorMenu?.Items.Add(BoardStyle.Colors[i].Name, null, (_, _) => board.ChangeColor(note, chosen));
            colorOptions.DropDownItems.Add(BoardStyle.Colors[i].Name, null, (_, _) => board.ChangeColor(note, chosen));
        }
        if (colorMenu is not null) colorMenu.Opening += (_, _) =>
        {
            colorMenu.SetSurface(writingArea.BackColor);
            CheckColors(colorMenu.Items);
        };
        var above = new ToolStripMenuItem("Keep above other windows", null, (_, _) => board.ToggleAlwaysOnTop(note));
        menu.Items.Add("Edit label", null, (_, _) => board.EditLabel(note, FindForm() ?? board));
        menu.Items.Add(colorOptions);
        if (editor is not null)
        {
            var formatting = new CorkSubmenuItem("Formatting");
            formatting.DropDownItems.Add("Text size…", null, (_, _) => EditTextSize());
            var indent = new CorkSubmenuItem("List indent");
            indent.DropDownItems.Add("Compact", null, (_, _) => SetListIndent(compact: true));
            indent.DropDownItems.Add("Standard", null, (_, _) => SetListIndent(compact: false));
            formatting.DropDownItems.Add(indent);
            var freeForm = new CorkSubmenuItem("Free-form list");
            freeForm.DropDownItems.Add("Bullets", null, (_, _) => MakeFreeFormList(1));
            freeForm.DropDownItems.Add("Numbers", null, (_, _) => MakeFreeFormList(2));
            freeForm.DropDownItems.Add("Dashes", null, (_, _) => MakeFreeFormList(3));
            formatting.DropDownItems.Add(freeForm);
            formatting.DropDownItems.Add(new ToolStripSeparator());
            formatting.DropDownItems.Add("Timestamp format…", null, (_, _) =>
            {
                var format = NoteTimestamp.ChooseFormat(FindForm() ?? board, note.TimestampFormat);
                if (format.HasValue) board.ChangeTimestampFormat(note, format.Value);
            });
            menu.Items.Add(formatting);
            menu.Items.Add("Daily checklist reset…", null, (_, _) => board.EditChecklistReset(note, FindForm() ?? board));
        }
        menu.Items.Add(new ToolStripSeparator());
        if (!floating) menu.Items.Add(isTable ? "Pop out table" : "Pop out note", null, (_, _) => board.PopOut(note));
        menu.Items.Add(above);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(isTable ? "Delete table" : "Delete note", null, (_, _) => board.DeleteNote(note, FindForm() ?? board));
        menu.Opening += (_, _) =>
        {
            menu.SetSurface(writingArea.BackColor);
            above.Checked = note.PoppedOut && note.AlwaysOnTop;
            CheckColors(colorOptions.DropDownItems);
        };

        paper.Controls.Add(writingArea);
        if (photoCaption is not null) paper.Controls.Add(photoCaption);
        if (!floating)
        {
            resizeFooter = new Panel { Dock = DockStyle.Bottom, Height = isImage ? 17 : 23 };
            resizeGrip = BoardStyle.Button("", BoardStyle.PanelTint, BoardStyle.Ink);
            resizeGrip.Dock = DockStyle.Right;
            resizeGrip.Width = 29;
            resizeGrip.Cursor = Cursors.SizeNWSE;
            resizeGrip.AccessibleName = "Resize note. Use the arrow keys when focused.";
            tips.SetToolTip(resizeGrip, "Drag to resize note");
            resizeGrip.Paint += DrawResizeGrip;
            resizeGrip.MouseDown += StartResize;
            resizeGrip.MouseMove += ContinueResize;
            resizeGrip.MouseUp += (_, _) => FinishResize();
            resizeGrip.MouseCaptureChanged += (_, _) => FinishResize();
            resizeGrip.PreviewKeyDown += (_, e) => e.IsInputKey = IsArrow(e.KeyCode);
            resizeGrip.KeyDown += ResizeWithKeyboard;
            resizeFooter.Controls.Add(resizeGrip);
            paper.Controls.Add(resizeFooter);
            SizeChanged += (_, _) => UpdateCardFrame();
        }
        if (toolbar is not null) paper.Controls.Add(toolbar);
        paper.Controls.Add(header);
        Controls.Add(paper);
        RefreshLabel();
        RefreshPin();
        ApplyColor();
        ReadText();
    }

    public Point PinPointOnParent => new(Left + Padding.Left + header.Left + (pin?.Left ?? 0) + (pin?.Width ?? 28) / 2,
        Top + Padding.Top + header.Top + (pin?.Top ?? 0) + (pin?.Height ?? header.Height) / 2);
    public StickyNote Note => note;

    public void SetConnectionTarget(bool value)
    {
        if (connectionTarget == value) return;
        connectionTarget = value;
        pin?.Invalidate();
    }

    public void FocusEditor()
    {
        if (editor is not null) editor.Focus();
        else if (tableGrid is not null) tableGrid.FocusCells();
        else if (label.Visible) label.Focus();
        else options.Focus();
    }

    public void RefreshLabel()
    {
        label.Text = string.IsNullOrWhiteSpace(note.Label) ? (note.Table is null ? "Add label" : "Table") : note.Label;
        tips.SetToolTip(label, string.IsNullOrWhiteSpace(note.Label) ? "Label this note" : note.Label);
    }

    public void RefreshTable() => tableGrid?.Reload();
    public void CommitTableEdit() => tableGrid?.CommitEdit();

    public void RefreshPin()
    {
        if (pin is null) return;
        pin.AccessibleName = note.Pinned ? "Pinned note options" : "Pin note";
        pin.AccessibleDescription = "Hold and drag to move the card.";
        tips.SetToolTip(pin, note.Pinned ? "Click for pin options · Hold and drag to move" : "Click to pin · Hold and drag to move");
        bool canMove = board.CanMoveNote(note);
        grip.Cursor = canMove ? Cursors.SizeAll : Cursors.Default;
        tips.SetToolTip(grip, canMove ? "Drag to move note" : "Hold and drag the pin to move this note");
        pin.Invalidate();
    }

    public void ReadText()
    {
        if (editor is null) return;
        if (note.FormattedText is string rtf ? editor.Rtf == rtf : editorText == note.Text) return;
        refreshing = true;
        int start = editor.SelectionStart;
        int length = editor.SelectionLength;
        try
        {
            LoadNoteText(editor, note);
            editorText = editor.Text;
            start = Math.Min(start, editor.TextLength);
            editor.Select(start, Math.Min(length, editor.TextLength - start));
        }
        finally { refreshing = false; }
        RefreshFormattingButtons();
    }

    private static void LoadNoteText(RichTextBox editor, StickyNote note)
    {
        if (!string.IsNullOrWhiteSpace(note.FormattedText))
        {
            try
            {
                editor.Rtf = note.FormattedText;
                return;
            }
            catch (ArgumentException) { }
        }
        editor.Text = note.Text;
    }

    public void ApplyColor()
    {
        var color = BoardStyle.Colors[Math.Clamp(note.Color, 0, BoardStyle.Colors.Length - 1)];
        Color paper = photoCaption is null ? color.Paper : BoardStyle.PhotoPaper(note.Color);
        Color controls = photoCaption is null ? color.Edge : paper;
        if (editor is not null) editor.BackColor = paper;
        tableGrid?.ApplyColor(paper, controls);
        writingArea.BackColor = paper;
        header.BackColor = controls;
        if (photoCaption is not null) photoCaption.BackColor = paper;
        if (toolbar is not null) toolbar.BackColor = paper;
        if (resizeFooter is not null) resizeFooter.BackColor = paper;
        if (resizeGrip is not null) resizeGrip.BackColor = paper;
        foreach (Button button in headerButtons) button.BackColor = toolbar?.Contains(button) == true ? paper : controls;
        pin?.Invalidate();
        RefreshFormattingButtons();
    }

    private void DrawPhotoEdge(object? sender, PaintEventArgs e)
    {
        if (sender is not PictureBox { Image: Image image } print || print.Width < 2 || print.Height < 2) return;
        double scale = Math.Min((double)print.Width / image.Width, (double)print.Height / image.Height);
        int width = Math.Max(1, (int)(image.Width * scale));
        int height = Math.Max(1, (int)(image.Height * scale));
        using var pen = new Pen(stamp ? Color.FromArgb(205, BoardStyle.Brick) : Color.FromArgb(95, BoardStyle.Muted));
        var border = new Rectangle((print.Width - width) / 2, (print.Height - height) / 2, width - 1, height - 1);
        e.Graphics.DrawRectangle(pen, border);
        if (stamp && width > 12 && height > 12)
        {
            pen.Color = Color.FromArgb(120, BoardStyle.Brick);
            e.Graphics.DrawRectangle(pen, Rectangle.Inflate(border, -2, -2));
        }
    }

    private void UpdateCardFrame()
    {
        FinishEntrance();
        if (photoCaption is null || resizeFooter is null)
        {
            BoardStyle.RoundCorners(this, LogicalToDeviceUnits(7));
            return;
        }

        bool compact = Width < LogicalToDeviceUnits(238) || Height < LogicalToDeviceUnits(184);
        if (stamp != compact)
        {
            stamp = compact;
            label.Visible = !stamp;
            if (photoPop is not null) photoPop.Visible = !stamp;
            header.Height = LogicalToDeviceUnits(stamp ? 21 : 25);
            if (pin is not null) pin.Height = LogicalToDeviceUnits(stamp ? 21 : 24);
            photoCaption.Height = LogicalToDeviceUnits(stamp ? 23 : 36);
            resizeFooter.Height = LogicalToDeviceUnits(stamp ? 15 : 17);
            int inset = LogicalToDeviceUnits(stamp ? 9 : 11);
            writingArea.Padding = new Padding(inset, 0, inset, 0);
            photoPrint?.Invalidate();
        }

        if (!stamp)
        {
            BoardStyle.RoundCorners(this, LogicalToDeviceUnits(2));
            return;
        }

        int radius = LogicalToDeviceUnits(3);
        int pitch = LogicalToDeviceUnits(10);
        using var holes = new GraphicsPath(FillMode.Winding);
        for (int x = pitch; x < Width - radius; x += pitch)
        {
            holes.AddEllipse(x - radius, -radius, radius * 2, radius * 2);
            holes.AddEllipse(x - radius, Height - radius, radius * 2, radius * 2);
        }
        for (int y = pitch; y < Height - radius; y += pitch)
        {
            holes.AddEllipse(-radius, y - radius, radius * 2, radius * 2);
            holes.AddEllipse(Width - radius, y - radius, radius * 2, radius * 2);
        }
        var edge = new Region(ClientRectangle);
        edge.Exclude(holes);
        Region? previous = Region;
        Region = edge;
        previous?.Dispose();
    }

    public void BeginEntrance()
    {
        FinishEntrance();
        entranceFrame = Region?.Clone();
        entering = true;
        RevealEntrance(0);
    }

    public void RevealEntrance(double progress)
    {
        if (!entering) return;
        double inset = 0.09 * (1 - progress);
        int horizontal = (int)Math.Round(Width * inset);
        int vertical = (int)Math.Round(Height * inset);
        Rectangle visible = Rectangle.Inflate(ClientRectangle, -horizontal, -vertical);
        Region clip = entranceFrame?.Clone() ?? new Region(ClientRectangle);
        clip.Intersect(visible);
        Region = clip;
    }

    public void FinishEntrance()
    {
        if (!entering) return;
        entering = false;
        Region = entranceFrame;
        entranceFrame = null;
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (editor is not null && editor.Focused && keys == Keys.Enter && NoteParagraphs.HandleDashEnter(editor))
            return true;
        if (editor is not null && keys == (Keys.Control | Keys.K))
        {
            InsertLink();
            return true;
        }
        if (editor is not null && keys == (Keys.Control | Keys.Shift | Keys.L))
        {
            AddChecklistItems();
            return true;
        }
        if (editor is not null && keys == (Keys.Control | Keys.Shift | Keys.Enter))
        {
            int marker = LineStart(editor.Text, editor.SelectionStart);
            if (IsChecklistItem(editor.Text, marker))
            {
                ToggleChecklistItem(marker);
                return true;
            }
        }
        uint effect = keys switch { Keys.Control | Keys.B => 1, Keys.Control | Keys.I => 2, Keys.Control | Keys.U => 4, _ => 0 };
        if (effect == 0 || editor is null) return base.ProcessCmdKey(ref message, keys);
        ToggleFormat(effect);
        return true;
    }

    private void CycleList()
    {
        if (editor is null) return;
        editor.Focus();
        EditText(() => NoteParagraphs.CycleList(editor));
    }

    private void SetListIndent(bool compact)
    {
        if (editor is null) return;
        editor.Focus();
        EditText(() => NoteParagraphs.SetListIndent(editor, compact));
    }

    private void MakeFreeFormList(int style)
    {
        if (editor is null) return;
        editor.Focus();
        EditText(() => NoteParagraphs.MakeFreeFormList(editor, style));
    }

    private void CycleDivider()
    {
        if (editor is null) return;
        editor.Focus();
        EditText(() => NoteParagraphs.CycleDivider(editor));
    }

    private void InsertLink()
    {
        if (editor is null) return;
        int start = editor.SelectionStart;
        int length = editor.SelectionLength;
        var link = NoteLinkPrompt.Show(FindForm() ?? board, NoteLinks.SelectedDisplayText(editor));
        if (link is not { } chosen) return;
        editor.Focus();
        editor.Select(start, length);
        EditText(() => NoteLinks.Insert(editor, chosen.Alias, chosen.Destination, chosen.Color));
    }

    private void InsertTimestamp()
    {
        if (editor is null) return;
        editor.Focus();
        // Use the editor's normal input path so divider rows and link boundaries are protected.
        EditText(() =>
        {
            ((NoteTextEditor)editor).MovePastDivider();
            NoteLinks.InsertPlainText(editor, NoteTimestamp.Format(DateTime.Now, note.TimestampFormat));
        });
    }

    private void OpenLink(string? address)
    {
        if (address is null || NoteLinks.ParseWebAddress(address) is not Uri destination) return;
        try { Process.Start(new ProcessStartInfo(destination.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            board.ShowNotice("Windows couldn’t open the link.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FinishEntrance();
            tips.Dispose();
            menu.Dispose();
            pinMenu.Dispose();
            colorMenu?.Dispose();
            formattingMenu?.Dispose();
        }
        base.Dispose(disposing);
    }

    private Button HeaderButton(string text, string title, DockStyle dock)
    {
        Button button = BoardStyle.Button(text, BoardStyle.PanelTint, BoardStyle.Ink);
        button.Dock = dock;
        button.Width = 28;
        button.AccessibleName = title;
        tips.SetToolTip(button, title);
        headerButtons.Add(button);
        return button;
    }

    private void AddFormat(Control tools, string text, string title, FontStyle style, uint effect)
    {
        Button button = BoardStyle.Button(text, BoardStyle.PanelTint, BoardStyle.Ink, compactText: true);
        button.Size = new Size(25, 27);
        button.Font = new Font(Font.FontFamily, 9F, style);
        button.AccessibleName = title;
        button.Click += (_, _) => ToggleFormat(effect);
        tips.SetToolTip(button, title);
        tools.Controls.Add(button);
        styleButtons.Add(effect, button);
    }

    private void CheckColors(ToolStripItemCollection items)
    {
        for (int i = 0; i < items.Count; i++) if (items[i] is ToolStripMenuItem item) item.Checked = i == note.Color;
    }

    private void ToggleFormat(uint effect)
    {
        if (editor is null) return;
        editor.Focus();
        if (editor is NoteTextEditor noteEditor && noteEditor.TryGetDivider(out _))
        {
            int dividerFlag = effect switch { 1 => 1, 4 => 2, _ => 0 };
            if (dividerFlag != 0) EditText(() => noteEditor.ToggleDividerFormat(dividerFlag));
            return;
        }
        CharFormat current = ReadSelectionFormat(editor);
        SetFormat(editor, effect, (current.Mask & current.Effects & effect) == 0);
        RefreshFormattingButtons();
        PublishEdit();
    }

    private static void SetFormat(RichTextBox editor, uint effect, bool enabled)
    {
        var change = CharFormat.Empty;
        change.Mask = effect;
        change.Effects = enabled ? effect : 0;
        SendFormat(editor.Handle, 0x0444, new IntPtr(1), ref change);
    }

    private void EditTextSize()
    {
        if (editor is null) return;
        int start = editor.SelectionStart;
        int length = editor.SelectionLength;
        int defaultHeight = (int)Math.Round(editor.Font.SizeInPoints * 20);
        CharFormat selection = ReadSelectionFormat(editor);
        int current = (selection.Mask & FontSizeMask) == 0 || selection.Height == defaultHeight
            ? 0 : Math.Clamp((int)Math.Round(selection.Height / 20.0), 1, MaximumTextSize);
        int? size = TextSizePrompt.Show(FindForm() ?? board, current, MaximumTextSize, editor.Font, writingArea.BackColor);
        if (size is null) return;

        editor.Focus();
        editor.Select(start, length);
        var change = CharFormat.Empty;
        change.Mask = FontSizeMask;
        change.Height = size == 0 ? defaultHeight : size.Value * 20;
        SendFormat(editor.Handle, 0x0444, new IntPtr(1), ref change);
        PublishEdit();
    }

    private static void PaintChecklistButton(object? sender, PaintEventArgs e)
    {
        if (sender is not Button button) return;
        int size = button.LogicalToDeviceUnits(12);
        var square = new Rectangle((button.Width - size) / 2, (button.Height - size) / 2, size, size);
        using GraphicsPath outline = BoardStyle.RoundedOutline(square, button.LogicalToDeviceUnits(2));
        using var pen = new Pen(button.ForeColor, button.LogicalToDeviceUnits(1)) { LineJoin = LineJoin.Round };
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, outline);
        e.Graphics.DrawLines(pen, new Point[]
        {
            new(square.Left + size / 4, square.Top + size / 2),
            new(square.Left + size * 5 / 12, square.Top + size * 2 / 3),
            new(square.Left + size * 3 / 4, square.Top + size / 3)
        });
    }

    private static void PaintLinkButton(object? sender, PaintEventArgs e)
    {
        if (sender is not Button button) return;
        Graphics graphics = e.Graphics;
        GraphicsState state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(button.Width / 2f, button.Height / 2f);
        float scale = button.DeviceDpi / 96f;
        graphics.ScaleTransform(scale, scale);
        graphics.RotateTransform(-45);
        using var pen = new Pen(button.ForeColor, 1.5f)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
        };
        using var links = new GraphicsPath();
        links.AddLine(-2.5f, 3, -5.5f, 3);
        links.AddArc(-8.5f, -3, 6, 6, 90, 180);
        links.AddLine(-5.5f, -3, -2.5f, -3);
        links.StartFigure();
        links.AddLine(2.5f, -3, 5.5f, -3);
        links.AddArc(2.5f, -3, 6, 6, 270, 180);
        links.AddLine(5.5f, 3, 2.5f, 3);
        graphics.DrawPath(pen, links);
        graphics.DrawLine(pen, -3, 0, 3, 0);
        graphics.Restore(state);
    }

    private void AddChecklistItems()
    {
        if (editor is null) return;
        editor.Focus();
        string text = editor.Text;
        int start = editor.SelectionStart;
        int end = start + editor.SelectionLength;
        int firstLine = LineStart(text, start);
        if (start == end && IsChecklistItem(text, firstLine))
        {
            ToggleChecklistItem(firstLine);
            return;
        }
        bool hasLinks = note.FormattedText?.Contains(@"\fldinst", StringComparison.Ordinal) == true;
        if (hasLinks)
        {
            (start, end) = NoteLinks.IncludeWholeLinks(editor, text, start, end);
            firstLine = LineStart(text, start);
        }

        var items = new List<(int Start, int End, bool BreakBefore, bool BreakAfter)>();
        for (int line = firstLine; ; )
        {
            int lineEnd = LineEnd(text, line);
            if (!IsChecklistItem(text, line))
            {
                int itemStart = start == end ? line : Math.Max(start, line);
                int itemEnd = start == end ? lineEnd : Math.Min(end, lineEnd);
                if (start == end || (itemEnd > itemStart && !text.AsSpan(itemStart, itemEnd - itemStart).IsWhiteSpace()))
                    items.Add((itemStart, itemEnd, itemStart > line, itemEnd < lineEnd));
            }
            if (lineEnd + 1 >= end) break;
            line = lineEnd + 1;
        }
        if (items.Count == 0) return;

        int caret = items[^1].End;
        EditText(() =>
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i];
                if (item.BreakAfter)
                {
                    editor.Select(item.End, 0);
                    InsertChecklistText("\n");
                }
                string prefix = item.BreakBefore ? $"\n{UncheckedItem} " : $"{UncheckedItem} ";
                editor.Select(item.Start, 0);
                InsertChecklistText(prefix);
                int marker = item.Start + prefix.Length - 2;
                editor.Select(marker, item.End - item.Start + 2);
                SetFormat(editor, Strikeout, false);
                caret += prefix.Length;
                if (i != items.Count - 1 && item.BreakAfter) caret++;
            }
            editor.Select(caret, 0);
            SetFormat(editor, Strikeout, false);
        });

        void InsertChecklistText(string value)
        {
            if (hasLinks) NoteLinks.InsertPlainText(editor, value);
            else editor.SelectedText = value;
        }
    }

    private void ClickChecklistItem(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Clicks != 1) return;
        int marker = ChecklistMarkerAt(e.Location);
        if (marker >= 0) ToggleChecklistItem(marker);
    }

    private int ChecklistMarkerAt(Point pointer)
    {
        if (editor is null || editorText.Length < 2) return -1;
        int marker = LineStart(editorText, editor.GetCharIndexFromPosition(pointer));
        if (!IsChecklistItem(editorText, marker)) return -1;
        Point left = editor.GetPositionFromCharIndex(marker);
        Point right = editor.GetPositionFromCharIndex(marker + 1);
        var target = new Rectangle(left, new Size(Math.Max(1, right.X - left.X), editor.Font.Height + 2));
        return target.Contains(pointer) ? marker : -1;
    }

    private void ToggleChecklistItem(int marker)
    {
        if (editor is null) return;
        string text = editor.Text;
        if (!IsChecklistItem(text, marker)) return;
        bool complete = text[marker] == UncheckedItem;
        int start = editor.SelectionStart;
        int length = editor.SelectionLength;
        EditText(() =>
        {
            SetChecklistState(editor, marker, LineEnd(text, marker), complete);
            editor.Select(start, length);
        });
    }

    public static bool ResetCheckedItems(StickyNote note)
    {
        if (!note.Text.Contains(CheckedItem)) return false;

        using var editor = new RichTextBox { DetectUrls = false };
        LoadNoteText(editor, note);
        string text = editor.Text;
        bool changed = false;
        for (int line = 0; line < text.Length;)
        {
            int end = LineEnd(text, line);
            if (text[line] == CheckedItem && IsChecklistItem(text, line))
            {
                SetChecklistState(editor, line, end, complete: false);
                changed = true;
            }
            line = end + 1;
        }
        if (changed)
        {
            string rtf = editor.Rtf ?? "";
            note.Text = NoteLinks.DisplayText(editor, editor.Text, rtf);
            note.FormattedText = rtf;
        }
        return changed;
    }

    private static void SetChecklistState(RichTextBox editor, int marker, int end, bool complete)
    {
        editor.Select(marker, 1);
        editor.SelectedText = (complete ? CheckedItem : UncheckedItem).ToString();
        editor.Select(marker, 2);
        SetFormat(editor, Strikeout, false);
        if (end <= marker + 2) return;
        editor.Select(marker + 2, end - marker - 2);
        SetFormat(editor, Strikeout, complete);
    }

    private void EditText(Action edit)
    {
        refreshing = true;
        try { edit(); }
        finally { refreshing = false; }
        RefreshFormattingButtons();
        PublishEdit();
    }

    private static bool IsChecklistItem(string text, int line) =>
        line + 1 < text.Length && text[line] is UncheckedItem or CheckedItem && text[line + 1] == ' ';

    private static int LineStart(string text, int position)
    {
        int before = Math.Min(text.Length, position) - 1;
        return before < 0 ? 0 : text.LastIndexOf('\n', before) + 1;
    }

    private static int LineEnd(string text, int start)
    {
        int end = text.IndexOf('\n', start);
        return end >= 0 ? end : text.Length;
    }

    private void RefreshFormattingButtons()
    {
        if (refreshing || toolbar is null || editor is null || !editor.IsHandleCreated) return;
        CharFormat selection = ReadSelectionFormat(editor);
        int? dividerFlags = editor is NoteTextEditor noteEditor && noteEditor.TryGetDivider(out var divider) ? divider.Flags : null;
        foreach ((uint flag, Button button) in styleButtons)
        {
            bool active = dividerFlags is int flags
                ? (flags & (flag switch { 1 => 1, 4 => 2, _ => 0 })) != 0
                : (selection.Mask & selection.Effects & flag) != 0;
            button.BackColor = active ? header.BackColor : toolbar.BackColor;
        }
    }

    private static CharFormat ReadSelectionFormat(RichTextBox editor)
    {
        CharFormat format = CharFormat.Empty;
        SendFormat(editor.Handle, 0x043A, new IntPtr(1), ref format);
        return format;
    }

    private void PublishEdit()
    {
        if (refreshing || editor is null) return;
        editorText = editor.Text;
        string rtf = editor.Rtf ?? "";
        board.ChangeText(note, NoteLinks.DisplayText(editor, editorText, rtf), rtf, this);
    }

    private void WireDragHandle(Control handle)
    {
        handle.MouseDown += StartDrag;
        handle.MouseMove += MoveDrag;
        handle.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) FinishDrag(handle); };
        handle.MouseCaptureChanged += (_, _) => { if (!handle.Capture) FinishDrag(handle); };
    }

    private void StartDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || sender is not Control handle || dragHandle is not null) return;
        FinishEntrance();
        if (handle == pin)
        {
            pinDragged = false;
            if (!board.CanDragPin) return;
        }
        else
        {
            if (!board.CanMoveNote(note)) return;
            BringToFront();
        }
        dragHandle = handle;
        dragStart = handle.PointToScreen(e.Location);
        dragOrigin = Location;
        handle.Capture = true;
    }

    private void MoveDrag(object? sender, MouseEventArgs e)
    {
        if (dragHandle is not Control handle || sender != handle) return;
        Point cursor = handle.PointToScreen(e.Location);
        if (handle == pin && !pinDragged)
        {
            Size threshold = SystemInformation.DragSize;
            var clickArea = new Rectangle(dragStart.X - threshold.Width / 2, dragStart.Y - threshold.Height / 2,
                threshold.Width, threshold.Height);
            if (clickArea.Contains(cursor)) return;
            pinDragged = true;
            handle.Cursor = Cursors.SizeAll;
            BringToFront();
        }
        var wanted = new Point(dragOrigin.X + cursor.X - dragStart.X, dragOrigin.Y + cursor.Y - dragStart.Y);
        Point next = board.LimitCardMove(wanted, Size);
        SetCardBounds(new Rectangle(next, Size));
    }

    private void FinishDrag(Control handle)
    {
        if (dragHandle != handle) return;
        dragHandle = null;
        handle.Capture = false;
        bool byPin = handle == pin;
        if (byPin)
        {
            handle.Cursor = Cursors.Hand;
            if (!pinDragged) return;
        }
        board.MoveNote(note, Left, Top, byPin);
        board.FinishPinnedMove(note);
    }

    private static void DrawResizeGrip(object? sender, PaintEventArgs e)
    {
        if (sender is not Control grip) return;
        using var pen = new Pen(BoardStyle.Muted, 1.3f);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int right = grip.Width - 7;
        int bottom = grip.Height - 5;
        int length = Math.Max(2, Math.Min(bottom, 11));
        e.Graphics.DrawLine(pen, right - length, bottom, right, bottom - length);
        e.Graphics.DrawLine(pen, right - Math.Min(6, length), bottom, right, bottom - Math.Min(6, length));
    }

    private void StartResize(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        FinishEntrance();
        BringToFront();
        resizeStart = Cursor.Position;
        resizeOrigin = Size;
        resizeGrip!.Capture = true;
    }

    private void ContinueResize(object? sender, MouseEventArgs e)
    {
        if (resizeStart is not Point start) return;
        Point cursor = Cursor.Position;
        SetCardSize(resizeOrigin.Width + cursor.X - start.X, resizeOrigin.Height + cursor.Y - start.Y);
    }

    private void FinishResize()
    {
        if (resizeStart is null) return;
        resizeStart = null;
        resizeGrip!.Capture = false;
        board.SaveCardSize(note, Size);
    }

    private static bool IsArrow(Keys key) => key is Keys.Left or Keys.Right or Keys.Up or Keys.Down;

    private void ResizeWithKeyboard(object? sender, KeyEventArgs e)
    {
        if (!IsArrow(e.KeyCode)) return;
        int step = LogicalToDeviceUnits(e.Shift ? 4 : 16);
        int width = Width + (e.KeyCode == Keys.Right ? step : e.KeyCode == Keys.Left ? -step : 0);
        int height = Height + (e.KeyCode == Keys.Down ? step : e.KeyCode == Keys.Up ? -step : 0);
        SetCardSize(width, height);
        board.SaveCardSize(note, Size);
        e.Handled = true;
    }

    private void SetCardSize(int width, int height)
    {
        Size size = board.LimitCardResize(note, Location, new Size(width, height));
        SetCardBounds(new Rectangle(Location, size));
    }

    private void SetCardBounds(Rectangle next)
    {
        Rectangle previous = Bounds;
        if (previous == next) return;
        Point previousPin = PinPointOnParent;
        Bounds = next;
        board.RedrawCard(this, previous, previousPin);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CharFormat
    {
        public uint Size, Mask, Effects;
        public int Height, Offset;
        public uint TextColor;
        public byte CharacterSet, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;

        public static CharFormat Empty => new() { Size = (uint)Marshal.SizeOf<CharFormat>(), FaceName = "" };
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendFormat(IntPtr window, uint message, IntPtr selection, ref CharFormat format);
}
