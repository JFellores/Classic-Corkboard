using System.Text.Json;

namespace Corkboard;

internal sealed record DeletedItem(Guid Id, Guid BoardId, string BoardName, string Title, int? SlideIndex,
    DateTimeOffset DeletedAt, int Size, JsonElement Snapshot)
{
    internal static DeletedItem Capture(CorkBoard board, int? slideIndex, StickyNote[] cards)
    {
        var ids = cards.Select(card => card.Id).ToHashSet();
        var payload = new Contents(cards, board.Threads.Where(thread => ids.Contains(thread.From) || ids.Contains(thread.To)).ToArray());
        JsonElement snapshot = JsonSerializer.SerializeToElement(payload, NoteStore.JsonOptions);
        return new(Guid.NewGuid(), board.Id, board.Name,
            slideIndex is int index ? new BoardSlide(index).Name + $" · {cards.Length} {(cards.Length == 1 ? "card" : "cards")}"
                : CardTitle(cards[0]), slideIndex, DateTimeOffset.UtcNow, snapshot.GetRawText().Length, snapshot);
    }

    internal Contents Read()
    {
        if (Snapshot.ValueKind != JsonValueKind.Object
            || Snapshot.Deserialize<Contents>(NoteStore.JsonOptions) is not { Cards: not null, Threads: not null } contents
            || (SlideIndex is null && contents.Cards.Length != 1)
            || contents.Cards.Any(card => card is null || card.Text is null || card.Label is null || card.Table is { IsValid: false }
                || (card.Table is not null && card.ImageFile is not null))
            || contents.Cards.Select(card => card.Id).Distinct().Count() != contents.Cards.Length
            || contents.Threads.Any(thread => thread is null))
            throw new JsonException("The recovery entry is invalid.");
        return contents;
    }

    private static string CardTitle(StickyNote card)
    {
        string title = string.IsNullOrWhiteSpace(card.Label) ? card.Text.Split(['\r', '\n'], 2)[0].Trim() : card.Label;
        string kind = card.Table is not null ? "Table" : card.ImageFile is not null ? "Image" : "Note";
        if (title.Length > 72) title = title[..71] + "…";
        return title.Length == 0 ? $"Untitled {kind.ToLowerInvariant()}" : $"{kind} · {title}";
    }

    internal sealed record Contents(StickyNote[] Cards, NoteThread[] Threads);
}

internal static class RecoveryView
{
    internal static DeletedItem? Choose(Form owner, IReadOnlyList<DeletedItem> entries, Func<DeletedItem, string> destination)
    {
        using var dialog = new CorkDialog("Version history", 356);
        dialog.Controls.Add(new Label
        {
            Text = entries.Count == 0 ? "Deleted notes and slides will appear here." : "Restore a recently deleted note or slide.",
            Bounds = new Rectangle(22, 54, 328, 25), ForeColor = BoardStyle.Muted
        });
        var list = new ListBox
        {
            Bounds = new Rectangle(22, 86, 328, 151), AccessibleName = "Deleted notes and slides",
            IntegralHeight = false, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = BoardStyle.Ink,
            DisplayMember = nameof(DeletedItem.Title), Visible = entries.Count > 0
        };
        list.Items.AddRange(entries.Cast<object>().ToArray());
        var details = new Label { Bounds = new Rectangle(22, 245, 328, 51), ForeColor = BoardStyle.Muted, AutoEllipsis = true };
        dialog.Controls.AddRange([list, details]);
        dialog.AddActions("Restore", acceptWithEnter: true).Text = "Close";
        var restore = (Button)dialog.AcceptButton!;
        list.SelectedIndexChanged += (_, _) => RefreshSelection();
        if (entries.Count > 0) list.SelectedIndex = 0;
        RefreshSelection();
        return dialog.ShowDialog(owner) == DialogResult.OK ? list.SelectedItem as DeletedItem : null;

        void RefreshSelection()
        {
            var item = list.SelectedItem as DeletedItem;
            restore.Enabled = item is not null;
            details.Text = item is null ? "" : $"{item.BoardName} · {item.DeletedAt.LocalDateTime:g}\n{destination(item)}";
        }
    }
}

