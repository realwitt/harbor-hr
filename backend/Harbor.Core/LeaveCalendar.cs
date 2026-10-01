namespace Harbor;

public static class LeaveCalendar
{
    public static DateOnly Boundary(string yearBoundary, DateOnly hiredOn, int year)
    {
        if (string.Equals(yearBoundary, "anniversary", StringComparison.Ordinal))
        {
            var day = Math.Min(hiredOn.Day, DateTime.DaysInMonth(year, hiredOn.Month));
            return new DateOnly(year, hiredOn.Month, day);
        }

        return new DateOnly(year, 1, 1);
    }

    public static IEnumerable<DateOnly> BoundariesAfter(string yearBoundary, DateOnly hiredOn, DateOnly after, DateOnly through)
    {
        if (through <= after)
        {
            yield break;
        }

        for (var year = after.Year - 1; year <= through.Year; year++)
        {
            if (year < 1)
            {
                continue;
            }

            var boundary = Boundary(yearBoundary, hiredOn, year);
            if (boundary > after && boundary <= through)
            {
                yield return boundary;
            }
        }
    }

    public static IEnumerable<DateOnly> BoundariesThrough(string yearBoundary, DateOnly hiredOn, DateOnly through)
    {
        for (var year = hiredOn.Year; year <= through.Year; year++)
        {
            var boundary = Boundary(yearBoundary, hiredOn, year);
            if (boundary >= hiredOn && boundary <= through)
            {
                yield return boundary;
            }
        }
    }
}
