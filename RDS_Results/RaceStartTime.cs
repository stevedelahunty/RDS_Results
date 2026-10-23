using System.Globalization;
using System.Text.RegularExpressions;

namespace RDS_Results;

public sealed class RaceStartSnapshot
{
    public string Display { get; init; } = "";
    public DateTime At { get; init; }
}

public static class RaceStartTime
{
    static readonly Regex MonthName = new(
        @"\b(jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static RaceStartSnapshot? Resolve(string? setting, string? raceDayStart, DateTime now)
    {
        if (TryParse(setting, out var parsed))
        {
            var at = HasExplicitDate(setting)
                ? parsed
                : DateOf(raceDayStart, now.Date) + parsed.TimeOfDay;
            return new RaceStartSnapshot { Display = FormatTime(parsed), At = at };
        }

        if (!TryParse(raceDayStart, out var fromRace) || fromRace.TimeOfDay == TimeSpan.Zero)
            return null;
        return new RaceStartSnapshot { Display = FormatTime(fromRace), At = fromRace };
    }

    public static bool TryParse(string? value, out DateTime time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out time))
            return true;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out time))
            return true;
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span)
            && span >= TimeSpan.Zero && span < TimeSpan.FromDays(1))
        {
            time = DateTime.Today.Add(span);
            return true;
        }

        return false;
    }

    public static string FormatTime(DateTime time) =>
        time.Second == 0
            ? time.ToString("h:mm tt", CultureInfo.InvariantCulture)
            : time.ToString("h:mm:ss tt", CultureInfo.InvariantCulture);

    static DateTime DateOf(string? raceDayStart, DateTime fallbackDate)
    {
        if (TryParse(raceDayStart, out var day))
            return day.Date;
        return fallbackDate.Date;
    }

    static bool HasExplicitDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.IndexOfAny(['/', '-']) >= 0)
            return true;
        return MonthName.IsMatch(value);
    }
}
