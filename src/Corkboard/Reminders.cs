namespace Corkboard;

internal sealed record NoteReminder(Guid Id, Guid Board, Guid Note, string Text, DateTimeOffset Due,
    DateTimeOffset? Fired = null, bool Done = false, int? Offset = null, bool DueActionsRun = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ReminderRepeat? Repeat = null);

internal sealed class Reminders(CorkboardApp app, BoardData data, string soundFolder) : IDisposable
{
    private readonly System.Windows.Forms.Timer timer = new();
    private readonly ReminderAudio audio = new(soundFolder);
    private readonly Queue<Guid> alerts = new();
    private ReminderPopup? popup;
    private Guid? active;
    private bool previewing;
    public event Action? Changed;
    public IReadOnlyList<NoteReminder> Items => data.Reminders is { } items ? items : [];
    public ReminderSoundSettings Sound => data.ReminderSound ?? new();
    public int Unfinished => Items.Count(item => item.Fired.HasValue && !item.Done);
    public bool IsAlerting(NoteReminder item) => active == item.Id || alerts.Contains(item.Id);

    public void Start()
    {
        timer.Tick += (_, _) => Check(DateTimeOffset.Now);
        Check(DateTimeOffset.Now);
    }

    public void Check(DateTimeOffset now)
    {
        timer.Stop();
        bool changed = false;
        foreach (NoteReminder item in Items.Where(item => !item.Done && !item.Fired.HasValue && item.Due <= now)
            .OrderBy(item => item.Due).ToArray())
        {
            alerts.Enqueue(item.Id);
            Replace(item, item with { Fired = now, DueActionsRun = true, Repeat = null });
            if (NextOccurrence(item, now) is { } next) Put(next);
            if (!item.DueActionsRun) app.ApplyReminderRule(item, NoteRules.Trigger.ReminderDue);
            changed = true;
        }
        Schedule(now);
        ShowNext();
        if (changed) Changed?.Invoke();
    }

    private void Schedule(DateTimeOffset now)
    {
        timer.Stop();
        DateTimeOffset? next = Items.Where(item => !item.Done && !item.Fired.HasValue).Select(item => (DateTimeOffset?)item.Due).Min();
        if (next is not { } due) return;
        timer.Interval = (int)Math.Clamp(Math.Ceiling((due - now).TotalMilliseconds), 1, 60_000);
        timer.Start();
    }

    public void Add(CorkBoard board, StickyNote note, string text, DateTimeOffset due, int? offset = null, ReminderRepeat? repeat = null)
    {
        var item = new NoteReminder(Guid.NewGuid(), board.Id, note.Id, text, due, Offset: offset, Repeat: repeat);
        Change("schedule reminder", () => Put(item), () => Put(null, item.Id));
    }

    public void Reschedule(NoteReminder item, DateTimeOffset due, ReminderRepeat? repeat) =>
        Change("reschedule reminder", () => Put(item with { Due = due, Fired = null, Done = false, Repeat = repeat }), () => Put(item));

    public void Snooze(NoteReminder item) => Reschedule(item, DateTimeOffset.Now.AddMinutes(5), null);

    private static NoteReminder? NextOccurrence(NoteReminder item, DateTimeOffset now) =>
        item.Repeat?.Next(item.Due > now ? item.Due : now) is { } due
            ? item with { Id = Guid.NewGuid(), Due = due, Fired = null, Done = false, DueActionsRun = false }
            : null;

    public void Done(NoteReminder item)
    {
        if (item.Done) return;
        DateTimeOffset now = DateTimeOffset.Now;
        var complete = item with { Done = true, Fired = item.Fired ?? now, Repeat = null };
        NoteReminder? next = NextOccurrence(item, now);
        var ruleEdit = app.ApplyReminderRule(item, NoteRules.Trigger.ReminderDone);
        Change("complete reminder", () => { ruleEdit?.Redo(); Complete(); },
            () =>
            {
                ruleEdit?.Undo();
                if (next is not null) Put(null, next.Id);
                Put(item);
            }, ruleEdit?.Size ?? 0, initial: Complete);

        void Complete()
        {
            Put(complete);
            if (next is not null) Put(next);
        }
    }

