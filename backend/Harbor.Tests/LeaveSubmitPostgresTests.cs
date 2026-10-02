using Harbor;
using Harbor.Host;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Tests;

[Collection("harbor_test")]
public class LeaveSubmitPostgresTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly AsOf = new(2026, 6, 1);

    [Fact]
    public async Task Overlapping_pending_submits_the_second_fails()
    {
        await using var db = Open();
        var employee = await CreateEmployee(db);
        var unpaid = await UnpaidType(db);
        var workflow = new LeaveWorkflow(db);
        var first = await Preview(workflow, employee, unpaid, "2026-06-15");
        var second = await Preview(workflow, employee, unpaid, "2026-06-15");
        var saved = await Submit(workflow, employee, unpaid, first.QuoteId, "overlap-a", "mcp", true, "2026-06-15");
        var rejected = await Submit(workflow, employee, unpaid, second.QuoteId, "overlap-b", "mcp", true, "2026-06-15");

        Assert.True(saved.Succeeded, string.Join("; ", saved.Errors.Select(error => error.Code + ": " + error.Message)));
        Assert.False(rejected.Succeeded);
        Assert.Contains(rejected.Errors, error => error.Code == LeaveRequestPolicy.Overlap);
        await using var check = Open();
        var count = await check.LeaveRequests.AsNoTracking().CountAsync(row => row.EmployeeId == employee);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Approve_writes_usage_and_the_same_idempotency_key_does_not_add_a_request()
    {
        await using var db = Open();
        var employee = await CreateEmployee(db);
        var manager = await CreateEmployee(db);
        db.ManagerLinks.Add(new ManagerLink
        {
            EmployeeId = employee,
            ManagerId = manager,
            EffectiveOn = new DateOnly(2024, 1, 15),
        });
        await db.SaveChangesAsync();
        var unpaid = await UnpaidType(db);
        var workflow = new LeaveWorkflow(db);
        var preview = await Preview(workflow, employee, unpaid, "2026-06-16");
        var key = "approve-" + Guid.NewGuid().ToString("N");
        var saved = await Submit(workflow, employee, unpaid, preview.QuoteId, key, "mcp", true, "2026-06-16");
        var approved = await workflow.ApproveAsync(new LeaveDecisionRequest
        {
            ActorId = manager,
            RequestId = saved.Value!.RequestId,
            Channel = "web",
            AsOf = AsOf,
            Now = Now,
            TraceId = "phase2-approve",
        });
        var replay = await Submit(workflow, employee, unpaid, preview.QuoteId, key, "mcp", true, "2026-06-16");

        Assert.True(approved.Succeeded);
        Assert.True(replay.Succeeded);
        Assert.True(replay.Value!.Replay);
        Assert.Equal(saved.Value.RequestId, replay.Value.RequestId);

        await using var check = Open();
        var requests = await check.LeaveRequests.AsNoTracking().CountAsync(row => row.EmployeeId == employee);
        var usage = await check.LeaveLedgers.AsNoTracking()
            .Where(row => row.RequestId == saved.Value.RequestId)
            .ToListAsync();
        Assert.Equal(1, requests);
        var row = Assert.Single(usage);
        Assert.Equal(LedgerKind.Usage, row.Kind);
        Assert.Equal(-8m, row.Hours);
        Assert.Equal("approval", row.Source);
        Assert.Equal(new DateOnly(2026, 6, 16), row.EffectiveOn);
        Assert.Equal(saved.Value.RequestId, row.RequestId);
    }

    [Fact]
    public async Task Web_submit_without_confirmed_at_is_rejected()
    {
        await using var db = Open();
        var employee = await CreateEmployee(db);
        var unpaid = await UnpaidType(db);
        var workflow = new LeaveWorkflow(db);
        var preview = await Preview(workflow, employee, unpaid, "2026-06-17");
        var rejected = await Submit(workflow, employee, unpaid, preview.QuoteId, "web-" + Guid.NewGuid().ToString("N"), "web", true, "2026-06-17");

        Assert.False(rejected.Succeeded);
        Assert.Contains(rejected.Errors, error => error.Code == "quote_unconfirmed");
        await using var check = Open();
        var count = await check.LeaveRequests.AsNoTracking().CountAsync(row => row.EmployeeId == employee);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Mcp_submit_with_confirm_consumes_the_quote_and_inserts_one_pending_request()
    {
        await using var db = Open();
        var employee = await CreateEmployee(db);
        var unpaid = await UnpaidType(db);
        var workflow = new LeaveWorkflow(db);
        var preview = await Preview(workflow, employee, unpaid, "2026-06-18");
        var saved = await Submit(workflow, employee, unpaid, preview.QuoteId, "mcp-" + Guid.NewGuid().ToString("N"), "mcp", true, "2026-06-18");

        Assert.True(saved.Succeeded, string.Join("; ", saved.Errors.Select(error => error.Code + ": " + error.Message)));
        Assert.False(saved.Value!.Replay);
        await using var check = Open();
        var quote = await check.ActionQuotes.AsNoTracking().SingleAsync(row => row.Id == preview.QuoteId);
        var requests = await check.LeaveRequests.AsNoTracking()
            .Where(row => row.EmployeeId == employee)
            .ToListAsync();
        var days = await check.LeaveRequestDays.AsNoTracking()
            .CountAsync(row => row.RequestId == saved.Value.RequestId);
        Assert.NotNull(quote.ConsumedAt);
        Assert.NotNull(quote.ConfirmedAt);
        var request = Assert.Single(requests);
        Assert.Equal(LeaveStatus.Pending, request.Status);
        Assert.Equal(1, days);
    }

    private static async Task<LeavePreview> Preview(LeaveWorkflow workflow, Guid employee, Guid leaveType, string day)
    {
        var result = await workflow.PreviewAsync(new LeavePreviewRequest
        {
            EmployeeId = employee,
            ActorId = employee,
            LeaveTypeId = leaveType,
            Start = day,
            End = day,
            HoursPerDay = 8,
            Channel = "mcp",
            AsOf = AsOf,
            Now = Now,
            RequestId = "phase2",
        });
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Code + ": " + error.Message)));
        return result.Value!;
    }

    private static async Task<WorkflowResult<LeaveCommandResult>> Submit(
        LeaveWorkflow workflow,
        Guid employee,
        Guid leaveType,
        Guid quoteId,
        string key,
        string channel,
        bool confirm,
        string day)
    {
        return await workflow.SubmitAsync(new LeaveSubmitRequest
        {
            EmployeeId = employee,
            ActorId = employee,
            QuoteId = quoteId,
            IdempotencyKey = key,
            LeaveTypeId = leaveType,
            Start = day,
            End = day,
            HoursPerDay = 8,
            Channel = channel,
            Confirm = confirm,
            AsOf = AsOf,
            Now = Now,
            RequestId = "phase2",
        });
    }

    private static async Task<Guid> CreateEmployee(HarborDbContext db)
    {
        var id = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = id,
            Email = $"phase2-{id:N}@example.com",
            Name = "Phase Two",
            Jurisdiction = "US-NC",
            HiredOn = new DateOnly(2024, 1, 15),
            RecoverySavedAt = Now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> UnpaidType(HarborDbContext db)
    {
        return await db.LeaveTypes.Where(row => row.Code == "unpaid").Select(row => row.Id).SingleAsync();
    }

    private static HarborDbContext Open()
    {
        var options = new DbContextOptionsBuilder<HarborDbContext>()
            .UseNpgsql(AppConnection(), HarborDbContext.MapEnums)
            .Options;
        return new HarborDbContext(options);
    }

    private static string AppConnection()
    {
        string? password = null;
        foreach (var line in File.ReadAllLines("/Users/ewitt/hr-app/.secrets/dev-db.env"))
        {
            var trimmed = line.Trim();
            var split = trimmed.IndexOf('=');
            if (split <= 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed[..split].Trim() == "HARBOR_APP_PASSWORD")
            {
                password = trimmed[(split + 1)..].Trim();
            }
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("HARBOR_APP_PASSWORD is missing.");
        }

        return $"Host=127.0.0.1;Port=5432;Database=harbor_test;Username=harbor_app;Password={password};Timeout=15";
    }
}
