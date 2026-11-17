using System.Globalization;

namespace Corkboard;

internal static class NoteRules
{
    internal enum Trigger
    {
        Check, Strike, Uncheck, Bold, Italic, Underline, TextAppearance, Link, List, ChecklistCreated, Timestamp,
        ReminderDue, ReminderDone, TextAdded, TextDeleted
    }

    private static readonly (Trigger Event, string Caption)[] Triggers =
    [
        (Trigger.Check, "An item is checked"),
        (Trigger.Strike, "Strikethrough is applied"),
        (Trigger.Uncheck, "An item is unchecked"),
        (Trigger.Bold, "Bold is applied"),
        (Trigger.Italic, "Italic is applied"),
        (Trigger.Underline, "Underline is applied"),
        (Trigger.TextAppearance, "Text appearance is changed"),
        (Trigger.Link, "A link is inserted"),
        (Trigger.List, "A list style is applied"),
        (Trigger.ChecklistCreated, "A checklist item is created"),
        (Trigger.Timestamp, "A timestamp is inserted"),
        (Trigger.ReminderDue, "A reminder comes due"),
        (Trigger.ReminderDone, "A reminder is marked done"),
        (Trigger.TextAdded, "Text is added"),
        (Trigger.TextDeleted, "Text is deleted")
    ];

    internal readonly record struct Selection(int NativeStart, int NativeEnd, int Offset, string Text);

    internal sealed record Destination(Guid? Id, string Label)
    {
        public override string ToString() => Label;
    }

    internal sealed record Settings(Guid? Destination, TimeOnly? Daily = null, int? DelayMinutes = null,
        bool Bold = false, bool Timestamp = false, bool Italic = false, bool Underline = false, bool Strike = false)
    {
        public static Settings For(StickyNote note) =>
            new(note.CheckedItemDestination, note.DailyChecklistReset, note.ChecklistResetDelayMinutes);
    }

    internal readonly record struct TextState(string Text, string? Rtf, ChecklistItemRule[]? Checklist, TextSelectionRule[]? Selections)
    {
        public static TextState Read(StickyNote note) => new(note.Text, note.FormattedText, note.ChecklistItems, note.TextSelectionRules);

        public static TextState Read(RichTextBox editor, StickyNote note, int? removeItem = null)
        {
            string rtf = editor.Rtf ?? "";
            string text = NoteLinks.DisplayText(editor, editor.Text, rtf);
            var copy = new StickyNote { Text = note.Text, ChecklistItems = note.ChecklistItems, TextSelectionRules = note.TextSelectionRules };
            if (removeItem is int line) ChecklistItemRule.RemoveLine(copy, line);
            else ChecklistItemRule.TrackEdit(copy, text);
            TextSelectionRule.TrackEdit(copy, text);
            return new(text, rtf, copy.ChecklistItems, copy.TextSelectionRules);
        }

        public void Apply(StickyNote note)
        {
            note.Text = Text;
            note.FormattedText = Rtf;
            note.ChecklistItems = Checklist;
            note.TextSelectionRules = Selections;
        }
    }