    public void Cancel(NoteReminder item) => Change("cancel reminder", () => Put(null, item.Id), () => Put(item));

    public void ClearDone()
    {
        var done = Items.Where(item => item.Done).ToArray();
        if (done.Length == 0) return;
        Change("clear completed reminders",
            () => { foreach (var item in done) Replace(item, null); },
            () => { foreach (var item in done) Replace(null, item); });
    }

    public void Stop(NoteReminder item)
    {
        int count = alerts.Count;
        for (int i = 0; i < count; i++)
        {
            Guid id = alerts.Dequeue();
            if (id != item.Id) alerts.Enqueue(id);
        }
        if (active == item.Id) CloseAlert();
        ShowNext();
        Changed?.Invoke();
    }

    public void Open(NoteReminder item)
    {
        Stop(item);
        app.OpenReminder(item);
    }

    public string NoteCaption(NoteReminder item)
    {
        CorkBoard? board = data.Boards.Find(board => board.Id == item.Board);
        StickyNote? note = board?.Notes.Find(note => note.Id == item.Note);
        return note is null ? "Source note unavailable"
            : string.IsNullOrWhiteSpace(note.Label) ? board!.Name : $"{note.Label} · {board!.Name}";
    }

    public void SetSound(ReminderSoundSettings settings)
    {
        ReminderSoundSettings before = Sound;
        if (before == settings) return;
        Change("change reminder sound", () => Apply(settings), () => Apply(before));

        void Apply(ReminderSoundSettings value)
        {
            data.ReminderSound = value;
            if (active.HasValue) audio.Play(value, value.Repeat);
        }
    }

    public string ImportSound(string path) => audio.Import(path);
    public void PreviewSound(ReminderSoundSettings value)
    {
        previewing = true;
        audio.Play(value, false);
    }

    public void EndPreview()
    {
        if (!previewing) return;
        previewing = false;
        audio.Stop();
        if (active.HasValue && Sound.Repeat) audio.Play(Sound, true);
    }

    private void Change(string label, Action apply, Action undo, long size = 0, Action? initial = null)
    {
        Apply(initial ?? apply);
        app.RecordEdit(label, () => Apply(undo), () => Apply(apply), size);

        void Apply(Action action)
        {
            action();
            if (active is Guid id && !Items.Any(item => item.Id == id && item.Fired.HasValue && !item.Done)) CloseAlert();
            Schedule(DateTimeOffset.Now);
            Changed?.Invoke();
            ShowNext();
        }
    }

    private void Put(NoteReminder? item, Guid? removed = null) =>
        Replace(Items.FirstOrDefault(old => old.Id == (item?.Id ?? removed)), item);

    private void Replace(NoteReminder? previous, NoteReminder? item)
    {
        var list = data.Reminders ??= [];
        int index = previous is null ? -1 : list.FindIndex(entry => entry.Id == previous.Id);
        if (index >= 0)
        {
            if (item is null) list.RemoveAt(index);
            else list[index] = item;
        }
        else if (item is not null) list.Add(item);
        app.MarkDirty();
    }

    private void ShowNext()
    {
        if (popup is not null) return;
        while (alerts.TryDequeue(out Guid id))
        {
            NoteReminder? item = Items.FirstOrDefault(item => item.Id == id && item.Fired.HasValue && !item.Done);
            if (item is null) continue;
            active = id;
            popup = new ReminderPopup(this, item);
            popup.Show();
            audio.Play(Sound, Sound.Repeat);
            return;
        }
    }

    private void CloseAlert()
    {
        audio.Stop();
        active = null;
        ReminderPopup? old = popup;
        popup = null;
        old?.Dispose();
    }

    public void Dispose()
    {
        timer.Dispose();
        CloseAlert();
        audio.Dispose();
    }
}
