namespace SpeedMeter.Core.View;

/// <summary>
/// Which page numbers a pager shows: the first and the last, the current page
/// with two either side, and a gap (null) wherever pages are skipped -
/// <c>1 … 4 5 6 7 8 … 12</c>. A gap never stands in for a single page: that
/// page is shown instead, since the marker would be as wide as the number.
/// The sibling apps' pager, one for one.
/// </summary>
public static class Pages
{
    public static List<int?> Items(int page, int count, int around = 2)
    {
        if (count <= 1) return [1];
        var current = Math.Clamp(page, 1, count);
        var shown = new SortedSet<int> { 1, count };
        for (var p = current - around; p <= current + around; p++)
            if (p >= 1 && p <= count) shown.Add(p);
        var items = new List<int?>();
        int? previous = null;
        foreach (var p in shown)
        {
            if (previous is { } prev)
            {
                if (p - prev == 2) items.Add(prev + 1);
                else if (p - prev > 2) items.Add(null);
            }
            items.Add(p);
            previous = p;
        }
        return items;
    }

    /// <summary>How many pages <paramref name="total"/> items fill, never fewer than one.</summary>
    public static int Count(int total, int pageSize) => Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
}

/// <summary>The daily peak chart's range: a day count, with "All" as a century.</summary>
public static class Ranges
{
    /// <summary>
    /// "All" as a day count rather than a sentinel, so every caller keeps
    /// subtracting days and there is no special case to forget.
    /// </summary>
    public const int AllDays = 36500;

    /// <summary>
    /// Offered in the chart's dropdown. No "Today": the page already shows
    /// today's fastest seconds, and a one-day chart is a single pair of bars.
    /// </summary>
    public static readonly int[] Offered = [7, 30, 90, AllDays];

    /// <summary>
    /// A month, not all history: a week is too short to show a change in what
    /// the connection can do, and a step down reads as a few quiet days until
    /// there are weeks on either side of it.
    /// </summary>
    public const int Default = 30;

    public static string Label(int days) => days == AllDays ? "All days" : $"{days} days";

    public static int Parse(int days) => Offered.Contains(days) ? days : Default;

    /// <summary>The first local date the range covers, ending today.</summary>
    public static DateTime From(int days, DateTime today) => today.Date.AddDays(-(days - 1));
}
