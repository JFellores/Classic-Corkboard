using System.Text.Json.Serialization;

namespace Corkboard;

[Flags]
internal enum ReminderDays
{
    None = 0, Monday = 1, Tuesday = 2, Wednesday = 4, Thursday = 8,
    Friday = 16, Saturday = 32, Sunday = 64,
    EveryDay = Monday | Tuesday | Wednesday | Thursday | Friday | Saturday | Sunday
}

internal sealed record ReminderRepeat(TimeOnly Time, ReminderDays Days)
{
    internal static readonly (ReminderDays Day, string Label, string Short)[] Week =
    [
        (ReminderDays.Monday, "Monday", "Mo"), (ReminderDays.Tuesday, "Tuesday", "Tu"),
        (ReminderDays.Wednesday, "Wednesday", "We"), (ReminderDays.Thursday, "Thursday", "Th"),
        (ReminderDays.Friday, "Friday", "Fr"), (ReminderDays.Saturday, "Saturday", "Sa"),
        (ReminderDays.Sunday, "Sunday", "Su")
    ];

    [JsonIgnore]
    public string Caption => Days == ReminderDays.EveryDay ? $"Daily at {Time:HH:mm}"
        : $"{string.Join(", ", Week.Where(day => Days.HasFlag(day.Day)).Select(day => day.Short))} at {Time:HH:mm}";

    public DateTimeOffset? Next(DateTimeOffset after, DateOnly? start = null, TimeZoneInfo? timeZone = null)
    {
        TimeZoneInfo zone = timeZone ?? TimeZoneInfo.Local;
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        DateOnly first = start is { } date && date > today ? date : today;
        for (int offset = 0; offset < 15 && first.DayNumber + offset <= DateOnly.MaxValue.DayNumber; offset++)
        {
            DateOnly day = first.AddDays(offset);
            int index = ((int)day.DayOfWeek + 6) % 7;
            if (!Days.HasFlag(Week[index].Day)) continue;
            DateTime local = day.ToDateTime(Time);
            if (zone.IsInvalidTime(local)) continue;
            var instant = new DateTimeOffset(local, zone.GetUtcOffset(local));
            if (instant > after) return instant;
        }
        return null;
    }
}
