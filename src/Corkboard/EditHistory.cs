using System.Diagnostics;

namespace Corkboard;

internal sealed class EditHistory
{
    private const int Limit = 60;
    private readonly List<Entry> undo = [];
    private readonly List<Entry> redo = [];
    private long bytes;
    private const long Budget = 12 * 1024 * 1024;
    public string? UndoLabel => undo.Count > 0 ? undo[^1].Label : null;
    public string? RedoLabel => redo.Count > 0 ? redo[^1].Label : null;

    public void Record(string label, Action revert, Action apply, object? group = null, long size = 0)
    {
        long now = Stopwatch.GetTimestamp();
        if (group is not null && undo.Count > 0 && redo.Count == 0 && ReferenceEquals(undo[^1].Group, group)
            && Stopwatch.GetElapsedTime(undo[^1].Time, now).TotalMilliseconds < 750)
        {
            bytes += size - undo[^1].Size;
            undo[^1] = undo[^1] with { Apply = apply, Time = now, Size = size };
        }
        else
        {
            undo.Add(new(label, revert, apply, group, now, size));
            bytes += size;
        }
        bytes -= redo.Sum(entry => entry.Size);
        redo.Clear();
        while (undo.Count > Limit || (bytes > Budget && undo.Count > 1))
        {
            bytes -= undo[0].Size;
            undo.RemoveAt(0);
        }
    }

    public string? Step(bool forward)
    {
        List<Entry> from = forward ? redo : undo;
        List<Entry> to = forward ? undo : redo;
        if (from.Count == 0) return null;
        Entry entry = from[^1];
        (forward ? entry.Apply : entry.Revert)();
        from.RemoveAt(from.Count - 1);
        to.Add(entry with { Group = null });
        return entry.Label;
    }

    private sealed record Entry(string Label, Action Revert, Action Apply, object? Group, long Time, long Size);
}
