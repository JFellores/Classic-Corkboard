using System.Text.Json;
using Microsoft.Win32;

namespace Corkboard;

internal sealed class BoardData
{
    [System.Text.Json.Serialization.JsonPropertyName("Notes")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<StickyNote>? LegacyNotes { get; set; }
    public List<CorkBoard> Boards { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DeletedItem[]? DeletedItems { get; set; }
    public Guid? LastBoard { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<NoteReminder>? Reminders { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReminderSoundSettings? ReminderSound { get; set; }

    public void Upgrade()
    {
        if (Boards.Count == 0) Boards.Add(new CorkBoard { Name = "My corkboard", Notes = LegacyNotes ?? [] });
        LegacyNotes = null;
        foreach (CorkBoard board in Boards) board.UpgradeSlides();
    }
}

internal readonly record struct BoardSlide(int Index)
{
    public string Name => $"Slide {Index + 1}";
}

internal sealed class CorkBoard
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Corkboard";
    public List<StickyNote> Notes { get; init; } = [];
    public List<NoteThread> Threads { get; init; } = [];
    public int HorizontalSlideCount { get; set; } = 1;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int VerticalSlideCount { get; set; }
    public int ActiveSlide { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ActiveSlideVertical { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("SlideCount")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacySlideCount { get; set; }
    public Rectangle? Bounds { get; set; }
    public bool WasOpen { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public BoardSlide CurrentSlide
    {
        get => new(ActiveSlide);
        set => ActiveSlide = value.Index;
    }

    public bool HasSlide(BoardSlide slide) => slide.Index >= 0 && slide.Index < HorizontalSlideCount;

    public IEnumerable<BoardSlide> Slides()
    {
        for (int i = 0; i < HorizontalSlideCount; i++) yield return new BoardSlide(i);
    }

    public void UpgradeSlides()
    {
        if (LegacySlideCount is int count)
        {
            HorizontalSlideCount = count;
            LegacySlideCount = null;
            return;
        }

        if (VerticalSlideCount == 0 && !ActiveSlideVertical && Notes?.Any(note => note?.SlideVertical == true) != true) return;
        int horizontal = HorizontalSlideCount;
        long total = (long)horizontal + VerticalSlideCount;
        if (horizontal < 0 || VerticalSlideCount < 0 || total is < 1 or > int.MaxValue ||
            ActiveSlide < 0 || ActiveSlide >= (ActiveSlideVertical ? VerticalSlideCount : horizontal))
            throw new JsonException("The saved slides are invalid.");
        if (Notes is not null)
        {
            foreach (StickyNote note in Notes.Where(note => note is not null))
            {
                if (note.Slide < 0 || note.Slide >= (note.SlideVertical ? VerticalSlideCount : horizontal))
                    throw new JsonException("The saved slides are invalid.");
                if (note.SlideVertical) note.Slide += horizontal;
                note.SlideVertical = false;
            }
        }
        if (ActiveSlideVertical) ActiveSlide += horizontal;
        HorizontalSlideCount = (int)total;
        VerticalSlideCount = 0;
        ActiveSlideVertical = false;
    }
}

internal sealed class StickyNote
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public string? FormattedText { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CheckedItemDestination { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public NoteTimestampFormat TimestampFormat { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool SubtleTimestamps { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool UsePages { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TimeOnly? DailyChecklistReset { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? LastChecklistReset { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? ChecklistResetDelayMinutes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ChecklistItemRule[]? ChecklistItems { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<NoteRules.Trigger, NoteRules.Settings>? ContentRules { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TextSelectionRule[]? TextSelectionRules { get; set; }
    public string Label { get; set; } = "";
    public string? ImageFile { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TableData? Table { get; set; }
    public bool Pinned { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? PinColor { get; set; }
    public int Color { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Slide { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool SlideVertical { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public BoardSlide SlideAddress
    {
        get => new(Slide);
        set => Slide = value.Index;
    }
    public int CardWidth { get; set; }
    public int CardHeight { get; set; }
    public bool PoppedOut { get; set; }
    public bool AlwaysOnTop { get; set; }
    public Rectangle? PopupBounds { get; set; }
}

internal sealed class NoteThread
{
    public Guid From { get; init; }
    public Guid To { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Color { get; set; }
}

internal sealed class NoteStore(string folder)
{
    public string ReminderSoundFolder => Path.Combine(folder, "reminder-sounds");
    public ImageLibrary Images { get; } = new(Path.Combine(folder, "images"));
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new BoundsConverter() }
    };

    private readonly string file = Path.Combine(folder, "notes.json");
    private readonly string backup = Path.Combine(folder, "notes.backup.json");
    private bool recoveredBackup;

    public (BoardData Data, string? Warning) Load()
    {
        try { return (Read(file), null); }
        catch (Exception original) when (IsStorageError(original))
        {
            try
            {
                BoardData recovered = Read(backup);
                recoveredBackup = true;
                return (recovered, "Opened the last good backup.");
            }
            catch (Exception fallback) when (IsStorageError(fallback))
            {
                if (IsMissing(original) && IsMissing(fallback))
                {
                    var empty = new BoardData();
                    empty.Upgrade();
                    return (empty, null);
                }
                throw new IOException("Neither the current note file nor its backup could be loaded.", original);
            }
        }
    }

    public void Save(BoardData data)
    {
        Directory.CreateDirectory(folder);
        string temporary = file + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, data, JsonOptions);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(file)) File.Replace(temporary, file, recoveredBackup ? null : backup);
        else File.Move(temporary, file);
        recoveredBackup = false;
    }

    private static BoardData Read(string path)
    {
        using var stream = File.OpenRead(path);
        BoardData? data = JsonSerializer.Deserialize<BoardData>(stream, JsonOptions);
        if (data is null || data.Boards is null || data.Boards.Any(board => board is null)) throw new JsonException("The board file is invalid.");
        data.Upgrade();
        if (data.Boards.Select(board => board.Id).Distinct().Count() != data.Boards.Count || data.Boards.Any(board =>
            board.Name is null || board.Notes is null || board.Threads is null || board.Threads.Any(thread => thread is null)
            || board.HorizontalSlideCount < 0 || board.VerticalSlideCount < 0 || !board.HasSlide(board.CurrentSlide)
            || board.Notes.Any(note => note is null || note.Text is null || note.Label is null)
            || board.Notes.Any(note => note.Table is { IsValid: false } || (note.Table is not null && note.ImageFile is not null))
            || board.Notes.Any(note => !board.HasSlide(note.SlideAddress))
            || board.Notes.Select(note => note.Id).Distinct().Count() != board.Notes.Count))
            throw new JsonException("The board file is invalid.");
        return data;
    }

    public static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException;
    private static bool IsMissing(Exception error) => error is FileNotFoundException or DirectoryNotFoundException;

    private sealed class BoundsConverter : System.Text.Json.Serialization.JsonConverter<Rectangle>
    {
        public override Rectangle Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            int[]? dimensions = JsonSerializer.Deserialize<int[]>(ref reader, options);
            if (dimensions?.Length != 4) throw new JsonException("The sticky window position is invalid.");
            return new Rectangle(dimensions[0], dimensions[1], dimensions[2], dimensions[3]);
        }

        public override void Write(Utf8JsonWriter writer, Rectangle value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new[] { value.X, value.Y, value.Width, value.Height }, options);
    }
}

internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Entry = "Corkboard";

    public static bool IsEnabled() => string.Equals(ReadCommand(), Command, StringComparison.OrdinalIgnoreCase);

    public static string? ReadCommand()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Entry) as string;
    }

    public static void SetEnabled(bool enabled) => WriteCommand(enabled ? Command : null);

    public static void WriteCommand(string? value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (value is not null) key.SetValue(Entry, value);
        else key.DeleteValue(Entry, throwOnMissingValue: false);
    }

    private static string Command => $"\"{Environment.ProcessPath}\"";
}
