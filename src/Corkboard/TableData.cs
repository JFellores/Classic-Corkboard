using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Corkboard;

internal sealed class TableData
{
    public const int MaximumRows = 20;
    public const int MaximumColumns = 8;
    public const int MaximumCellLength = 512;
    private const string ReferenceError = "#REF!";
    private const string Address = @"\$?[A-H]\$?[1-9][0-9]?";
    private static readonly Regex Formula = new(
        $@"^=\s*(?:(?<function>SUM|MAX|MIN|AVERAGE|COUNT)\s*\(\s*(?<first>{Address})(?:\s*:\s*(?<last>{Address}))?\s*\)|(?<first>{Address})(?:\s*:\s*(?<last>{Address}))?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public List<List<string>> Rows { get; init; } =
        Enumerable.Range(0, 4).Select(_ => new List<string> { "", "", "" }).ToList();

    [JsonIgnore]
    public int ColumnCount => Rows[0].Count;

    [JsonIgnore]
    public bool IsValid => Rows is { Count: >= 1 and <= MaximumRows }
        && Rows[0] is { Count: >= 1 and <= MaximumColumns }
        && Rows.All(row => row is not null && row.Count == ColumnCount
            && row.All(cell => cell is { Length: <= MaximumCellLength }));

    public bool CanAdd(bool column) => column ? ColumnCount < MaximumColumns : Rows.Count < MaximumRows;
    public bool CanDelete(bool column) => column ? ColumnCount > 1 : Rows.Count > 1;

    public void Append(bool column)
    {
        if (!CanAdd(column)) return;
        if (column) foreach (List<string> row in Rows) row.Add("");
        else Rows.Add(Enumerable.Repeat("", ColumnCount).ToList());
    }

    public bool HasValues(bool column, int index) =>
        column ? Rows.Any(row => row[index].Length != 0) : Rows[index].Any(cell => cell.Length != 0);

    public void Remove(bool column, int index)
    {
        if (!CanDelete(column)) return;
        if (column) foreach (List<string> row in Rows) row.RemoveAt(index);
        else Rows.RemoveAt(index);

        foreach (List<string> row in Rows)
        {
            for (int cell = 0; cell < row.Count; cell++)
            {
                if (!TryParseFormula(row[cell], out string function, out CellRange range)) continue;
                CellRange? shifted = range.Remove(column, index);
                if (shifted != range) row[cell] = FormatFormula(function, shifted);
            }
        }
    }

    public string[,] Calculate()
    {
        var results = new CellResult?[Rows.Count, ColumnCount];
        var visiting = new bool[Rows.Count, ColumnCount];
        var display = new string[Rows.Count, ColumnCount];
        for (int row = 0; row < Rows.Count; row++)
        {
            for (int column = 0; column < ColumnCount; column++)
            {
                string raw = Rows[row][column];
                if (!raw.TrimStart().StartsWith('=')) { display[row, column] = raw; continue; }
                CellResult result = Read(row, column);
                display[row, column] = result.Error ?? result.Number?.ToString("G12", CultureInfo.CurrentCulture) ?? result.Text ?? "";
            }
        }
        return display;

        CellResult Read(int row, int column)
        {
            if (results[row, column] is CellResult cached) return cached;
            if (visiting[row, column]) return new(null, "#CYCLE!");
            visiting[row, column] = true;
            CellResult result = Evaluate(Rows[row][column]);
            visiting[row, column] = false;
            results[row, column] = result;
            return result;
        }

        CellResult Evaluate(string raw)
        {
            string formula = raw.Trim();
            if (!formula.StartsWith('='))
            {
                return TryReadNumber(raw, out double value) ? new(value, null) : new(null, null, raw);
            }
            if (formula == $"={ReferenceError}") return new(null, ReferenceError);
            if (!TryParseFormula(formula, out string function, out CellRange range)) return new(null, "#FORMULA!");
            if (range.Bottom >= Rows.Count || range.Right >= ColumnCount) return new(null, ReferenceError);
            if (function.Length == 0)
            {
                if (range.Bottom != range.Top || range.Right != range.Left) return new(null, "#VALUE!");
                CellResult reference = Read(range.Top, range.Left);
                return reference is { Number: null, Error: null, Text.Length: 0 } ? new(0, null) : reference;
            }

            var numbers = new List<double>();
            for (int row = range.Top; row <= range.Bottom; row++)
            {
                for (int column = range.Left; column <= range.Right; column++)
                {
                    CellResult item = Read(row, column);
                    if (item.Error is not null) return item;
                    if (item.Number is double number) numbers.Add(number);
                }
            }
            if (numbers.Count == 0 && function == "AVERAGE") return new(null, "#DIV/0!");
            double result = function switch
            {
                "SUM" => numbers.Sum(),
                "MAX" => numbers.Count == 0 ? 0 : numbers.Max(),
                "MIN" => numbers.Count == 0 ? 0 : numbers.Min(),
                "AVERAGE" => numbers.Average(),
                _ => numbers.Count
            };
            return double.IsFinite(result) ? new(result, null) : new(null, "#NUM!");
        }
    }

    public static bool TryReadNumber(string raw, out double value) =>
        (double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        && double.IsFinite(value);

    public static string ShiftCopiedFormula(string raw, int rows, int columns)
    {
        if (rows == 0 && columns == 0 || !TryParseFormula(raw, out string function, out CellRange range)) return raw;
        CellRange shifted = new(range.First.Shift(rows, columns), range.Last.Shift(rows, columns));
        if (shifted == range) return raw;
        return FormatFormula(function, shifted.Top < 0 || shifted.Left < 0
            || shifted.Bottom >= MaximumRows || shifted.Right >= MaximumColumns ? null : shifted);
    }

    private static string FormatFormula(string function, CellRange? range) =>
        range is null ? $"={ReferenceError}" : function.Length == 0 ? $"={range}" : $"={function}({range})";

    private static bool TryParseFormula(string text, out string function, out CellRange range)
    {
        function = "";
        range = default;
        if (!text.TrimStart().StartsWith('=')) return false;
        Match match = Formula.Match(text.Trim());
        if (!match.Success) return false;
        function = match.Groups["function"].Value.ToUpperInvariant();
        CellAddress first = CellAddress.Parse(match.Groups["first"].ValueSpan);
        range = new(first, match.Groups["last"].Success ? CellAddress.Parse(match.Groups["last"].ValueSpan) : first);
        return true;
    }

    private readonly record struct CellResult(double? Number, string? Error, string? Text = null);

    private readonly record struct CellAddress(int Row, int Column, bool FixedRow, bool FixedColumn)
    {
        public static CellAddress Parse(ReadOnlySpan<char> text)
        {
            bool fixedColumn = text[0] == '$';
            int columnIndex = fixedColumn ? 1 : 0;
            int column = char.ToUpperInvariant(text[columnIndex]) - 'A';
            int rowIndex = columnIndex + 1;
            bool fixedRow = text[rowIndex] == '$';
            int row = int.Parse(text[(rowIndex + (fixedRow ? 1 : 0))..], CultureInfo.InvariantCulture) - 1;
            return new(row, column, fixedRow, fixedColumn);
        }

        public CellAddress Shift(int rows, int columns) =>
            this with { Row = FixedRow ? Row : Row + rows, Column = FixedColumn ? Column : Column + columns };

        public override string ToString() => $"{(FixedColumn ? "$" : "")}{(char)('A' + Column)}{(FixedRow ? "$" : "")}{Row + 1}";
    }

    private readonly record struct CellRange(CellAddress First, CellAddress Last)
    {
        public int Top => Math.Min(First.Row, Last.Row);
        public int Bottom => Math.Max(First.Row, Last.Row);
        public int Left => Math.Min(First.Column, Last.Column);
        public int Right => Math.Max(First.Column, Last.Column);

        public CellRange? Remove(bool column, int index)
        {
            int min = column ? Left : Top;
            int max = column ? Right : Bottom;
            if (min == max && min == index) return null;
            return new(Shift(First), Shift(Last));

            CellAddress Shift(CellAddress address)
            {
                int coordinate = column ? address.Column : address.Row;
                if (coordinate > index || (coordinate == index && coordinate == max)) coordinate--;
                return column ? address with { Column = coordinate } : address with { Row = coordinate };
            }
        }

        public override string ToString()
        {
            return First == Last ? First.ToString() : $"{First}:{Last}";
        }
    }
}
