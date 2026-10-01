using Harbor;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Host;

public sealed class AccrualPoster(HarborDbContext db)
{
    public async Task PostDueAccruals(DateOnly asOf, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await AuditGuc.Apply(db, AuditStamp.ForJob(), ct);

        var employees = await db.Employees.AsNoTracking().ToListAsync(ct);
        var types = await db.LeaveTypes.AsNoTracking().ToListAsync(ct);
        var payDates = await db.PayPeriods.AsNoTracking()
            .Select(period => period.PayDate)
            .Distinct()
            .ToListAsync(ct);
        var existing = await db.LeaveLedgers.AsNoTracking()
            .Select(row => new BalanceRow(
                row.EmployeeId,
                row.LeaveTypeId,
                row.EffectiveOn,
                row.Hours,
                row.ExternalRef))
            .ToListAsync(ct);
        var refs = existing
            .Select(row => row.ExternalRef)
            .Where(value => value is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var buckets = existing
            .GroupBy(row => (row.EmployeeId, row.LeaveTypeId))
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var employee in employees)
        {
            foreach (var type in types)
            {
                if (type.Model == GrantModel.Unpaid || type.HoursPerGrant is null or <= 0)
                {
                    continue;
                }

                var key = (employee.Id, type.Id);
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    buckets[key] = bucket;
                }

                PostOne(employee, type, asOf, payDates, bucket, refs);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private void PostOne(
        Employee employee,
        LeaveType type,
        DateOnly asOf,
        IReadOnlyList<DateOnly> payDates,
        List<BalanceRow> bucket,
        HashSet<string> refs)
    {
        var dates = new SortedSet<DateOnly>();
        var duePays = new HashSet<DateOnly>();
        if (type.Model == GrantModel.Accrued)
        {
            var start = employee.HiredOn.AddDays(type.WaitingDays);
            foreach (var pay in payDates)
            {
                if (pay < start || pay > asOf)
                {
                    continue;
                }

                if (employee.TerminatedOn is DateOnly ended && pay > ended)
                {
                    continue;
                }

                duePays.Add(pay);
                dates.Add(pay);
            }
        }

        foreach (var boundary in LeaveCalendar.BoundariesThrough(type.YearBoundary, employee.HiredOn, asOf))
        {
            dates.Add(boundary);
        }

        foreach (var date in dates)
        {
            var isBoundary = LeaveCalendar.Boundary(type.YearBoundary, employee.HiredOn, date.Year) == date
                && date >= employee.HiredOn;
            if (isBoundary && type.CarryCapHours is decimal cap)
            {
                var expirationRef = $"expiration:{employee.Id}:{type.Id}:{date:yyyy-MM-dd}";
                if (!refs.Contains(expirationRef))
                {
                    var before = bucket.Where(row => row.EffectiveOn < date).Sum(row => row.Hours);
                    if (before > cap)
                    {
                        var hours = RoundHours(-(before - cap));
                        if (hours != 0)
                        {
                            Add(employee, type, bucket, refs, LedgerKind.Expiration, hours, date, expirationRef);
                        }
                    }
                }
            }

            if (type.Model == GrantModel.Instant
                && isBoundary
                && (employee.TerminatedOn is not DateOnly term || date <= term))
            {
                var grantRef = $"grant:{employee.Id}:{type.Id}:{date.Year.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                if (!refs.Contains(grantRef))
                {
                    var hours = RoundHours(type.HoursPerGrant ?? 0);
                    if (hours > 0)
                    {
                        Add(employee, type, bucket, refs, LedgerKind.Grant, hours, date, grantRef);
                    }
                }
            }

            if (!duePays.Contains(date))
            {
                continue;
            }

            var accrualRef = $"accrual:{employee.Id}:{type.Id}:{date:yyyy-MM-dd}";
            if (refs.Contains(accrualRef))
            {
                continue;
            }

            var balance = bucket.Where(row => row.EffectiveOn <= date).Sum(row => row.Hours);
            var credit = type.HoursPerGrant ?? 0;
            if (type.MaxBalanceHours is decimal max)
            {
                var room = max - balance;
                credit = room <= 0 ? 0 : Math.Min(credit, room);
            }

            credit = RoundHours(credit);
            if (credit > 0)
            {
                Add(employee, type, bucket, refs, LedgerKind.Accrual, credit, date, accrualRef);
            }
        }
    }

    private void Add(
        Employee employee,
        LeaveType type,
        List<BalanceRow> bucket,
        HashSet<string> refs,
        LedgerKind kind,
        decimal hours,
        DateOnly date,
        string externalRef)
    {
        if (!refs.Add(externalRef))
        {
            return;
        }

        db.LeaveLedgers.Add(new LeaveLedger
        {
            Id = Guid.NewGuid(),
            EmployeeId = employee.Id,
            LeaveTypeId = type.Id,
            Kind = kind,
            Hours = hours,
            EffectiveOn = date,
            Source = "schedule",
            ExternalRef = externalRef,
        });
        bucket.Add(new BalanceRow(employee.Id, type.Id, date, hours, externalRef));
    }

    private static decimal RoundHours(decimal hours)
    {
        return Math.Round(hours, 2, MidpointRounding.AwayFromZero);
    }

    private sealed record BalanceRow(
        Guid EmployeeId,
        Guid LeaveTypeId,
        DateOnly EffectiveOn,
        decimal Hours,
        string? ExternalRef);
}

public sealed class AccrualPosterService(
    IServiceScopeFactory scopes,
    ILogger<AccrualPosterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PostOnce(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PostOnce(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PostOnce(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var poster = scope.ServiceProvider.GetRequiredService<AccrualPoster>();
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
            await poster.PostDueAccruals(DateOnly.FromDateTime(local), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Accrual poster failed: {ErrorType}", ex.GetType().Name);
        }
    }
}
