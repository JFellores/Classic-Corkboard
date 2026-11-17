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
    internal const uint Bold = 1, Strikeout = 8;
    private const uint FontSizeMask = 0x80000000;
    private const uint FontFaceMask = 0x20000000;
    private const int MaximumTextSize = 24;
    private readonly BoardWindow board;
    private readonly StickyNote note;
    private readonly RichTextBox? editor;
    private readonly NotePages? pages;
    private readonly NoteScrollBar? noteScrollBar;
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
    private readonly CorkMenu? textMenu;
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
            tableGrid = new TableGrid(table, () => board.ChangeTable(note, this), () => board.EditNote(note, "edit table"));
            writingArea.Controls.Add(tableGrid);
        }
        else
        {
            editor = new NoteTextEditor
            {
                Multiline = true, AcceptsTab = false, ScrollBars = RichTextBoxScrollBars.None, DetectUrls = false,
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, ForeColor = BoardStyle.Ink,
                AccessibleName = "Note text", Margin = Padding.Empty, HideSelection = false,
                IsOverChecklist = point => ChecklistMarkerAt(point) >= 0
            };
            editor.TextChanged += (_, _) => PublishEdit(runTextRules: true);
            editor.SelectionChanged += (_, _) => RefreshFormattingButtons();
            editor.LinkClicked += (_, e) => OpenLink(e.LinkText);
            editor.MouseClick += ClickChecklistItem;
            textMenu = BuildTextMenu(editor);
            editor.ContextMenuStrip = textMenu;
            writingArea.Controls.Add(editor);
            pages = new NotePages((NoteTextEditor)editor, writingArea);
            noteScrollBar = new NoteScrollBar((NoteTextEditor)editor);
            writingArea.Controls.Add(noteScrollBar);
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
                    else if (button == textSize) formattingMenu.Items.Add("Text appearance…", null, (_, _) => EditTextSize());
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
            formatting.DropDownItems.Add("Text appearance…", null, (_, _) => EditTextSize());
            var indent = new CorkSubmenuItem("List indent");
            indent.DropDownItems.Add("Compact", null, (_, _) => SetListIndent(compact: true));
            indent.DropDownItems.Add("Standard", null, (_, _) => SetListIndent(compact: false));
            indent.DropDownItems.Add(new ToolStripSeparator());
            indent.DropDownItems.Add(new ToolStripMenuItem("Indent", null, (_, _) => ChangeListLevel(1)) { ShortcutKeyDisplayString = "Tab" });
            indent.DropDownItems.Add(new ToolStripMenuItem("Outdent", null, (_, _) => ChangeListLevel(-1)) { ShortcutKeyDisplayString = "Shift+Tab" });
            formatting.DropDownItems.Add(indent);
            var freeForm = new CorkSubmenuItem("Free-form list");
            freeForm.DropDownItems.Add("Bullets", null, (_, _) => MakeFreeFormList(1));
            freeForm.DropDownItems.Add("Numbers", null, (_, _) => MakeFreeFormList(2));
            freeForm.DropDownItems.Add("Dashes", null, (_, _) => MakeFreeFormList(3));
            formatting.DropDownItems.Add(freeForm);
            var layout = ListLayoutMenu(editor!);
            formatting.DropDownItems.Add(layout);
            formatting.DropDownOpening += (_, _) => layout.Enabled = NoteListLayout.CanArrange(editor!);
            formatting.DropDownItems.Add(new ToolStripSeparator());
            var readingMode = new CorkSubmenuItem("Reading mode");
            var scroll = new ToolStripMenuItem("Scroll", null, (_, _) => board.ChangeNoteReadingMode(note, false));
            var paged = new ToolStripMenuItem("Pages", null, (_, _) => board.ChangeNoteReadingMode(note, true));
            readingMode.DropDownItems.AddRange([scroll, paged]);
            readingMode.DropDownOpening += (_, _) =>
            {
                scroll.Checked = !note.UsePages;
                paged.Checked = note.UsePages;
            };
            formatting.DropDownItems.Add(readingMode);
            formatting.DropDownItems.Add("Timestamp format…", null, (_, _) =>
            {
                var format = NoteTimestamp.ChooseFormat(FindForm() ?? board, note.TimestampFormat, note.SubtleTimestamps, writingArea.BackColor);
                if (format is { } choice) board.ChangeTimestampFormat(note, choice.Format, choice.Subtle);
            });
            menu.Items.Add(formatting);
            menu.Items.Add("Note rules…", null, (_, _) => EditRules());
            var reminder = menu.Items.Add("Set reminder…", null, (_, _) => EditReminder());
            reminder.Available = false;
            menu.Opening += (_, _) => reminder.Available = editor is { SelectionLength: > 0 };
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
        RefreshReadingMode();
    }

    public Point PinPointOnParent => new(Left + Padding.Left + header.Left + (pin?.Left ?? 0) + (pin?.Width ?? 28) / 2,
        Top + Padding.Top + header.Top + (pin?.Top ?? 0) + (pin?.Height ?? header.Height) / 2);
    public StickyNote Note => note;
    internal void RefreshReadingMode()
    {
        noteScrollBar?.SetActive(!note.UsePages);
        pages?.SetEnabled(note.UsePages);
    }

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
    internal void RefreshFromHistory()
    {
        RefreshLabel();
        RefreshPin();
        ApplyColor();
        ReadText();
        RefreshReadingMode();
        RefreshTable();
    }
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

    internal static void LoadNoteText(RichTextBox editor, StickyNote note)
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
        pages?.ApplyColor(paper, controls);
        noteScrollBar?.ApplyColor(paper, controls);
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
        if (board.HandleHistoryKey(keys, this)) return true;
        if (editor is NoteTextEditor { TurnPage: { } turn } && keys is (Keys.Alt | Keys.PageUp) or (Keys.Alt | Keys.PageDown))
        {
            turn(keys == (Keys.Alt | Keys.PageUp) ? -1 : 1);
            return true;
        }
        if (editor is not null && editor.Focused && keys is Keys.Tab or (Keys.Shift | Keys.Tab)
            && ChangeListLevel(keys == Keys.Tab ? 1 : -1)) return true;
        if (editor is not null && editor.Focused && keys == Keys.Enter)
        {
            bool handled = false;
            using var edit = board.EditNote(note, "continue list");
            EditText(() => handled = NoteParagraphs.ContinueChecklist(editor) || NoteParagraphs.HandleDashEnter(editor));
            if (handled) return true;
        }
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
            int marker = FindChecklistMarker(editor.Text, LineStart(editor.Text, editor.SelectionStart));
            if (marker >= 0)
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
        var rule = CaptureRule(NoteRules.Trigger.List);
        using var edit = board.EditContent(note, "change list style", rule);
        bool applied = false;
        EditText(() => applied = NoteParagraphs.CycleList(editor));
        if (applied) RunContentRule(NoteRules.Trigger.List, rule, useCurrentLine: true);
    }

    private void SetListIndent(bool compact)
    {
        if (editor is null) return;
        editor.Focus();
        using var edit = board.EditNote(note, "change list indent");
        EditText(() => NoteParagraphs.SetListIndent(editor, compact));
    }

    private bool ChangeListLevel(int direction)
    {
        if (editor is null) return false;
        editor.Focus();
        bool handled = false;
        using var edit = board.EditNote(note, "change list level");
        EditText(() => handled = NoteParagraphs.AdjustListLevel(editor, direction));
        return handled;
    }

    private void MakeFreeFormList(int style)
    {
        if (editor is null) return;
        editor.Focus();
        var rule = CaptureRule(NoteRules.Trigger.List);
        using var edit = board.EditContent(note, "change list style", rule);
        EditText(() => NoteParagraphs.MakeFreeFormList(editor, style));
        RunContentRule(NoteRules.Trigger.List, rule, useCurrentLine: true);
    }

    private void ArrangeList(int columns)
    {
        if (editor is null || !NoteListLayout.CanArrange(editor, columns == 1)) return;
        editor.Focus();
        using var edit = board.EditNote(note, "change list layout");
        bool arranged = false;
        EditText(() => arranged = NoteListLayout.Arrange(editor, columns));
        if (!arranged) board.ShowNotice("Items are too wide for that layout. Try fewer columns or widen the note.");
    }

    private CorkSubmenuItem ListLayoutMenu(RichTextBox text)
    {
        var layout = new CorkSubmenuItem("List layout");
        layout.DropDownItems.Add("Across a row", null, (_, _) => ArrangeList(0));
        for (int columns = 2; columns <= 4; columns++)
        {
            int count = columns;
            layout.DropDownItems.Add($"{count} columns", null, (_, _) => ArrangeList(count));
        }
        layout.DropDownItems.Add(new ToolStripSeparator());
        var stack = layout.DropDownItems.Add("Single column", null, (_, _) => ArrangeList(1));
        layout.DropDownOpening += (_, _) => stack.Enabled = NoteListLayout.CanArrange(text, restoring: true);
        return layout;
    }

    private void CycleDivider()
    {
        if (editor is null) return;
        editor.Focus();
        using var edit = board.EditNote(note, "change divider");
        EditText(() => NoteParagraphs.CycleDivider(editor));
    }

    private void InsertLink()
    {
        if (editor is null) return;
        int start = editor.SelectionStart;
        int length = editor.SelectionLength;
        var rule = CaptureRule(NoteRules.Trigger.Link);
        var link = NoteLinkPrompt.Show(FindForm() ?? board, NoteLinks.SelectedDisplayText(editor));
        if (link is not { } chosen) return;
        using var edit = board.EditContent(note, "insert link", rule);
        editor.Focus();
        editor.Select(start, length);
        int remainder = editor.TextLength - start - length;
        EditText(() => NoteLinks.Insert(editor, chosen.Alias, chosen.Destination, chosen.Color));
        int end = editor.TextLength - remainder;
        if (rule is not null && end > start)
        {
            int caret = editor.SelectionStart;
            editor.Select(start, end - start);
            if (!RunContentRule(NoteRules.Trigger.Link, rule)) editor.Select(caret, 0);
        }
    }

    private void InsertTimestamp()
    {
        if (editor is null) return;
        editor.Focus();
        var rule = CaptureRule(NoteRules.Trigger.Timestamp);
        using var edit = board.EditContent(note, "insert timestamp", rule);
        int inserted = 0, count = 0;
        // Use the editor's normal input path so divider rows and link boundaries are protected.
        EditText(() =>
        {
            ((NoteTextEditor)editor).MovePastDivider();
            int start = editor.SelectionStart;
            CharFormat original = ReadSelectionFormat(editor);
            Color ink = editor.SelectionColor;
            string stamp = NoteTimestamp.Format(DateTime.Now, note.TimestampFormat);
            inserted = start;
            count = stamp.Length;
            NoteLinks.InsertPlainText(editor, stamp, bold: !note.SubtleTimestamps,
                color: note.SubtleTimestamps ? TextSizePrompt.TextInk(writingArea.BackColor, 65) : null);
            editor.Select(start + stamp.Length, 0);
            SetFormat(editor, 1, (original.Mask & original.Effects & 1) != 0);
            editor.SelectionColor = ink.IsEmpty ? editor.ForeColor : ink;
        });
        if (rule is not null)
        {
            editor.Select(inserted, count);
            if (!RunContentRule(NoteRules.Trigger.Timestamp, rule)) editor.Select(inserted + count, 0);
        }
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
            textMenu?.Dispose();
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
            if (dividerFlag != 0)
            {
                using var edit = board.EditNote(note, "format divider");
                EditText(() => noteEditor.ToggleDividerFormat(dividerFlag));
            }
            return;
        }
        CharFormat current = ReadSelectionFormat(editor);
        bool enabled = (current.Mask & current.Effects & effect) == 0;
        var trigger = effect switch
        {
            Bold => NoteRules.Trigger.Bold, 2 => NoteRules.Trigger.Italic, 4 => NoteRules.Trigger.Underline, _ => NoteRules.Trigger.Strike
        };
        if (enabled && HasContentRule(trigger) && TryGetSelectedText(out var selection)
            && BoardWindow.ResolveContentRule(note, trigger, selection, editor.Text) is { } rule
            && board.TryApplyContentRule(note, editor, selection, rule, EditText,
                applyTrigger: box => SetFormat(box, effect, true))) return;
        SetFormat(editor, effect, enabled);
        RefreshFormattingButtons();
        PublishEdit();
    }

    internal static void SetFormat(RichTextBox editor, uint effect, bool enabled)
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
        CharFormat selection = ReadSelectionFormat(editor);
        int? current = (selection.Mask & FontSizeMask) == 0 ? null
            : Math.Clamp((int)Math.Round(selection.Height / 20.0), 1, MaximumTextSize);
        var appearance = TextSizePrompt.Show(FindForm() ?? board, current, MaximumTextSize, editor.Font,
            (selection.Mask & FontFaceMask) != 0 ? selection.FaceName : null, writingArea.BackColor, editor.SelectionColor);
        if (appearance is not { } choice) return;
        var rule = CaptureRule(NoteRules.Trigger.TextAppearance);
        using var edit = board.EditContent(note, "change text appearance", rule);

        editor.Focus();
        editor.Select(start, length);
        if (choice.Size is int size)
        {
            var change = CharFormat.Empty;
            change.Mask = FontSizeMask;
            change.Height = size * 20;
            SendFormat(editor.Handle, 0x0444, new IntPtr(1), ref change);
        }
        if (choice.Family is string family)
        {
            var change = CharFormat.Empty;
            change.Mask = FontFaceMask;
            change.FaceName = family;
            SendFormat(editor.Handle, 0x0444, new IntPtr(1), ref change);
        }
        if (choice.Ink is Color ink) editor.SelectionColor = ink;
        PublishEdit();
        if (choice.Size.HasValue || choice.Family is not null || choice.Ink.HasValue)
            RunContentRule(NoteRules.Trigger.TextAppearance, rule);
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
        if (start == end && FindChecklistMarker(text, firstLine) is int existing && existing >= 0)
        {
            ToggleChecklistItem(existing);
            return;
        }
        bool hasLinks = note.FormattedText?.Contains(@"\fldinst", StringComparison.Ordinal) == true;
        if (hasLinks)
        {
            (start, end) = NoteLinks.IncludeWholeLinks(editor, text, start, end);
            firstLine = LineStart(text, start);
        }

        var items = new List<(int Start, int End, int PrefixLength, bool BreakBefore, bool BreakAfter)>();
        for (int line = firstLine; ; )
        {
            int lineEnd = LineEnd(text, line);
            if (FindChecklistMarker(text, line) < 0)
            {
                int itemStart = start == end ? line : Math.Max(start, line);
                int itemEnd = start == end ? lineEnd : Math.Min(end, lineEnd);
                int prefixLength = NoteParagraphs.FreeFormPrefixLength(text.AsSpan(line, lineEnd - line));
                if (prefixLength > 0 && itemStart <= line + prefixLength && itemEnd >= line + prefixLength)
                    itemStart = line;
                else prefixLength = 0;
                if (start == end || (itemEnd > itemStart && !text.AsSpan(itemStart, itemEnd - itemStart).IsWhiteSpace()))
                    items.Add((itemStart, itemEnd, prefixLength, itemStart > line, itemEnd < lineEnd));
            }
            if (lineEnd + 1 >= end) break;
            line = lineEnd + 1;
        }
        if (items.Count == 0) return;

        var rule = CaptureRule(NoteRules.Trigger.ChecklistCreated);
        using var edit = board.EditContent(note, "create checklist items", rule);
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
                editor.Select(item.Start, item.PrefixLength);
                InsertChecklistText(prefix);
                int marker = item.Start + prefix.Length - 2;
                NoteParagraphs.ClearListMarker(editor, marker);
                editor.Select(marker, item.End - item.Start - item.PrefixLength + 2);
                SetFormat(editor, Strikeout, false);
                caret += prefix.Length - item.PrefixLength;
                if (i != items.Count - 1 && item.BreakAfter) caret++;
            }
            editor.Select(caret, 0);
            SetFormat(editor, Strikeout, false);
        });
        if (rule is not null)
        {
            int firstItem = items[0].Start + (items[0].BreakBefore ? 1 : 0);
            editor.Select(firstItem, Math.Max(0, caret - firstItem));
            if (!RunContentRule(NoteRules.Trigger.ChecklistCreated, rule)) editor.Select(caret, 0);
        }

        void InsertChecklistText(string value)
        {
            if (hasLinks) NoteLinks.InsertPlainText(editor, value);
            else editor.SelectedText = value;
        }
    }

    private void ClickChecklistItem(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int marker = ChecklistMarkerAt(e.Location);
        if (marker >= 0) ToggleChecklistItem(marker);
    }

    private int ChecklistMarkerAt(Point pointer)
    {
        if (editor is null || editorText.Length < 2) return -1;
        int line = LineStart(editorText, editor.GetCharIndexFromPosition(pointer));
        int marker = BoxAt(line);
        if (marker >= 0) return marker;
        // RichEdit can snap a hit in the left indent to the adjacent paragraph.
        int next = LineEnd(editorText, line) + 1;
        if (line > 0 && (marker = BoxAt(LineStart(editorText, line - 1))) >= 0) return marker;
        return next < editorText.Length ? BoxAt(next) : -1;

        int BoxAt(int start)
        {
            start = FindChecklistMarker(editorText, start);
            if (start < 0) return -1;
            Point left = editor.GetPositionFromCharIndex(start);
            Point right = editor.GetPositionFromCharIndex(start + 1);
            int nextRow = editor.GetFirstCharIndexFromLine(editor.GetLineFromCharIndex(start) + 1);
            int bottom = nextRow > start ? editor.GetPositionFromCharIndex(nextRow).Y
                : left.Y + Math.Max(editor.Font.Height + 3, right.X - left.X + 5);
            int boxRight = right.Y == left.Y ? right.X : left.X + editor.Font.Height;
            var target = Rectangle.FromLTRB(left.X - 2, left.Y, Math.Max(left.X + 5, boxRight + 2), bottom);
            return target.Contains(pointer) ? start : -1;
        }
    }

    private void ToggleChecklistItem(int marker)
    {
        if (editor is null) return;
        string text = editor.Text;
        if (!IsChecklistItem(text, marker)) return;
        bool complete = text[marker] == UncheckedItem;
        int line = ChecklistItemRule.LineAt(text, marker);
        if (complete && ChecklistItemRule.GetSettings(note, line).Destination is Guid destination
            && board.TryMoveCheckedItem(note, marker, destination)) return;
        int start = editor.SelectionStart;
        int length = editor.SelectionLength;
        if (!complete && HasContentRule(NoteRules.Trigger.Uncheck))
        {
            int end = LineEnd(text, marker);
            editor.Select(marker, end - marker);
            if (TryGetSelectedText(out var selection)
                && BoardWindow.ResolveContentRule(note, NoteRules.Trigger.Uncheck, selection, text) is { } rule
                && board.TryApplyContentRule(note, editor, selection, rule, EditText,
                    applyTrigger: box =>
                    {
                        SetChecklistState(box, marker, end, complete: false);
                        box.Select(marker, end - marker);
                    }, removeItemLine: true))
                return;
            editor.Select(start, length);
        }
        using var edit = board.EditNote(note, complete ? "check item" : "uncheck item");
        EditText(() =>
        {
            SetChecklistState(editor, marker, LineEnd(text, marker), complete);
            editor.Select(start, length);
        });
        board.TrackChecklistToggle(note, line, complete);
        if (complete && HasContentRule(NoteRules.Trigger.Check))
        {
            editor.Select(marker, LineEnd(editor.Text, marker) - marker);
            if (!RunContentRule(NoteRules.Trigger.Check)) editor.Select(start, length);
        }
    }

    private void EditRules()
    {
        if (editor is null) return;
        Form owner = FindForm() ?? board;
        if (!TryGetSelectedText(out var selection))
        {
            board.EditNoteRules(note, owner);
            return;
        }
        int first = LineStart(editor.Text, selection.NativeStart);
        bool checklist = FindChecklistMarker(editor.Text, first) >= 0
            && selection.NativeEnd <= LineEnd(editor.Text, first);
        board.EditNoteRules(note, owner, selection, checklist ? ChecklistItemRule.LineAt(editor.Text, first) : null);
    }

    private void EditReminder()
    {
        if (TryGetSelectedText(out var selection))
            board.ScheduleReminder(note, FindForm() ?? board, selection);
    }

    private CorkMenu BuildTextMenu(RichTextBox text)
    {
        var popup = new CorkMenu { ShowCheckMargin = false, ShowImageMargin = false };
        ToolStripItem rule = popup.Items.Add("Set rule…", null, (_, _) => EditRules());
        ToolStripItem reminder = popup.Items.Add("Set reminder…", null, (_, _) => EditReminder());
        ToolStripItem checklist = popup.Items.Add("Make checklist item", null, (_, _) => AddChecklistItems());
        ToolStripItem link = popup.Items.Add("Insert link…", null, (_, _) => InsertLink());
        var layout = ListLayoutMenu(text);
        popup.Items.Add(layout);
        popup.Items.Add(new ToolStripSeparator());
        ToolStripItem cut = Add("Cut", "Ctrl+X", text.Cut);
        ToolStripItem copy = Add("Copy", "Ctrl+C", () =>
        {
            if (TryGetSelectedText(out _)) text.Copy();
        });
        ToolStripItem paste = Add("Paste", "Ctrl+V", text.Paste);
        ToolStripItem all = Add("Select all", "Ctrl+A", text.SelectAll);
        popup.Opening += (_, _) =>
        {
            popup.SetSurface(writingArea.BackColor);
            bool selected = TryGetSelectedText(out _);
            rule.Text = selected ? "Set rule…" : "Note rules…";
            reminder.Available = selected;
            checklist.Available = selected;
            layout.Available = selected && NoteListLayout.CanArrange(text);
            cut.Enabled = copy.Enabled = text.SelectionLength > 0;
            all.Enabled = text.TextLength > 0;
            try { paste.Enabled = Clipboard.ContainsText() || Clipboard.ContainsData(DataFormats.Rtf); }
            catch (ExternalException) { paste.Enabled = true; }
        };
        return popup;

        ToolStripItem Add(string title, string keys, Action command)
        {
            var item = new ToolStripMenuItem(title, null, (_, _) =>
            {
                text.Focus();
                try { command(); }
                catch (ExternalException) { board.ShowNotice("The clipboard is busy. Try again."); }
            }) { ShortcutKeyDisplayString = keys };
            popup.Items.Add(item);
            return item;
        }
    }

    private bool TryGetSelectedText(out NoteRules.Selection selection)
    {
        selection = default;
        if (editor is null || editor.SelectionLength == 0) return false;
        int savedStart = editor.SelectionStart, savedLength = editor.SelectionLength;
        int start = savedStart, end = savedStart + savedLength;
        if (note.FormattedText?.Contains(@"\fldinst", StringComparison.Ordinal) == true)
            (start, end) = NoteLinks.IncludeWholeLinks(editor, editor.Text, start, end);
        editor.Select(start, end - start);
        string value = NoteParagraphs.RemoveDividerMarkers(NoteLinks.SelectedDisplayText(editor));
        editor.Select(0, start);
        int offset = NoteParagraphs.RemoveDividerMarkers(NoteLinks.SelectedDisplayText(editor)).Length;
        editor.Select(savedStart, savedLength);
        selection = new(start, end, offset, value);
        return !string.IsNullOrWhiteSpace(value);
    }

    private NoteRules.Settings? CaptureRule(NoteRules.Trigger trigger) =>
        editor is not null && HasContentRule(trigger) && TryGetSelectedText(out var selected)
            ? BoardWindow.ResolveContentRule(note, trigger, selected, editor.Text) : NoteRules.WholeRule(note, trigger);

    private bool HasContentRule(NoteRules.Trigger trigger) => note.ContentRules?.ContainsKey(trigger) == true
        || (trigger is NoteRules.Trigger.Check or NoteRules.Trigger.Uncheck
            ? note.ChecklistItems?.Any(item => trigger == NoteRules.Trigger.Check ? item.Settings is not null : item.Uncheck is not null) == true
            : note.TextSelectionRules?.Any(rule => rule.Trigger == trigger) == true);

    private bool RunContentRule(NoteRules.Trigger trigger, NoteRules.Settings? original = null, bool useCurrentLine = false)
    {
        if (editor is null || !HasContentRule(trigger)) return false;
        int start = editor.SelectionStart, length = editor.SelectionLength;
        if (length == 0 && useCurrentLine)
        {
            string text = editor.Text;
            int first = LineStart(text, start);
            editor.Select(first, LineEnd(text, first) - first);
        }
        if (TryGetSelectedText(out var active) && (original ?? BoardWindow.ResolveContentRule(note, trigger, active, editor.Text)) is { } rule
            && board.TryApplyContentRule(note, editor, active, rule, EditText))
            return true;
        editor.Select(start, length);
        return false;
    }

    public static bool ResetCheckedItems(StickyNote note, Func<int, bool>? shouldReset = null)
    {
        if (!note.Text.Contains(CheckedItem)) return false;

        using var editor = new RichTextBox { DetectUrls = false };
        LoadNoteText(editor, note);
        string text = editor.Text;
        bool changed = false;
        for (int line = 0, index = 0; line < text.Length; index++)
        {
            int end = LineEnd(text, line);
            int marker = FindChecklistMarker(text, line);
            if (marker >= 0 && text[marker] == CheckedItem && (shouldReset?.Invoke(index) ?? true))
            {
                SetChecklistState(editor, marker, end, complete: false);
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

    internal static void SetChecklistState(RichTextBox editor, int marker, int end, bool complete)
    {
        editor.Select(marker, 1);
        editor.SelectedText = (complete ? CheckedItem : UncheckedItem).ToString();
        editor.Select(marker, Math.Min(2, end - marker));
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

    internal static bool IsChecklistItem(string text, int line) =>
        line < text.Length && text[line] is UncheckedItem or CheckedItem
        && (line + 1 == text.Length || text[line + 1] is ' ' or '\u00a0' or '\t' or '\n');

    internal static int FindChecklistMarker(string text, int line)
    {
        int marker = line;
        while (marker < text.Length && text[marker] is ' ' or '\t' or '\u00a0') marker++;
        return IsChecklistItem(text, marker) ? marker : -1;
    }

    internal static int LineStart(string text, int position)
    {
        int before = Math.Min(text.Length, position) - 1;
        return before < 0 ? 0 : text.LastIndexOf('\n', before) + 1;
    }

    internal static int LineEnd(string text, int start)
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

    private void PublishEdit(bool runTextRules = false)
    {
        if (refreshing || editor is null) return;
        editorText = editor.Text;
        string rtf = editor.Rtf ?? "";
        board.ChangeText(note, NoteLinks.DisplayText(editor, editorText, rtf), rtf, this, runTextRules);
        pages?.Reflow();
        noteScrollBar?.RefreshScroll();
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
