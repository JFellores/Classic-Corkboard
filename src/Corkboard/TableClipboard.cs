using System.Text;
using System.Text.Json;

namespace Corkboard;

internal sealed record TableClipboard(string[][] Cells, int? SourceRow = null, int? SourceColumn = null)
{
    private const string Format = "ClassicCorkboard.TableCells";

    public DataObject ToDataObject(string[][] visible)
    {
        var data = new DataObject();
        data.SetData(Format, false, JsonSerializer.Serialize(this));
        string text = string.Join("\r\n", visible.Select(row => string.Join('\t', row.Select(Quote))));
        data.SetData(DataFormats.UnicodeText, false, text);
        return data;
    }

    public static TableClipboard? Read(IDataObject? data)
    {
        if (data?.GetData(Format, false) is string raw && raw.Length <= 600_000)
        {
            try
            {
                TableClipboard? cells = JsonSerializer.Deserialize<TableClipboard>(raw);
                if (cells?.Cells is not null) return cells;
            }
            catch (JsonException) { }
        }
        return data?.GetData(DataFormats.UnicodeText) is string text && text.Length > 0
            ? new(ParseText(text)) : null;
    }

    private static string Quote(string value) =>
        value.IndexOfAny(['\t', '\r', '\n', '"']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string[][] ParseText(string text)
    {
        if (text.Length > 180_000) return [];
        var rows = new List<string[]>();
        var cells = new List<string>();
        var value = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < text.Length; index++)
        {
            char ch = text[index];
            if (quoted)
            {
                if (ch == '"' && index + 1 < text.Length && text[index + 1] == '"') { value.Append(ch); index++; }
                else if (ch == '"') quoted = false;
                else value.Append(ch);
            }
            else if (ch == '"' && value.Length == 0) quoted = true;
            else if (ch == '\t')
            {
                AddCell();
                if (cells.Count >= TableData.MaximumColumns) return [];
            }
            else if (ch is '\r' or '\n')
            {
                AddRow();
                if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                if (index == text.Length - 1) return rows.ToArray();
                if (rows.Count >= TableData.MaximumRows) return [];
            }
            else value.Append(ch);
        }
        AddRow();
        return rows.ToArray();

        void AddCell()
        {
            cells.Add(value.ToString());
            value.Clear();
        }

        void AddRow()
        {
            AddCell();
            rows.Add(cells.ToArray());
            cells.Clear();
        }
    }
}
