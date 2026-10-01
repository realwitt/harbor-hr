using Harbor;

namespace Harbor.Tests;

public class LeaveProjectionTests
{
    private static readonly DateOnly Hired = new(2020, 1, 1);

    [Fact]
    public void Accrued_grant_after_on_date_is_not_included()
    {
        var result = LeaveProjection.Project(
            Accrued(4),
            Hired,
            new DateOnly(2026, 2, 1),
            new DateOnly(2026, 3, 1),
            [],
            [new DateOnly(2026, 4, 1)],
            true,
            []);

        Assert.Equal(0m, result.AvailableHours);
        Assert.Equal(0m, result.BookedHours);
    }

    [Fact]
    public void Accrued_grant_on_or_before_on_date_is_included_when_not_posted()
    {
        var payDate = new DateOnly(2026, 3, 15);
        var result = LeaveProjection.Project(
            Accrued(4),
            Hired,
            new DateOnly(2026, 3, 1),
            payDate,
            [],
            [payDate],
            true,
            []);

        Assert.Equal(4m, result.AvailableHours);
        Assert.Equal(4m, result.BookedHours);
        Assert.Contains(result.Events, item => item.Date == payDate && item.Kind == "accrual" && item.Hours == 4m);
    }

    [Fact]
    public void Instant_grant_is_usable_before_the_next_boundary()
    {
        var ledger = new[]
        {
            new LedgerSlice(new DateOnly(2026, 1, 1), 40m, LedgerKind.Grant, "schedule", "grant:test:2026"),
        };
        var asOf = new DateOnly(2026, 6, 1);
        var beforeBoundary = LeaveProjection.Project(
            Instant(40),
            Hired,
            asOf,
            new DateOnly(2026, 12, 31),
            ledger,
            [],
            true,
            []);

        Assert.Equal(40m, beforeBoundary.AvailableHours);
        Assert.DoesNotContain(beforeBoundary.Events, item => item.Kind == "grant");
    }

    [Fact]
    public void Date_past_the_boundary_uses_next_years_grant()
    {
        var ledger = new[]
        {
            new LedgerSlice(new DateOnly(2026, 1, 1), 40m, LedgerKind.Grant, "schedule", "grant:test:2026"),
        };
        var result = LeaveProjection.Project(
            Instant(40),
            Hired,
            new DateOnly(2026, 6, 1),
            new DateOnly(2027, 1, 1),
            ledger,
            [],
            true,
            []);

        Assert.Equal(80m, result.AvailableHours);
        Assert.Equal(80m, result.BookedHours);
    }

    [Fact]
    public void Carry_cap_expires_the_excess_at_the_boundary()
    {
        var ledger = new[]
        {
            new LedgerSlice(new DateOnly(2026, 1, 1), 40m, LedgerKind.Grant, "schedule", "grant:test:2026"),
        };
        var result = LeaveProjection.Project(
            Instant(40, carry: 10),
            Hired,
            new DateOnly(2026, 6, 1),
            new DateOnly(2027, 1, 1),
            ledger,
            [],
            true,
            []);

        Assert.Equal(50m, result.AvailableHours);
        Assert.Contains(result.Events, item =>
            item.Date == new DateOnly(2027, 1, 1) && item.Kind == "expiration" && item.Hours == -30m);
    }

    [Fact]
    public void Max_balance_stops_the_accrual()
    {
        var result = LeaveProjection.Project(
            Accrued(4, max: 6),
            Hired,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 2, 1),
            [],
            [new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 29)],
            true,
            []);

        Assert.Equal(6m, result.AvailableHours);
        Assert.Equal(6m, result.BookedHours);
    }

    [Fact]
    public void Pending_hours_reduce_available_and_do_not_reduce_booked()
    {
        var ledger = new[]
        {
            new LedgerSlice(new DateOnly(2026, 1, 1), 16m, LedgerKind.Grant, "schedule", null),
        };
        var result = LeaveProjection.Project(
            Instant(40),
            Hired,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            ledger,
            [],
            true,
            [new PendingDay(new DateOnly(2026, 6, 10), 8m)]);

        Assert.Equal(8m, result.AvailableHours);
        Assert.Equal(16m, result.BookedHours);
    }

    [Fact]
    public void Approved_future_usage_reduces_available_and_booked_once()
    {
        var ledger = new[]
        {
            new LedgerSlice(new DateOnly(2026, 1, 1), 16m, LedgerKind.Grant, "schedule", null),
            new LedgerSlice(new DateOnly(2026, 6, 15), -8m, LedgerKind.Usage, "approval", null),
        };
        var result = LeaveProjection.Project(
            Instant(40),
            Hired,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            ledger,
            [],
            true,
            []);

        Assert.Equal(8m, result.AvailableHours);
        Assert.Equal(8m, result.BookedHours);
        var approved = result.Events.Where(item => item.Kind == "approved_leave").ToList();
        var only = Assert.Single(approved);
        Assert.Equal(-8m, only.Hours);
    }

    [Fact]
    public void Unpaid_does_not_return_a_balance()
    {
        var result = LeaveProjection.Project(
            new LeaveGrantPolicy { Model = GrantModel.Unpaid, HoursPerGrant = null },
            Hired,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 7, 1),
            [new LedgerSlice(new DateOnly(2026, 1, 1), 40m, LedgerKind.Grant, "schedule", null)],
            [new DateOnly(2026, 6, 15)],
            true,
            [new PendingDay(new DateOnly(2026, 6, 20), 8m)]);

        Assert.False(result.HasBalance);
        Assert.Null(result.AvailableHours);
        Assert.Null(result.BookedHours);
    }

    private static LeaveGrantPolicy Accrued(decimal hours, decimal? max = null, decimal? carry = null) => new()
    {
        Model = GrantModel.Accrued,
        HoursPerGrant = hours,
        YearBoundary = "calendar",
        MaxBalanceHours = max,
        CarryCapHours = carry,
    };

    private static LeaveGrantPolicy Instant(decimal hours, decimal? carry = null) => new()
    {
        Model = GrantModel.Instant,
        HoursPerGrant = hours,
        YearBoundary = "calendar",
        CarryCapHours = carry,
    };
}
