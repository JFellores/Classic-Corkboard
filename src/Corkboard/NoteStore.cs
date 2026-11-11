using System.Text.Json;
using Microsoft.Win32;

namespace Corkboard;

internal sealed class BoardData
{
    public List<StickyNote> Notes { get; init; } = [];
}

internal sealed class StickyNote
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public int Color { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public bool PoppedOut { get; set; }
    public bool AlwaysOnTop { get; set; }
    public Rectangle? PopupBounds { get; set; }
}

internal sealed class NoteStore(string folder)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
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
                if (IsMissing(original) && IsMissing(fallback)) return (new BoardData(), null);
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
        if (data?.Notes is null || data.Notes.Any(note => note is null || note.Text is null)
            || data.Notes.Select(note => note.Id).Distinct().Count() != data.Notes.Count)
            throw new JsonException("The note file is invalid.");
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

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(Entry) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Entry, Command);
        else key.DeleteValue(Entry, throwOnMissingValue: false);
    }

    private static string Command => $"\"{Environment.ProcessPath}\"";
}
