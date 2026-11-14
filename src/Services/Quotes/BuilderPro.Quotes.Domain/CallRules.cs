namespace BuilderPro.Quotes.Domain;

/// <summary>
/// 4b: when an automated scheduling call may be placed, and which visit times it offers. Pure rules.
///
/// Calls go out only inside the customer's contact hours AND never outside 9:00-20:00 local time
/// (quiet hours stricter than the 8-21 legal limit for automated calls):
///   business  Mon-Fri 9-17      evenings  every day 17-20
///   weekends  Sat-Sun 10-18     anytime   every day 9-20
/// Times are in the store's zone (the customer's own zone isn't known yet; stores serve their area).
/// </summary>
public static class CallWindows
{
    public const int MaxAttempts = 3;

    public static bool MayCall(DateTimeOffset at, string hours, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var weekday = local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        var h = local.Hour;
        return hours switch
        {
            "anytime" => h is >= 9 and < 20,
            "evenings" => h is >= 17 and < 20,
            "weekends" => !weekday && h is >= 10 and < 18,
            _ => weekday && h is >= 9 and < 17,   // business (default)
        };
    }

    /// <summary>The first moment from now (15-minute steps, up to 14 days) a call is allowed; null if none.</summary>
    public static DateTimeOffset? NextAllowed(DateTimeOffset now, string hours, TimeZoneInfo zone)
    {
        var t = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute / 15 * 15, 0, now.Offset);
        if (t < now) t = t.AddMinutes(15);
        for (var i = 0; i < 14 * 24 * 4; i++, t = t.AddMinutes(15))
            if (MayCall(t, hours, zone)) return t;
        return null;
    }

    /// <summary>Visit times that suit the customer's hours (same rule as the 4a agent): up to max, on different days.</summary>
    public static IReadOnlyList<DateTimeOffset> ProposeVisits(IEnumerable<(DateTimeOffset Start, bool Available)> slots, string hours, TimeZoneInfo zone, int max = 3)
    {
        var chosen = new List<DateTimeOffset>();
        var days = new HashSet<DateOnly>();
        foreach (var (start, available) in slots.OrderBy(s => s.Start))
        {
            if (!available || !VisitFits(start, hours, zone)) continue;
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
            if (!days.Add(day)) continue;
            chosen.Add(start);
            if (chosen.Count == max) break;
        }
        return chosen;
    }

    /// <summary>business 08-17 Mon-Fri; evenings 17-21; weekends Sat/Sun; anytime.</summary>
    public static bool VisitFits(DateTimeOffset start, string hours, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(start, zone);
        var weekday = local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        return hours switch
        {
            "anytime" => true,
            "weekends" => !weekday,
            "evenings" => local.Hour is >= 17 and < 21,
            _ => weekday && local.Hour is >= 8 and < 17,
        };
    }
}