    public static bool Show(Form owner, IReadOnlyList<Destination> connected, StickyNote note, string? selection,
        bool checklistSelection, Func<Trigger, Settings?> existing, out Dictionary<Trigger, Settings> result)
    {
        bool wholeNote = selection is null;
        bool checklist = wholeNote || checklistSelection;
        using var dialog = new CorkDialog("Note rules", 518);
        var scope = new Panel { Bounds = new Rectangle(22, 55, 328, 47), BackColor = Color.FromArgb(241, 229, 209) };
        scope.Resize += (_, _) => BoardStyle.RoundCorners(scope, scope.LogicalToDeviceUnits(7));
        BoardStyle.RoundCorners(scope, 7);
        scope.Controls.Add(new Label
        {
            Text = wholeNote ? "Applies to" : "Selected text",
            ForeColor = BoardStyle.Muted, Bounds = new Rectangle(10, 4, 304, 19)
        });
        using var scopeFont = new Font("Segoe UI Semibold", 10F);
        scope.Controls.Add(new Label
        {
            Text = wholeNote ? "Whole note" : selection,
            Font = scopeFont, Bounds = new Rectangle(10, 23, 306, 21), AutoEllipsis = true
        });

        var trigger = Picker("Rule trigger", 132);
        trigger.Width = 236;
        var share = BoardStyle.Button("Also when…", BoardStyle.PanelTint, BoardStyle.Ink, compactText: true);
        share.Bounds = new Rectangle(264, 130, 86, 28);
        share.AccessibleName = "Use these actions for more triggers";
        var when = LabelAt("When", 110);
        var pendingHint = new Label { ForeColor = BoardStyle.Brick, AutoEllipsis = false };
        var available = Triggers.Where(option => checklist || option.Event is not (Trigger.Check or Trigger.Uncheck)).ToArray();
        trigger.Items.AddRange(available.Select(option => (object)option.Caption).ToArray());
        trigger.SelectedIndex = 0;
        trigger.DrawMode = DrawMode.OwnerDrawFixed;
        trigger.DropDownHeight = 260;
        var inherited = new CheckBox
        {
            Text = "Use the whole-note rule", AccessibleName = "Use the whole-note rule",
            Bounds = new Rectangle(22, 163, 328, 28),
            Visible = !wholeNote
        };
        var inheritedSummary = new Label
        {
            Bounds = new Rectangle(22, 199, 328, 68), ForeColor = BoardStyle.Ink, Padding = new Padding(11, 8, 8, 5),
            BackColor = Color.FromArgb(241, 229, 209)
        };
        inheritedSummary.Resize += (_, _) => BoardStyle.RoundCorners(inheritedSummary, inheritedSummary.LogicalToDeviceUnits(7));
        BoardStyle.RoundCorners(inheritedSummary, 7);
        int actionTop = wholeNote ? 175 : 203;
        var actions = LabelAt("Then", actionTop);
        actions.Width = 100;
        var changeTrigger = BoardStyle.Button("Change trigger…", BoardStyle.PanelTint, BoardStyle.Ink, compactText: true);
        changeTrigger.Bounds = new Rectangle(224, actionTop - 5, 126, 26);
        var moveLabel = LabelAt("Move to connected note", actionTop + 90);
        var move = Picker("Move to connected note", actionTop + 112);
        move.Items.Add(new Destination(null, "Keep in this note"));
        move.Items.AddRange(connected.Cast<object>().ToArray());
        var destinationHint = new Label
        {
            ForeColor = BoardStyle.Muted, Bounds = new Rectangle(22, actionTop + 142, 328, 22)
        };
        int resetTop = actionTop + 168;
        var resetLabel = LabelAt("Checklist reset", resetTop);
        var reset = Picker("Checklist reset", resetTop + 23);
        reset.Items.AddRange(["Off", "Every day at a set time", "After each item is checked"]);
        var clock = new TextBox { AccessibleName = "Daily reset time", MaxLength = 5, Bounds = new Rectangle(22, resetTop + 53, 90, 28) };
        var localTime = new Label { Text = "Local time · 24-hour", ForeColor = BoardStyle.Muted, Bounds = new Rectangle(122, resetTop + 56, 228, 24) };
        var duration = new NumericUpDown
        {
            AccessibleName = "Reset delay", Minimum = 1, Maximum = 1440, Bounds = new Rectangle(22, resetTop + 53, 90, 28)
        };
        var units = Picker("Delay unit", resetTop + 53);
        units.Bounds = new Rectangle(122, resetTop + 53, 100, 28);
        units.Items.AddRange(["Minutes", "Hours", "Days"]);
        units.SelectedIndex = 0;
        var resetHint = new Label { ForeColor = BoardStyle.Muted, Bounds = new Rectangle(22, resetTop + 83, 328, 22) };
        var bold = ActionBox("Make the text bold", "Bold", 22, actionTop + 24, FontStyle.Bold);
        var italic = ActionBox("Italicize text", "Italic", 105, actionTop + 24, FontStyle.Italic);
        var underline = ActionBox("Underline text", "Underline", 188, actionTop + 24, FontStyle.Underline);
        var strike = ActionBox("Strike through text", "Strike", 271, actionTop + 24, FontStyle.Strikeout);
        var stamp = ActionBox("Add a timestamp", "Add timestamp", 22, actionTop + 55, FontStyle.Regular, width: 124);

        dialog.Controls.AddRange([scope, when, trigger, share, inherited, inheritedSummary, moveLabel, move, destinationHint, pendingHint,
            resetLabel, reset, clock, localTime, duration, units, resetHint, actions, changeTrigger, bold, stamp, italic, underline, strike]);
        dialog.AddActions("Save", acceptWithEnter: true);
        var save = (Button)dialog.AcceptButton!;
        var cancel = (Button)dialog.CancelButton!;
        var remove = BoardStyle.Button("Remove rule", BoardStyle.PanelTint, BoardStyle.Brick, compactText: true);
        remove.Bounds = new Rectangle(22, cancel.Top, 105, cancel.Height);
        dialog.Controls.Add(remove);
        save.Anchor = cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var drafts = new Dictionary<Trigger, Settings>();
        foreach (var option in available)
            if (existing(option.Event) is { } rule) drafts[option.Event] = rule;
        int previousIndex = Math.Max(0, Array.FindIndex(available, option => IsConfigured(option.Event)));
        trigger.SelectedIndex = previousIndex;
        Trigger current = available[previousIndex].Event;
        bool isCheck = current == Trigger.Check;
        bool loading = false;
        Trigger[] pending = [];
        trigger.DrawItem += (_, args) =>
        {
            if (args.Index < 0) return;
            bool dropDown = (args.State & DrawItemState.ComboBoxEdit) == 0;
            if (dropDown) args.DrawBackground();
            else args.Graphics.FillRectangle(Brushes.White, args.Bounds);
            var option = available[args.Index];
            bool marked = IsConfigured(option.Event);
            var bounds = Rectangle.Inflate(args.Bounds, -4, 0);
            Color ink = dropDown ? args.ForeColor : BoardStyle.Ink;
            TextRenderer.DrawText(args.Graphics, option.Caption, args.Font, bounds, ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (marked)
                TextRenderer.DrawText(args.Graphics, "●", args.Font, bounds, dropDown && (args.State & DrawItemState.Selected) != 0 ? ink : BoardStyle.Brick,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            if (dropDown) args.DrawFocusRectangle();
        };

        void LoadDraft()
        {
            loading = true;
            Settings? configured = drafts.GetValueOrDefault(current);
            Settings draft = configured ?? (wholeNote ? null : WholeRule(note, current)) ?? new Settings(null);
            int index = move.Items.OfType<Destination>().ToList().FindIndex(item => item.Id == draft.Destination);
            if (index < 0) index = move.Items.Add(new Destination(draft.Destination, "Destination unavailable · move paused"));
            move.SelectedIndex = index;
            reset.SelectedIndex = draft.Daily.HasValue ? 1 : draft.DelayMinutes.HasValue ? 2 : 0;
            clock.Text = (draft.Daily ?? TimeOnly.MinValue).ToString("HH:mm", CultureInfo.InvariantCulture);
            int minutes = Math.Clamp(draft.DelayMinutes ?? 30, 1, 1440 * 30);
            units.SelectedIndex = minutes % 1440 == 0 ? 2 : minutes % 60 == 0 ? 1 : 0;
            duration.Maximum = units.SelectedIndex == 2 ? 30 : units.SelectedIndex == 1 ? 720 : 1440;
            duration.Value = Math.Clamp(minutes / UnitMinutes(), (int)duration.Minimum, (int)duration.Maximum);
            bold.Checked = draft.Bold;
            stamp.Checked = draft.Timestamp;
            italic.Checked = draft.Italic;
            underline.Checked = draft.Underline;
            strike.Checked = draft.Strike;
            inherited.Checked = !wholeNote && configured is null;
            loading = false;
            Update();
        }

        int UnitMinutes() => units.SelectedIndex switch { 1 => 60, 2 => 1440, _ => 1 };
        bool ReadTime(out TimeOnly time) => TimeOnly.TryParseExact(clock.Text, ["HH:mm", "H:mm"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
        Settings ReadDraft() => new(((Destination)move.SelectedItem!).Id,
            isCheck && reset.SelectedIndex == 1 && ReadTime(out TimeOnly time) ? time : null,
            isCheck && reset.SelectedIndex == 2 ? (int)duration.Value * UnitMinutes() : null,
            current != Trigger.Bold && bold.Checked,
            current != Trigger.Timestamp && stamp.Checked,
            current != Trigger.Italic && italic.Checked,
            current != Trigger.Underline && underline.Checked,
            !isCheck && current != Trigger.Strike && strike.Checked);

        void Update()
        {
            if (loading) return;
            bool editable = wholeNote || !inherited.Checked;
            remove.Visible = IsConfigured(current);
            changeTrigger.Visible = editable && IsConfigured(current);
            changeTrigger.Enabled = pending.Length == 0;
            share.Enabled = editable || !wholeNote;
            inheritedSummary.Visible = !editable;
            inheritedSummary.Text = InheritedText();
            actions.Visible = move.Visible = moveLabel.Visible = editable;
            resetLabel.Visible = reset.Visible = editable && isCheck;
            resetHint.Visible = editable && isCheck && reset.SelectedIndex != 0;
            stamp.Visible = editable && current != Trigger.Timestamp;
            bold.Visible = editable && current != Trigger.Bold;
            italic.Visible = editable && current != Trigger.Italic;
            underline.Visible = editable && current != Trigger.Underline;
            strike.Visible = editable && !isCheck && current != Trigger.Strike;
            bool showClock = editable && isCheck && reset.SelectedIndex == 1;
            clock.Visible = localTime.Visible = showClock;
            duration.Visible = units.Visible = editable && isCheck && reset.SelectedIndex == 2;
            bool valid = !showClock || ReadTime(out _);
            string? missing = pending.Length > 0 ? ShareProblem(pending, ReadDraft()) : null;
            save.Enabled = (!editable || valid) && missing is null;
            if (editable && current == Trigger.TextDeleted && Shareable(ReadDraft()) && !ReadDraft().Destination.HasValue)
            {
                missing = "Choose a connected note to receive the deleted text.";
                save.Enabled = false;
            }
            pendingHint.Visible = missing is not null;
            pendingHint.Text = missing ?? "";
            resetHint.Text = !valid ? "Enter a time from 00:00 to 23:59."
                : reset.SelectedIndex == 2 ? "Each checked item gets its own timer."
                : reset.SelectedIndex == 1 ? "Unchecks completed items at this time." : "";
            resetHint.ForeColor = valid ? BoardStyle.Muted : BoardStyle.Brick;
            destinationHint.Text = move.SelectedItem is Destination { Id: Guid id } && !connected.Any(item => item.Id == id)
                ? "Reconnect the thread to resume moves."
                : current == Trigger.TextDeleted ? "Choose where to archive the removed text."
                : connected.Count == 0 ? "Connect another text note with a thread." : "";
            bool showDestinationHint = editable && destinationHint.Text.Length > 0;
            destinationHint.Visible = showDestinationHint;

            Control last = !editable ? inheritedSummary
                : isCheck ? reset.SelectedIndex == 0 ? reset : resetHint
                : showDestinationHint ? destinationHint : move;
            if (missing is not null)
            {
                pendingHint.Bounds = new Rectangle(when.Left, last.Bottom + dialog.LogicalToDeviceUnits(8),
                    move.Width, dialog.LogicalToDeviceUnits(38));
                last = pendingHint;
            }
            int footerTop = last.Bottom + dialog.LogicalToDeviceUnits(18);
            int height = footerTop + Math.Max(save.Height, cancel.Height) + dialog.LogicalToDeviceUnits(18);
            dialog.SuspendLayout();
            save.Top = cancel.Top = remove.Top = footerTop;
            dialog.ClientSize = new Size(dialog.ClientSize.Width, height);
            dialog.ResumeLayout();
        }

        string InheritedText()
        {
            Settings? rule = WholeRule(note, current);
            if (rule is null || rule == new Settings(null)) return "No actions set for this trigger on the whole note.";
            var summary = new List<string>();
            if (rule.Bold) summary.Add("Bold");
            if (rule.Italic) summary.Add("Italic");
            if (rule.Underline) summary.Add("Underline");
            if (rule.Strike) summary.Add("Strikethrough");
            if (rule.Timestamp) summary.Add("Timestamp");
            if (rule.Destination is Guid id)
                summary.Add(connected.FirstOrDefault(item => item.Id == id) is { } target ? $"Move to {target.Label}" : "Move paused");
            if (rule.Daily is TimeOnly time) summary.Add($"Reset at {time:HH:mm}");
            if (rule.DelayMinutes is int minutes) summary.Add($"Reset after {minutes} min");
            return string.Join(" · ", summary);
        }

        trigger.SelectedIndexChanged += (_, _) =>
        {
            if (loading || trigger.SelectedIndex == previousIndex) return;
            if (!save.Enabled)
            {
                loading = true;
                trigger.SelectedIndex = previousIndex;
                loading = false;
                if (pending.Length == 0) clock.Focus();
                return;
            }
            SaveDraft();
            previousIndex = trigger.SelectedIndex;
            current = available[previousIndex].Event;
            isCheck = current == Trigger.Check;
            LoadDraft();
        };
        remove.Click += (_, _) =>
        {
            drafts.Remove(current);
            pending = [];
            LoadDraft();
            RefreshTriggerSummary();
        };
        changeTrigger.Click += (_, _) =>
        {
            if (!save.Enabled) return;
            Settings source = ReadDraft();
            var targets = available.Select((option, index) => (option.Event, option.Caption, Index: index))
                .Where(option => option.Event != current && !IsConfigured(option.Event)
                    && (option.Event != Trigger.TextDeleted || source.Destination.HasValue)
                    && Shareable(CopyActionsTo(option.Event, source))).ToArray();
            using var chooser = new CorkDialog("Change trigger", 213);
            var target = Picker("New trigger", 86);
            target.Items.AddRange(targets.Select(option => (object)option.Caption).ToArray());
            if (targets.Length > 0) target.SelectedIndex = 0;
            chooser.Controls.AddRange([LabelAt("Run this rule when", 62), target,
                new Label
                {
                    Bounds = new Rectangle(22, 118, 328, 38), ForeColor = BoardStyle.Muted,
                    Text = targets.Length == 0 ? "No unused trigger can run the current actions."
                        : source.Daily.HasValue || source.DelayMinutes.HasValue
                            ? "The checklist reset stays on the checked trigger." : "Moves this rule to the selected trigger."
                }]);
            chooser.AddActions("Change", acceptWithEnter: true);
            ((Button)chooser.AcceptButton!).Enabled = targets.Length > 0;
            if (chooser.ShowDialog(dialog) != DialogResult.OK || target.SelectedIndex < 0) return;
            var selected = targets[target.SelectedIndex];
            drafts[selected.Event] = CopyActionsTo(selected.Event, source);
            if (current == Trigger.Check && (source.Daily.HasValue || source.DelayMinutes.HasValue))
                drafts[current] = new Settings(null, source.Daily, source.DelayMinutes);
            else drafts.Remove(current);
            loading = true;
            trigger.SelectedIndex = previousIndex = selected.Index;
            current = selected.Event;
            isCheck = current == Trigger.Check;
            LoadDraft();
            RefreshTriggerSummary();
        };
        share.Click += (_, _) =>
        {
            if (isCheck && reset.SelectedIndex == 1 && !ReadTime(out _)) { clock.Focus(); return; }
            SaveDraft(commitShared: false);
            Settings actionsToShare = ReadDraft();
            using var chooser = new CorkDialog("Also run these actions when", 370);
            var list = new CheckedListBox
            {
                Bounds = new Rectangle(22, 60, 328, 212), CheckOnClick = true, IntegralHeight = false,
                AccessibleName = "Additional triggers", BackColor = Color.White, BorderStyle = BorderStyle.None
            };
            var choices = available.Where(option => option.Event != current).ToArray();
            var copies = choices.Select(option => CopyActionsTo(option.Event, actionsToShare)).ToArray();
            var matching = new HashSet<Trigger>();
            foreach (var option in choices)
            {
                Settings copy = CopyActionsTo(option.Event, actionsToShare);
                bool matches = IsConfigured(option.Event) && drafts[option.Event] == copy;
                bool selected = pending.Length > 0 ? pending.Contains(option.Event) : matches;
                if (matches && selected) matching.Add(option.Event);
                string caption = matches ? $"{option.Caption} (same)"
                    : IsConfigured(option.Event) ? $"{option.Caption} (replace)" : option.Caption;
                list.Items.Add(caption, selected);
            }
            chooser.Controls.Add(list);
            var hint = new Label
            {
                ForeColor = BoardStyle.Muted, Bounds = new Rectangle(22, 278, 328, 37)
            };
            chooser.Controls.Add(hint);
            chooser.AddActions("Apply", acceptWithEnter: true);
            var apply = (Button)chooser.AcceptButton!;
            list.ItemCheck += (_, change) => UpdateShare(change.Index, change.NewValue);
            UpdateShare();
            if (chooser.ShowDialog(dialog) != DialogResult.OK) return;
            pending = list.CheckedIndices.Cast<int>().Select(index => choices[index].Event).ToArray();
            foreach (Trigger action in matching)
                if (!pending.Contains(action)) drafts.Remove(action);
            if (pending.Length > 0) inherited.Checked = false;
            SaveDraft(commitShared: false);
            RefreshTriggerSummary();
            Update();

            void UpdateShare(int changing = -1, CheckState state = CheckState.Unchecked)
            {
                int selected = 0;
                for (int i = 0; i < choices.Length; i++)
                {
                    if (!(i == changing ? state == CheckState.Checked : list.GetItemChecked(i))) continue;
                    selected++;
                }
                apply.Enabled = selected > 0 || pending.Length > 0 || matching.Count > 0;
                hint.Text = Shareable(actionsToShare)
                    ? "Uncheck a matching trigger to remove its rule. Other rules are kept."
                    : "Choose your triggers, then add an action in Note rules.";
            }
        };
        foreach (ComboBox box in new[] { move, reset }) box.SelectedIndexChanged += (_, _) => Update();
        units.SelectedIndexChanged += (_, _) =>
        {
            if (!loading) duration.Maximum = units.SelectedIndex == 2 ? 30 : units.SelectedIndex == 1 ? 720 : 1440;
        };
        clock.TextChanged += (_, _) => Update();
        inherited.CheckedChanged += (_, _) => Update();
        foreach (CheckBox action in new[] { bold, italic, underline, strike, stamp }) action.CheckedChanged += (_, _) => Update();
        LoadDraft();
        RefreshTriggerSummary();
        dialog.Load += (_, _) => Update();
        dialog.DpiChanged += (_, _) => dialog.BeginInvoke(Update);
        dialog.Shown += (_, _) => trigger.Focus();
        bool saved = dialog.ShowDialog(owner) == DialogResult.OK;
        SaveDraft();
        result = drafts;
        return saved;

        void SaveDraft(bool commitShared = true)
        {
            Settings draft = ReadDraft();
            if (commitShared && pending.Length > 0 && ShareProblem(pending, draft) is null)
            {
                foreach (Trigger action in pending) drafts[action] = CopyActionsTo(action, draft);
                pending = [];
            }
            if (inherited.Checked || (wholeNote && !isCheck && draft == new Settings(null))) drafts.Remove(current);
            else drafts[current] = draft;
            RefreshTriggerSummary();
        }

        void RefreshTriggerSummary()
        {
            int count = drafts.Count(pair => !wholeNote || pair.Value != new Settings(null));
            when.Text = pending.Length > 0 ? $"When · {pending.Length} additional selected"
                : count == 0 ? "When" : $"When · {count} configured";
            trigger.Invalidate();
        }

        bool IsConfigured(Trigger action) => drafts.TryGetValue(action, out Settings? configured)
            && (!wholeNote || configured != new Settings(null));

        static bool Shareable(Settings value) => value.Destination.HasValue || value.Bold || value.Italic
            || value.Underline || value.Strike || value.Timestamp;

        string? ShareProblem(Trigger[] selected, Settings draft)
        {
            if (!Shareable(draft)) return "Choose a Then action or a destination for the selected triggers.";
            if (selected.Any(action => !Shareable(CopyActionsTo(action, draft))
                || (action == Trigger.TextDeleted && !draft.Destination.HasValue)))
                return "Add an action every selected trigger can run, or change the selection in Also when.";
            return null;
        }

        Settings CopyActionsTo(Trigger action, Settings source)
        {
            // A reset schedule belongs to the checked trigger.
            Settings? schedule = action == Trigger.Check ? drafts.GetValueOrDefault(action) ?? WholeRule(note, action) : null;
            return source with
            {
                Daily = schedule?.Daily, DelayMinutes = schedule?.DelayMinutes,
                Bold = action != Trigger.Bold && source.Bold,
                Italic = action != Trigger.Italic && source.Italic,
                Underline = action != Trigger.Underline && source.Underline,
                Strike = action is not (Trigger.Strike or Trigger.Check) && source.Strike,
                Timestamp = action != Trigger.Timestamp && source.Timestamp
            };
        }

        ComboBox Picker(string name, int y) => new()
        {
            AccessibleName = name, Bounds = new Rectangle(22, y, 328, 28),
            FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList
        };
        Label LabelAt(string value, int y) => new() { Text = value, Bounds = new Rectangle(22, y, 328, 22) };
        CheckBox ActionBox(string text, string caption, int x, int y, FontStyle style, int width = 77)
        {
            var button = new CheckBox
            {
                Text = text, AccessibleName = text, Bounds = new Rectangle(x, y, width, 26),
                Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false, TextAlign = ContentAlignment.MiddleCenter
            };
            // The visible chip is drawn separately to keep the full command available to accessibility tools.
            var font = new Font(dialog.Font, style);
            button.Font = font;
            button.Disposed += (_, _) => font.Dispose();
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.CheckedBackColor = BoardStyle.Brick;
            button.Paint += (_, args) =>
            {
                Color fill = button.Checked ? BoardStyle.Brick : BoardStyle.PanelTint;
                using var brush = new SolidBrush(fill);
                args.Graphics.FillRectangle(brush, button.ClientRectangle);
                TextRenderer.DrawText(args.Graphics, caption, button.Font, button.ClientRectangle,
                    button.Checked ? Color.White : BoardStyle.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (button.Focused) ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(button.ClientRectangle, -3, -3));
            };
            button.Resize += (_, _) => BoardStyle.RoundCorners(button, button.LogicalToDeviceUnits(6));
            BoardStyle.RoundCorners(button, 6);
            return button;
        }
    }

    public static Settings? WholeRule(StickyNote note, Trigger trigger) =>
        trigger == Trigger.Check ? Settings.For(note) with
        {
            Bold = note.ContentRules?.GetValueOrDefault(trigger)?.Bold ?? false,
            Italic = note.ContentRules?.GetValueOrDefault(trigger)?.Italic ?? false,
            Underline = note.ContentRules?.GetValueOrDefault(trigger)?.Underline ?? false,
            Timestamp = note.ContentRules?.GetValueOrDefault(trigger)?.Timestamp ?? false
        } : note.ContentRules?.GetValueOrDefault(trigger);
}
