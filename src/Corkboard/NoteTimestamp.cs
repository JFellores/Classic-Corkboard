using System.Globalization;
using System.Drawing.Drawing2D;

namespace Corkboard;

internal enum NoteTimestampFormat
{
    Default,
    DayFirst12,
    MonthFirst12,
    YearFirst24,
    WrittenDate,
    DayFirst24
}

internal static class NoteTimestamp
{
    private static readonly (NoteTimestampFormat Format, string Name)[] Choices =
    [
        (NoteTimestampFormat.DayFirst12, "Day first · 12-hour"),
        (NoteTimestampFormat.DayFirst24, "Day first · 24-hour"),
        (NoteTimestampFormat.MonthFirst12, "Month first · 12-hour"),
        (NoteTimestampFormat.YearFirst24, "Year first · 24-hour"),
        (NoteTimestampFormat.WrittenDate, "Written month · 24-hour")
    ];

    public static string Format(DateTime time, NoteTimestampFormat format) =>
        time.ToString(format switch
        {
            NoteTimestampFormat.DayFirst24 => "dd/MM/yyyy HH:mm",
            NoteTimestampFormat.MonthFirst12 => "MM/dd/yyyy h:mm tt",
            NoteTimestampFormat.YearFirst24 => "yyyy-MM-dd HH:mm",
            NoteTimestampFormat.WrittenDate => "d MMM yyyy, HH:mm",
            _ => "dd/MM/yyyy h:mm tt"
        }, CultureInfo.InvariantCulture);

    public static (NoteTimestampFormat Format, bool Subtle)? ChooseFormat(IWin32Window owner, NoteTimestampFormat current,
        bool subtle, Color paper)
    {
        using var dialog = new CorkDialog("Timestamp format", 345);
        using var boldFont = new Font("Segoe UI", 14F, FontStyle.Bold);
        using var subtleFont = new Font("Segoe UI", 14F);
        var label = new Label { Text = "Date and time format", Bounds = new Rectangle(22, 56, 328, 23) };
        var formats = new ComboBox
        {
            AccessibleName = "Date and time format", DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat, Bounds = new Rectangle(22, 82, 328, 32)
        };
        formats.Items.AddRange(Choices.Select(choice => (object)choice.Name).ToArray());
        formats.SelectedIndex = Math.Max(0, Array.FindIndex(Choices, choice => choice.Format == current));
        var weight = new Panel { Bounds = new Rectangle(22, 116, 328, 34) };
        var boldChoice = new RadioButton { Text = "Bold", Bounds = new Rectangle(0, 0, 112, 31), Checked = !subtle };
        var subtleChoice = new RadioButton { Text = "Subtle", Bounds = new Rectangle(118, 0, 125, 31), Checked = subtle };
        weight.Controls.AddRange([boldChoice, subtleChoice]);
        var preview = new Panel { BackColor = paper, Bounds = new Rectangle(22, 162, 328, 77) };
        preview.Resize += (_, _) => BoardStyle.RoundCorners(preview, preview.LogicalToDeviceUnits(8));
        BoardStyle.RoundCorners(preview, 8);
        var caption = new Label { Text = "Preview", ForeColor = BoardStyle.Muted, Bounds = new Rectangle(12, 9, 300, 21) };
        var example = new Label
        {
            AccessibleName = "Timestamp preview", Bounds = new Rectangle(12, 32, 304, 33),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var hint = new Label
        {
            Text = "Applies to new timestamps in this note.", ForeColor = BoardStyle.Muted,
            Bounds = new Rectangle(22, 251, 328, 22)
        };
        DateTime now = DateTime.Now;
        formats.SelectedIndexChanged += (_, _) => UpdatePreview();
        subtleChoice.CheckedChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        preview.Controls.AddRange([caption, example]);
        dialog.Controls.AddRange([label, formats, weight, preview, hint]);
        dialog.AddActions("Save", acceptWithEnter: true);
        dialog.Shown += (_, _) => formats.Focus();
        return dialog.ShowDialog(owner) == DialogResult.OK ? (Choices[formats.SelectedIndex].Format, subtleChoice.Checked) : null;

        void UpdatePreview()
        {
            example.Text = Format(now, Choices[formats.SelectedIndex].Format);
            example.Font = subtleChoice.Checked ? subtleFont : boldFont;
            example.ForeColor = TextSizePrompt.TextInk(paper, subtleChoice.Checked ? 65 : 100);
        }
    }

    public static void PaintIcon(object? sender, PaintEventArgs e)
    {
        if (sender is not Button button) return;
        Graphics graphics = e.Graphics;
        GraphicsState state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(button.Width / 2f, button.Height / 2f);
        float scale = button.DeviceDpi / 96f;
        graphics.ScaleTransform(scale, scale);
        using var pen = new Pen(button.ForeColor, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawEllipse(pen, -6.5f, -6.5f, 13, 13);
        graphics.DrawLines(pen, [new PointF(0, -3.5f), PointF.Empty, new PointF(3, 1.5f)]);
        graphics.Restore(state);
    }
}
