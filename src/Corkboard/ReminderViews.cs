namespace Corkboard;

internal sealed class ReminderPopup : Form
{
    public ReminderPopup(Reminders reminders, NoteReminder item)
    {
        Text = "Corkboard reminder";
        AccessibleName = Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = BoardStyle.Paper;
        ForeColor = BoardStyle.Ink;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(358, 168);
        Controls.AddRange([
            new Label { Text = "Corkboard reminder", Bounds = new Rectangle(16, 12, 290, 24), ForeColor = BoardStyle.Brick },
            new Label { Text = item.Text, Bounds = new Rectangle(16, 41, 326, 49), AutoEllipsis = true },
            new Label { Text = reminders.NoteCaption(item), Bounds = new Rectangle(16, 92, 326, 22), ForeColor = BoardStyle.Muted, AutoEllipsis = true }
        ]);
        Add("Open note", 16, 82, () => reminders.Open(item));
        Add("Snooze 5 min", 102, 102, () => reminders.Snooze(item));
        Add("Stop", 208, 59, () => reminders.Stop(item));
        Add("Done", 271, 71, () => reminders.Done(item), primary: true);
        FormClosing += (_, args) =>
        {
            if (args.CloseReason == CloseReason.UserClosing)
            {
                args.Cancel = true;
                BeginInvoke(() => reminders.Stop(item));
            }
        };
        Load += (_, _) =>
        {
            Rectangle screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            int gap = LogicalToDeviceUnits(14);
            Location = new Point(screen.Right - Width - gap, screen.Bottom - Height - gap);
            BoardStyle.RoundCorners(this, LogicalToDeviceUnits(10));
        };

        void Add(string text, int x, int width, Action click, bool primary = false)
        {
            Button button = BoardStyle.Button(text, primary ? BoardStyle.Brick : BoardStyle.PanelTint,
                primary ? Color.White : BoardStyle.Ink, compactText: true);
            button.Bounds = new Rectangle(x, 122, width, 30);
            button.Click += (_, _) => click();
            Controls.Add(button);
        }
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { CreateParams style = base.CreateParams; style.ExStyle |= 0x08000000; return style; }
    }
}

