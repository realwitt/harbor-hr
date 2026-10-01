namespace Harbor;

public sealed record LeaveGrantPolicy
{
    public required GrantModel Model { get; init; }
    public decimal? HoursPerGrant { get; init; }
    public string YearBoundary { get; init; } = "calendar";
    public decimal? CarryCapHours { get; init; }
    public decimal? MaxBalanceHours { get; init; }
    public int WaitingDays { get; init; }
}

public sealed record LedgerSlice(
    DateOnly EffectiveOn,
    decimal Hours,
    LedgerKind Kind,
    string Source,
    string? ExternalRef);

public sealed record PendingDay(DateOnly OnDate, decimal Hours);

public sealed record ProjectionEvent(DateOnly Date, string Kind, decimal Hours);

public sealed record LeaveProjectionResult
{
    public bool HasBalance { get; init; }
    public decimal? AvailableHours { get; init; }
    public decimal? BookedHours { get; init; }
    public IReadOnlyList<ProjectionEvent> Events { get; init; } = [];
}

public static class LeaveProjection
{
    public static LeaveProjectionResult Project(
        LeaveGrantPolicy policy,
        DateOnly hiredOn,
        DateOnly asOf,
        DateOnly onDate,
        IReadOnlyList<LedgerSlice> ledger,
        IReadOnlyList<DateOnly> payDates,
        bool approvedFutureAlreadyInLedger,
        IReadOnlyList<PendingDay> pendingDays)
    {
        if (policy.Model == GrantModel.Unpaid)
        {
            return new LeaveProjectionResult { HasBalance = false };
        }

        var balance = ledger.Where(row => row.EffectiveOn <= asOf).Sum(row => row.Hours);
        var events = new List<ProjectionEvent>();
        var future = ledger
            .Where(row => row.EffectiveOn > asOf && row.EffectiveOn <= onDate)
            .ToList();

        if (onDate > asOf)
        {
            var boundaries = LeaveCalendar
                .BoundariesAfter(policy.YearBoundary, hiredOn, asOf, onDate)
                .ToHashSet();
            var accrualStart = hiredOn.AddDays(policy.WaitingDays);
            var duePays = payDates
                .Where(date => date > asOf && date <= onDate && date >= accrualStart)
                .ToHashSet();
            var dates = new SortedSet<DateOnly>(boundaries);
            foreach (var date in duePays)
            {
                dates.Add(date);
            }

            foreach (var row in future)
            {
                dates.Add(row.EffectiveOn);
            }

            foreach (var date in dates)
            {
                var today = future.Where(row => row.EffectiveOn == date).ToList();
                var isBoundary = boundaries.Contains(date);
                if (isBoundary)
                {
                    ApplyBoundary(policy, ledger, today, date, ref balance, events);
                }

                foreach (var row in today.Where(row => row.Kind != LedgerKind.Expiration))
                {
                    balance += row.Hours;
                    events.Add(new ProjectionEvent(date, EventKind(row), row.Hours));
                }

                if (policy.Model == GrantModel.Accrued
                    && duePays.Contains(date)
                    && !HasScheduleAccrual(ledger, date))
                {
                    var credit = CapAccrual(policy, balance);
                    if (credit > 0)
                    {
                        balance += credit;
                        events.Add(new ProjectionEvent(date, "accrual", credit));
                    }
                }

                if (policy.Model == GrantModel.Instant
                    && isBoundary
                    && !HasYearGrant(ledger, date))
                {
                    var credit = policy.HoursPerGrant ?? 0m;
                    if (credit > 0)
                    {
                        balance += credit;
                        events.Add(new ProjectionEvent(date, "grant", credit));
                    }
                }
            }
        }

        // Approved future usage is already a negative ledger row when the flag is true.
        // Those hours are in the balance above. Do not subtract them again.
        _ = approvedFutureAlreadyInLedger;

        var pending = pendingDays.Where(day => day.OnDate <= onDate).Sum(day => day.Hours);
        return new LeaveProjectionResult
        {
            HasBalance = true,
            AvailableHours = balance - pending,
            BookedHours = balance,
            Events = events,
        };
    }

    private static void ApplyBoundary(
        LeaveGrantPolicy policy,
        IReadOnlyList<LedgerSlice> ledger,
        List<LedgerSlice> today,
        DateOnly date,
        ref decimal balance,
        List<ProjectionEvent> events)
    {
        var existing = today.Where(row => row.Kind == LedgerKind.Expiration).ToList();
        if (existing.Count > 0)
        {
            foreach (var row in existing)
            {
                balance += row.Hours;
                events.Add(new ProjectionEvent(date, "expiration", row.Hours));
            }
        }
        else if (policy.CarryCapHours is decimal cap && balance > cap && !HasExpiration(ledger, date))
        {
            var expired = balance - cap;
            balance -= expired;
            events.Add(new ProjectionEvent(date, "expiration", -expired));
        }

        events.Add(new ProjectionEvent(date, "year_reset", balance));
    }

    private static decimal CapAccrual(LeaveGrantPolicy policy, decimal balance)
    {
        var credit = policy.HoursPerGrant ?? 0m;
        if (policy.MaxBalanceHours is not decimal max)
        {
            return credit;
        }

        var room = max - balance;
        if (room <= 0)
        {
            return 0;
        }

        return credit > room ? room : credit;
    }

    private static bool HasScheduleAccrual(IReadOnlyList<LedgerSlice> ledger, DateOnly payDate)
    {
        return ledger.Any(row =>
            row.EffectiveOn == payDate
            && row.Kind == LedgerKind.Accrual
            && string.Equals(row.Source, "schedule", StringComparison.Ordinal));
    }

    private static bool HasYearGrant(IReadOnlyList<LedgerSlice> ledger, DateOnly boundary)
    {
        var suffix = ":" + boundary.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ledger.Any(row =>
            row.Kind == LedgerKind.Grant
            && (row.EffectiveOn == boundary
                || (row.ExternalRef?.EndsWith(suffix, StringComparison.Ordinal) ?? false)));
    }

    private static bool HasExpiration(IReadOnlyList<LedgerSlice> ledger, DateOnly boundary)
    {
        return ledger.Any(row => row.Kind == LedgerKind.Expiration && row.EffectiveOn == boundary);
    }

    private static string EventKind(LedgerSlice row)
    {
        return row.Kind switch
        {
            LedgerKind.Accrual => "accrual",
            LedgerKind.Grant => "grant",
            LedgerKind.Usage => "approved_leave",
            LedgerKind.Expiration => "expiration",
            _ => row.Kind.ToString().ToLowerInvariant(),
        };
    }
}
