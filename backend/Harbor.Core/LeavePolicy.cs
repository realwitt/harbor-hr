using System.Globalization;

namespace Harbor;

public sealed record LeaveWarning(string Code, string Message);

public sealed record LeavePolicyResult
{
    public required IReadOnlyList<LeaveWarning> Warnings { get; init; }
    public required IReadOnlyList<DateOnly> Days { get; init; }
    public decimal HoursPerDay { get; init; }
}

public static class LeaveRequestPolicy
{
    public const string Overlap = "overlap";
    public const string Blackout = "blackout";
    public const string InsufficientBalance = "insufficient_balance";
    public const string InvalidDate = "invalid_date";
    public const string InvalidHours = "invalid_hours";
    public const string Terminated = "terminated";

    public static bool TryParseIsoDate(string? text, out DateOnly date)
    {
        date = default;
        if (text is null || text.Length != 10)
        {
            return false;
        }

        return DateOnly.TryParseExact(
            text,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    public static LeavePolicyResult Evaluate(
        GrantModel model,
        DateOnly? terminatedOn,
        decimal? availableOnStart,
        bool hasBalance,
        EmployeeRole callerRole,
        bool adminOverride,
        string? startText,
        string? endText,
        decimal? hoursPerDay,
        IReadOnlyCollection<DateOnly> openDays,
        IReadOnlyCollection<DateOnly> blackouts)
    {
        var warnings = new List<LeaveWarning>();
        var hours = hoursPerDay ?? 8m;
        if (hoursPerDay is not null && (hours <= 0 || hours > 24))
        {
            warnings.Add(new LeaveWarning(InvalidHours, "Hours per day must be greater than 0 and at most 24."));
        }

        var startOk = TryParseIsoDate(startText, out var start);
        var endOk = TryParseIsoDate(endText, out var end);
        if (!startOk || !endOk || end < start)
        {
            warnings.Add(new LeaveWarning(InvalidDate, "The date is not an ISO date."));
            return new LeavePolicyResult
            {
                Warnings = warnings,
                Days = [],
                HoursPerDay = hours,
            };
        }

        if (terminatedOn is DateOnly ended && ended <= start)
        {
            warnings.Add(new LeaveWarning(Terminated, "The employee is terminated on or before the first day."));
        }

        var days = new List<DateOnly>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            days.Add(day);
        }

        var open = openDays as ISet<DateOnly> ?? openDays.ToHashSet();
        var blocked = blackouts as ISet<DateOnly> ?? blackouts.ToHashSet();
        var allowBlackout = adminOverride && callerRole == EmployeeRole.HrAdmin;
        foreach (var day in days)
        {
            if (open.Contains(day))
            {
                warnings.Add(new LeaveWarning(Overlap, "This request overlaps an open leave day."));
            }

            if (blocked.Contains(day) && !allowBlackout)
            {
                warnings.Add(new LeaveWarning(Blackout, "This date is a blackout."));
            }
        }

        if (model != GrantModel.Unpaid && hasBalance)
        {
            var requested = hours * days.Count;
            var available = availableOnStart ?? 0m;
            if (requested > available)
            {
                warnings.Add(new LeaveWarning(
                    InsufficientBalance,
                    "The request uses more hours than the projected balance."));
            }
        }

        return new LeavePolicyResult
        {
            Warnings = warnings,
            Days = days,
            HoursPerDay = hours,
        };
    }
}
