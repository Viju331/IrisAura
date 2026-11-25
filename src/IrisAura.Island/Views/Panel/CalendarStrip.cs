using System.Globalization;

namespace IrisAura.Island.Views.Panel;

/// <summary>One day in the calendar strip.</summary>
/// <param name="Initial">Shortest day name ("Mo").</param>
/// <param name="Number">Day of month.</param>
/// <param name="IsToday">Highlighted.</param>
/// <param name="AccessibleName">"Wednesday 25 November".</param>
public sealed record CalendarDay(string Initial, int Number, bool IsToday, string AccessibleName);

/// <summary>
/// The panel's calendar strip (BLUEPRINT §8.13A): month, year and a row of days around today. Dates only: showing
/// events needs calendar access with the user's consent (Appointments API, package identity), which is not built,
/// so no calendar data is read. Pure, for tests.
/// </summary>
/// <param name="Month">Month name in the user's language.</param>
/// <param name="Year">Year.</param>
/// <param name="Days">Days, today in the middle.</param>
public sealed record CalendarStrip(string Month, string Year, IReadOnlyList<CalendarDay> Days)
{
    public const int DaysBefore = 2;
    public const int DaysAfter = 2;

    public static CalendarStrip Build(DateTime today, CultureInfo culture)
    {
        var format = culture.DateTimeFormat;
        var days = new List<CalendarDay>();
        for (var offset = -DaysBefore; offset <= DaysAfter; offset++)
        {
            var day = today.Date.AddDays(offset);
            days.Add(new CalendarDay(
                format.GetShortestDayName(day.DayOfWeek),
                day.Day,
                offset == 0,
                day.ToString("dddd d MMMM", culture) + (offset == 0 ? ", today" : string.Empty)));
        }

        return new CalendarStrip(
            culture.TextInfo.ToTitleCase(format.GetMonthName(today.Month)),
            today.Year.ToString(culture),
            days);
    }
}