internal static class ReminderViews
{
    public static (DateTimeOffset Due, ReminderRepeat? Repeat)? PickTime(Form owner, string text, NoteReminder? reminder = null)
    {
        using var dialog = new CorkDialog(reminder is null ? "Set reminder" : "Reschedule reminder", 339);
        var caption = new Label { Text = text, Bounds = new Rectangle(22, 58, 328, 41), AutoEllipsis = true };
        DateTime now = DateTime.Now;
        DateTime initial = reminder?.Due.LocalDateTime ?? now.AddMinutes(5);
        if (initial <= now) initial = now.AddMinutes(5);
        var dateTitle = new Label { Text = "Date", Bounds = new Rectangle(22, 106, 146, 21) };
        var date = new DateTimePicker { AccessibleName = "Reminder date", Format = DateTimePickerFormat.Short,
            Bounds = new Rectangle(22, 129, 146, 28), Value = initial };
        DateTime initialTime = reminder?.Repeat?.Time is { } savedTime ? initial.Date.Add(savedTime.ToTimeSpan()) : initial;
        var time = new DateTimePicker { AccessibleName = "Reminder time", Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm",
            ShowUpDown = true, Bounds = new Rectangle(183, 129, 167, 28), Value = initialTime };
        var repeat = new ComboBox
        {
            AccessibleName = "Reminder repeat", FlatStyle = FlatStyle.Flat,
            DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(22, 192, 328, 27)
        };
        repeat.Items.AddRange(["Once", "Daily", "Selected days"]);
        repeat.SelectedIndex = reminder?.Repeat?.Days switch
        {
            null => 0, ReminderDays.EveryDay => 1, _ => 2
        };
        var days = new Panel { Bounds = new Rectangle(22, 229, 328, 31), AccessibleName = "Days of the week" };
        for (int index = 0; index < ReminderRepeat.Week.Length; index++)
        {
            var day = ReminderRepeat.Week[index];
            var button = new CheckBox
            {
                Text = day.Short, AccessibleName = day.Label, Tag = day.Day,
                Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
                UseVisualStyleBackColor = false, Cursor = Cursors.Hand,
                Bounds = new Rectangle(index * 47, 0, 43, 30),
                Checked = (reminder?.Repeat?.Days ?? ReminderDays.None).HasFlag(day.Day)
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.CheckedBackColor = BoardStyle.Brick;
            StyleDay(button);

            days.Controls.Add(button);
        }
        var hint = new Label { ForeColor = BoardStyle.Muted };
        dialog.Controls.AddRange([caption, dateTitle, new Label { Text = "Time · local", Bounds = new Rectangle(183, 106, 167, 21) },
            date, time, new Label { Text = "Repeat", Bounds = new Rectangle(22, 169, 328, 21) }, repeat, days, hint]);
        var cancel = dialog.AddActions(reminder is null ? "Schedule" : "Save", acceptWithEnter: true);
        var save = (Button)dialog.AcceptButton!;

        (DateTimeOffset Due, ReminderRepeat? Repeat)? Selection()
        {
            DateTimeOffset current = DateTimeOffset.Now;
            var at = new TimeOnly(time.Value.Hour, time.Value.Minute);
            DateOnly first = DateOnly.FromDateTime(date.Value);
            if (repeat.SelectedIndex == 0)
            {
                DateTime local = first.ToDateTime(at);
                if (TimeZoneInfo.Local.IsInvalidTime(local)) return null;
                var due = new DateTimeOffset(local);
                return due > current ? (due, null) : null;
            }

            ReminderDays selected = repeat.SelectedIndex == 1 ? ReminderDays.EveryDay
                : days.Controls.OfType<CheckBox>().Where(button => button.Checked)
                    .Aggregate(ReminderDays.None, (mask, button) => mask | (ReminderDays)button.Tag!);
            if (selected == ReminderDays.None) return null;
            var pattern = new ReminderRepeat(at, selected);
            return pattern.Next(current, first) is { } next ? (next, pattern) : null;
        }

        void RefreshLayout()
        {
            bool custom = repeat.SelectedIndex == 2;
            days.Visible = custom;
            dateTitle.Text = repeat.SelectedIndex == 0 ? "Date" : "Starts on";
            int bottom = custom ? days.Bottom : repeat.Bottom;
            int gap = dialog.LogicalToDeviceUnits(10);
            hint.Bounds = new Rectangle(repeat.Left, bottom + gap, repeat.Width, dialog.LogicalToDeviceUnits(25));
            save.Top = cancel.Top = hint.Bottom + gap;
            dialog.ClientSize = new Size(dialog.ClientSize.Width, save.Bottom + dialog.LogicalToDeviceUnits(17));
            var selected = Selection();
            save.Enabled = selected.HasValue;
            hint.Text = selected is { } schedule ? $"Next reminder · {schedule.Due.LocalDateTime:g}"
                : custom && !days.Controls.OfType<CheckBox>().Any(button => button.Checked)
                    ? "Choose at least one day." : "Choose a valid time in the future.";
        }

        static void StyleDay(CheckBox button)
        {
            button.BackColor = button.Checked ? BoardStyle.Brick : BoardStyle.PanelTint;
            button.ForeColor = button.Checked ? Color.White : BoardStyle.Ink;
        }

        foreach (CheckBox button in days.Controls.OfType<CheckBox>())
            button.CheckedChanged += (_, _) => { StyleDay(button); RefreshLayout(); };
        repeat.SelectedIndexChanged += (_, _) => RefreshLayout();
        date.ValueChanged += (_, _) => RefreshLayout();
        time.ValueChanged += (_, _) => RefreshLayout();
        dialog.Load += (_, _) => RefreshLayout();
        RefreshLayout();
        return dialog.ShowDialog(owner) == DialogResult.OK ? Selection() : null;
    }

    public static void ShowCenter(Form owner, Reminders reminders)
    {
        using var dialog = new CorkDialog("Reminders", 435);
        using var menu = new CorkMenu();
        var notifications = BoardStyle.Button("Notifications", BoardStyle.PanelTint, BoardStyle.Ink);
        var scheduled = BoardStyle.Button("Scheduled", BoardStyle.PanelTint, BoardStyle.Ink);
        var options = BoardStyle.Button("⋯", BoardStyle.PanelTint, BoardStyle.Ink);
        options.AccessibleName = "Reminder options";
        notifications.Bounds = new Rectangle(22, 60, 127, 32);
        scheduled.Bounds = new Rectangle(154, 60, 115, 32);
        options.Bounds = new Rectangle(309, 60, 41, 32);
        var list = new ListBox { AccessibleName = "Reminders", Bounds = new Rectangle(22, 104, 328, 165),
            BackColor = Color.White, ForeColor = BoardStyle.Ink, BorderStyle = BorderStyle.None, IntegralHeight = false };
        var empty = new Label { Bounds = list.Bounds, TextAlign = ContentAlignment.MiddleCenter, ForeColor = BoardStyle.Muted };
        var detail = new Label { Bounds = new Rectangle(22, 276, 328, 68), AutoEllipsis = true };
        bool queue = false;
        Button open = Add("Open note", 22, 84, item => { dialog.Close(); reminders.Open(item); });
        Button later = Add("Snooze", 110, 99, item =>
        {
            if (!queue) reminders.Snooze(item);
            else if (PickTime(dialog, item.Text, item) is { } schedule) reminders.Reschedule(item, schedule.Due, schedule.Repeat);
        });
        Button done = Add("Done", 213, 61, reminders.Done);
        Button stop = Add("Stop", 279, 71, item =>
        {
            if (queue) reminders.Cancel(item);
            else reminders.Stop(item);
        });
        menu.Items.Add("Sound settings…", null, (_, _) => ShowSound(dialog, reminders));
        var clear = menu.Items.Add("Clear completed", null, (_, _) => reminders.ClearDone());
        options.Click += (_, _) =>
        {
            clear.Enabled = reminders.Items.Any(item => item.Done);
            menu.Show(options, new Point(options.Width, options.Height), ToolStripDropDownDirection.BelowLeft);
        };
        var close = BoardStyle.Button("Close", BoardStyle.PanelTint, BoardStyle.Ink);
        close.Bounds = new Rectangle(266, 391, 84, 30);
        close.DialogResult = DialogResult.Cancel;
        dialog.CancelButton = close;
        dialog.Controls.AddRange([notifications, scheduled, options, list, empty, detail, close]);
        notifications.Click += (_, _) => { queue = false; Refresh(); };
        scheduled.Click += (_, _) => { queue = true; Refresh(); };
        list.SelectedIndexChanged += (_, _) => UpdateSelection();
        list.DoubleClick += (_, _) => { if (list.SelectedItem is ReminderRow row) { dialog.Close(); reminders.Open(row.Item); } };
        reminders.Changed += Refresh;
        try { Refresh(); dialog.ShowDialog(owner); }
        finally { reminders.Changed -= Refresh; }

        Button Add(string text, int x, int width, Action<NoteReminder> action)
        {
            Button button = BoardStyle.Button(text, BoardStyle.PanelTint, BoardStyle.Ink, compactText: true);
            button.Bounds = new Rectangle(x, 349, width, 30);
            button.Click += (_, _) => { if (list.SelectedItem is ReminderRow row) action(row.Item); };
            dialog.Controls.Add(button);
            return button;
        }

        void Refresh()
        {
            Guid? selected = (list.SelectedItem as ReminderRow)?.Item.Id;
            ReminderRow[] rows = (queue ? reminders.Items.Where(item => !item.Done && !item.Fired.HasValue).OrderBy(item => item.Due)
                : reminders.Items.Where(item => item.Fired.HasValue).OrderByDescending(item => item.Fired))
                .Select(item => new ReminderRow(item)).ToArray();
            list.BeginUpdate();
            list.Items.Clear();
            list.Items.AddRange(rows);
            int index = Array.FindIndex(rows, row => row.Item.Id == selected);
            list.SelectedIndex = rows.Length == 0 ? -1 : Math.Max(0, index);
            list.EndUpdate();
            list.Visible = rows.Length > 0;
            empty.Visible = rows.Length == 0;
            empty.Text = queue ? "No reminders scheduled" : "No notifications yet";
            notifications.BackColor = queue ? BoardStyle.PanelTint : BoardStyle.Brick;
            notifications.ForeColor = queue ? BoardStyle.Ink : Color.White;
            notifications.Text = reminders.Unfinished > 0 ? $"Notifications ({reminders.Unfinished})" : "Notifications";
            scheduled.BackColor = queue ? BoardStyle.Brick : BoardStyle.PanelTint;
            scheduled.ForeColor = queue ? Color.White : BoardStyle.Ink;
            UpdateSelection();
        }

        void UpdateSelection()
        {
            NoteReminder? item = (list.SelectedItem as ReminderRow)?.Item;
            open.Visible = later.Visible = done.Visible = item is not null;
            stop.Visible = item is { Done: false } && (queue || reminders.IsAlerting(item));
            later.Enabled = done.Enabled = item is { Done: false };
            later.Text = queue ? "Reschedule" : "Snooze 5 min";
            stop.Text = queue ? "Cancel" : "Stop";
            detail.Text = item is null ? "" : $"{item.Text}\n{reminders.NoteCaption(item)}\n{(item.Fired ?? item.Due).LocalDateTime:g}{(item.Repeat is { } repeat ? $" · {repeat.Caption}" : "")}";
        }
    }

    private sealed record ReminderRow(NoteReminder Item)
    {
        public override string ToString() => $"{(Item.Done ? "✓  " : Item.Fired.HasValue ? "●  " : Item.Due.LocalDateTime.ToString("g") + "  ·  ")}{Item.Text.Replace('\n', ' ')}";
    }

    private static void ShowSound(Form owner, Reminders reminders)
    {
        using var dialog = new CorkDialog("Reminder sound", 344);
        var sound = new ComboBox { AccessibleName = "Alarm sound", DropDownStyle = ComboBoxStyle.DropDownList,
            Bounds = new Rectangle(22, 79, 230, 28) };
        foreach (string builtIn in ReminderAudio.BuiltIn) sound.Items.Add(new SoundChoice(builtIn, builtIn));
        ReminderSoundSettings original = reminders.Sound;
        int selected = Array.IndexOf(ReminderAudio.BuiltIn, original.Sound);
        if (selected < 0) selected = sound.Items.Add(new SoundChoice(original.Sound, "Custom sound"));
        sound.SelectedIndex = selected;
        var import = BoardStyle.Button("Add WAV…", BoardStyle.PanelTint, BoardStyle.Ink);
        import.Bounds = new Rectangle(257, 77, 93, 30);
        var volumeText = new Label { Bounds = new Rectangle(22, 120, 328, 22) };
        var volume = new TrackBar { AccessibleName = "Reminder volume", Minimum = 0, Maximum = 100, TickStyle = TickStyle.None,
            Value = Math.Clamp(original.Volume, 0, 100), Bounds = new Rectangle(22, 145, 328, 39) };
        var mode = new ComboBox { AccessibleName = "Reminder playback", DropDownStyle = ComboBoxStyle.DropDownList,
            Bounds = new Rectangle(22, 204, 328, 28) };
        mode.Items.AddRange(["Play once", "Repeat until stopped or snoozed"]);
        mode.SelectedIndex = original.Repeat ? 1 : 0;
        var preview = BoardStyle.Button("Preview", BoardStyle.PanelTint, BoardStyle.Ink);
        preview.Bounds = new Rectangle(22, 245, 91, 30);
        var hint = new Label { Text = "Custom WAVs can be up to 30 seconds.", Bounds = new Rectangle(121, 250, 229, 36), ForeColor = BoardStyle.Muted };
        dialog.Controls.AddRange([new Label { Text = "Sound", Location = new Point(22, 56) }, sound, import, volumeText, volume,
            new Label { Text = "Playback", Location = new Point(22, 181) }, mode, preview, hint]);
        dialog.AddActions("Save", acceptWithEnter: true);
        volume.ValueChanged += (_, _) => SetVolumeLabel();
        SetVolumeLabel();
        import.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = "Choose a reminder sound", Filter = "WAV audio (*.wav)|*.wav", RestoreDirectory = true };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            try
            {
                string name = reminders.ImportSound(picker.FileName);
                sound.SelectedIndex = sound.Items.Add(new SoundChoice(name, Path.GetFileNameWithoutExtension(picker.FileName)));
                hint.Text = "Custom sound ready.";
            }
            catch (Exception error) when (error is InvalidDataException || NoteStore.IsStorageError(error))
            {
                MessageBox.Show(dialog, error is InvalidDataException ? error.Message : "Corkboard couldn’t import that sound.",
                    "Reminder sound", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        preview.Click += (_, _) => reminders.PreviewSound(Read());
        try { if (dialog.ShowDialog(owner) == DialogResult.OK) reminders.SetSound(Read()); }
        finally { reminders.EndPreview(); }

        void SetVolumeLabel() => volumeText.Text = $"Volume · {volume.Value}%";
        ReminderSoundSettings Read() => new(((SoundChoice)sound.SelectedItem!).Key, volume.Value, mode.SelectedIndex == 1);
    }

    private sealed record SoundChoice(string Key, string Label)
    {
        public override string ToString() => Label;
    }
}
