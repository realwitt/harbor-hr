using System.ComponentModel;
using System.Text.Json;
using Harbor;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Harbor.Host;

[McpServerToolType]
public sealed class HarborMcpTools
{
    [McpServerTool(Name = "whoami", ReadOnly = true, Destructive = false, OpenWorld = false), Description("Show the signed-in employee.")]
    public static Task<CallToolResult> Whoami(IHttpContextAccessor http, HarborDbContext db, CancellationToken ct) =>
        Run(http, db, "whoami", (caller, token) => WhoamiCore(db, caller, token), ct);

    [McpServerTool(Name = "get_leave_balance", ReadOnly = true, Destructive = false, OpenWorld = false), Description("Show leave balances for today.")]
    public static Task<CallToolResult> GetLeaveBalance(
        IHttpContextAccessor http,
        HarborDbContext db,
        HarborBusiness business,
        CancellationToken ct) =>
        Run(http, db, "get_leave_balance", (caller, token) => Balances(db, business, caller, null, token), ct);

    [McpServerTool(Name = "project_leave_balance", ReadOnly = true, Destructive = false, OpenWorld = false), Description("Show leave balances on an ISO date.")]
    public static Task<CallToolResult> ProjectLeaveBalance(
        IHttpContextAccessor http,
        HarborDbContext db,
        HarborBusiness business,
        [Description("ISO date yyyy-MM-dd.")] string on,
        CancellationToken ct) =>
        Run(http, db, "project_leave_balance", (caller, token) => Balances(db, business, caller, on, token), ct);

    [McpServerTool(Name = "list_my_requests", ReadOnly = true, Destructive = false, OpenWorld = false), Description("List the signed-in employee's leave requests.")]
    public static Task<CallToolResult> ListMyRequests(
        IHttpContextAccessor http,
        HarborDbContext db,
        HarborBusiness business,
        CancellationToken ct) =>
        Run(http, db, "list_my_requests", async (caller, token) =>
        {
            var result = await business.OwnRequests(caller.EmployeeId, token);
            return result.Status == StatusCodes.Status200OK
                ? Ok(result.Body)
                : Fail(JsonSerializer.Serialize(result.Body, HarborJson.Options));
        }, ct);

    [McpServerTool(Name = "preview_leave_request", ReadOnly = true, Destructive = false, OpenWorld = false), Description("Preview a leave request. Start and end are ISO dates.")]
    public static Task<CallToolResult> PreviewLeaveRequest(
        IHttpContextAccessor http,
        HarborDbContext db,
        LeaveWorkflow workflow,
        Guid leaveTypeId,
        string start,
        string end,
        decimal hoursPerDay,
        CancellationToken ct) =>
        Run(http, db, "preview_leave_request", async (caller, token) =>
        {
            if (!Iso(start) || !Iso(end))
            {
                return Fail("The date is not an ISO date.");
            }

            if (await Today(db, caller.EmployeeId, token) is not DateOnly today)
            {
                return Fail("The employee timezone is not valid.");
            }

            var result = await workflow.PreviewAsync(new LeavePreviewRequest
            {
                EmployeeId = caller.EmployeeId,
                ActorId = caller.EmployeeId,
                LeaveTypeId = leaveTypeId,
                Start = start,
                End = end,
                HoursPerDay = hoursPerDay,
                AdminOverride = false,
                Channel = "mcp",
                ClientId = caller.ClientId,
                AsOf = today,
                Now = DateTimeOffset.UtcNow,
            }, token);
            if (!result.Succeeded || result.Value is null)
            {
                return Fail(Errors(result.Errors));
            }

            return Ok(new
            {
                quoteId = result.Value.QuoteId,
                expiresAt = result.Value.ExpiresAt,
                warnings = result.Value.Warnings,
            });
        }, ct);

    [McpServerTool(Name = "submit_leave_request", Destructive = false, Idempotent = true, OpenWorld = false), Description("Submit a leave request. confirm must be true.")]
    public static Task<CallToolResult> SubmitLeaveRequest(
        IHttpContextAccessor http,
        HarborDbContext db,
        LeaveWorkflow workflow,
        Guid quoteId,
        Guid leaveTypeId,
        string start,
        string end,
        string idempotencyKey,
        bool confirm,
        decimal hoursPerDay,
        CancellationToken ct) =>
        Run(http, db, "submit_leave_request", async (caller, token) =>
        {
            if (!confirm)
            {
                return Fail("Confirm is required.");
            }

            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return Fail("The idempotency key is required.");
            }

            if (!Iso(start) || !Iso(end))
            {
                return Fail("The date is not an ISO date.");
            }

            if (await Today(db, caller.EmployeeId, token) is not DateOnly today)
            {
                return Fail("The employee timezone is not valid.");
            }

            var result = await workflow.SubmitAsync(new LeaveSubmitRequest
            {
                EmployeeId = caller.EmployeeId,
                ActorId = caller.EmployeeId,
                QuoteId = quoteId,
                IdempotencyKey = idempotencyKey,
                LeaveTypeId = leaveTypeId,
                Start = start,
                End = end,
                HoursPerDay = hoursPerDay,
                AdminOverride = false,
                Channel = "mcp",
                Confirm = true,
                ClientId = caller.ClientId,
                AsOf = today,
                Now = DateTimeOffset.UtcNow,
            }, token);
            if (!result.Succeeded || result.Value is null)
            {
                return Fail(Errors(result.Errors));
            }

            return Ok(new
            {
                requestId = result.Value.RequestId,
                status = result.Value.Status,
                replay = result.Value.Replay,
            });
        }, ct);

    [McpServerTool(Name = "cancel_leave_request", Destructive = false, Idempotent = true, OpenWorld = false), Description("Cancel a pending leave request. confirm must be true.")]
    public static Task<CallToolResult> CancelLeaveRequest(
        IHttpContextAccessor http,
        HarborDbContext db,
        LeaveWorkflow workflow,
        Guid requestId,
        string idempotencyKey,
        bool confirm,
        CancellationToken ct) =>
        Run(http, db, "cancel_leave_request", async (caller, token) =>
        {
            if (!confirm)
            {
                return Fail("Confirm is required.");
            }

            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return Fail("The idempotency key is required.");
            }

            var now = DateTimeOffset.UtcNow;
            var preview = await workflow.PreviewCancelAsync(new LeaveCancelPreviewRequest
            {
                EmployeeId = caller.EmployeeId,
                ActorId = caller.EmployeeId,
                RequestId = requestId,
                Channel = "mcp",
                ClientId = caller.ClientId,
                Now = now,
            }, token);
            if (!preview.Succeeded || preview.Value is null)
            {
                return Fail(Errors(preview.Errors));
            }

            var result = await workflow.CancelAsync(new LeaveCancelRequest
            {
                EmployeeId = caller.EmployeeId,
                ActorId = caller.EmployeeId,
                QuoteId = preview.Value.QuoteId,
                RequestId = requestId,
                Channel = "mcp",
                Confirm = true,
                ClientId = caller.ClientId,
                Now = now,
            }, token);
            if (!result.Succeeded || result.Value is null)
            {
                return Fail(Errors(result.Errors));
            }

            return Ok(new
            {
                requestId = result.Value.RequestId,
                status = result.Value.Status,
                replay = result.Value.Replay,
            });
        }, ct);

    [McpServerTool(Name = "list_deduction_elections", ReadOnly = true, Destructive = false, OpenWorld = false), Description("List the signed-in employee's deduction elections.")]
    public static Task<CallToolResult> ListDeductionElections(
        IHttpContextAccessor http,
        HarborDbContext db,
        CancellationToken ct) =>
        Run(http, db, "list_deduction_elections", async (caller, token) =>
        {
            var rows = await db.DeductionElections.AsNoTracking()
                .Where(row => row.EmployeeId == caller.EmployeeId)
                .OrderByDescending(row => row.CreatedAt)
                .Select(row => new
                {
                    id = row.Id,
                    kind = row.Kind,
                    perPaycheckCents = row.PerPaycheckCents,
                    status = row.Status,
                    effectiveOn = row.EffectiveOn,
                    endedOn = row.EndedOn,
                })
                .ToListAsync(token);
            return Ok(rows);
        }, ct);

    [McpServerTool(Name = "preview_deduction", ReadOnly = true, Destructive = false, OpenWorld = false), Description("Preview a deduction election.")]
    public static Task<CallToolResult> PreviewDeduction(
        IHttpContextAccessor http,
        HarborDbContext db,
        DeductionWorkflow workflow,
        string kind,
        int perPaycheckCents,
        string? qualifyingEvent,
        CancellationToken ct) =>
        Run(http, db, "preview_deduction", async (caller, token) =>
        {
            if (await Today(db, caller.EmployeeId, token) is not DateOnly today)
            {
                return Fail("The employee timezone is not valid.");
            }

            var result = await workflow.PreviewAsync(new DeductionPreviewRequest
            {
                EmployeeId = caller.EmployeeId,
                ActorId = caller.EmployeeId,
                Kind = kind,
                PerPaycheckCents = perPaycheckCents,
                QualifyingEvent = qualifyingEvent,
                Channel = "mcp",
                ClientId = caller.ClientId,
                AsOf = today,
                Now = DateTimeOffset.UtcNow,
            }, token);
            if (!result.Succeeded || result.Value is null)
            {
                return Fail(Errors(result.Errors));
            }

            return Ok(new
            {
                quoteId = result.Value.QuoteId,
                expiresAt = result.Value.ExpiresAt,
                estimate = result.Value.Estimate,
            });
        }, ct);

    [McpServerTool(Name = "submit_deduction", Destructive = false, Idempotent = true, OpenWorld = false), Description("Submit a deduction election. confirm must be true.")]
    public static Task<CallToolResult> SubmitDeduction(
        IHttpContextAccessor http,
        HarborDbContext db,
        DeductionWorkflow workflow,
        Guid quoteId,
        string kind,
        int perPaycheckCents,
        string idempotencyKey,
        bool confirm,
        string? qualifyingEvent,
        CancellationToken ct) =>
        Run(http, db, "submit_deduction", async (caller, token) =>
        {
            if (!confirm)
            {
                return Fail("Confirm is required.");
            }

            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return Fail("The idempotency key is required.");
            }

            if (await Today(db, caller.EmployeeId, token) is not DateOnly today)
            {
                return Fail("The employee timezone is not valid.");
            }

            var result = await workflow.SubmitAsync(new DeductionSubmitRequest
            {
                EmployeeId = caller.EmployeeId,
                ActorId = caller.EmployeeId,
                QuoteId = quoteId,
                IdempotencyKey = idempotencyKey,
                Kind = kind,
                PerPaycheckCents = perPaycheckCents,
                QualifyingEvent = qualifyingEvent,
                Channel = "mcp",
                Confirm = true,
                ClientId = caller.ClientId,
                AsOf = today,
                Now = DateTimeOffset.UtcNow,
            }, token);
            if (!result.Succeeded || result.Value is null)
            {
                return Fail(Errors(result.Errors));
            }

            return Ok(new
            {
                electionId = result.Value.ElectionId,
                status = result.Value.Status,
                replay = result.Value.Replay,
            });
        }, ct);

    private static async Task<CallToolResult> WhoamiCore(HarborDbContext db, McpCaller caller, CancellationToken ct)
    {
        var employee = await db.Employees.AsNoTracking()
            .Where(row => row.Id == caller.EmployeeId)
            .Select(row => new { employeeId = row.Id, name = row.Name, email = row.Email })
            .FirstOrDefaultAsync(ct);
        return employee is null ? Fail("The employee is missing.") : Ok(employee);
    }

    private static async Task<CallToolResult> Balances(
        HarborDbContext db,
        HarborBusiness business,
        McpCaller caller,
        string? on,
        CancellationToken ct)
    {
        if (await Today(db, caller.EmployeeId, ct) is not DateOnly today)
        {
            return Fail("The employee timezone is not valid.");
        }

        DateOnly date;
        if (on is null)
        {
            date = today;
        }
        else if (!LeaveRequestPolicy.TryParseIsoDate(on, out date))
        {
            return Fail("The date is not an ISO date.");
        }

        var employee = await db.Employees.AsNoTracking().FirstAsync(row => row.Id == caller.EmployeeId, ct);
        var result = await business.Balances(employee, today, date, ct);
        return result.Status == StatusCodes.Status200OK
            ? Ok(result.Body)
            : Fail(JsonSerializer.Serialize(result.Body, HarborJson.Options));
    }

    private static async Task<CallToolResult> Run(
        IHttpContextAccessor http,
        HarborDbContext db,
        string tool,
        Func<McpCaller, CancellationToken, Task<CallToolResult>> action,
        CancellationToken ct)
    {
        McpClientGate.DetachIdle(db);
        if (!TryCaller(http.HttpContext, out var caller))
        {
            await Record(db, null, null, tool, "denied", ct);
            return Fail("The caller is missing.");
        }

        try
        {
            var result = await action(caller, ct);
            await Record(db, caller.EmployeeId, caller.ClientId, tool, result.IsError == true ? "denied" : "ok", ct);
            return result;
        }
        catch
        {
            await Record(db, caller.EmployeeId, caller.ClientId, tool, "denied", ct);
            throw;
        }
    }

    private static async Task Record(
        HarborDbContext db,
        Guid? actorId,
        Guid? clientId,
        string tool,
        string outcome,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await AuditGuc.Apply(db, new AuditStamp(actorId, "mcp", clientId, "session", null, null, null), ct);
        await AuditAccess.Write(db, "mcp_tool", outcome, actorId, tool, ct);
        await tx.CommitAsync(ct);
    }

    private static bool TryCaller(HttpContext? http, out McpCaller caller)
    {
        caller = default;
        var user = http?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (!Guid.TryParse(McpClientGate.Subject(user), out var employeeId)
            || !Guid.TryParse(McpClientGate.Claim(user, McpClaims.ClientRow), out var clientId))
        {
            return false;
        }

        caller = new McpCaller(employeeId, clientId);
        return true;
    }

    private static async Task<DateOnly?> Today(HarborDbContext db, Guid employeeId, CancellationToken ct)
    {
        var zone = await db.Employees.AsNoTracking()
            .Where(row => row.Id == employeeId)
            .Select(row => row.Timezone)
            .FirstOrDefaultAsync(ct);
        if (zone is null || !EmployeeClock.TryToday(zone, DateTimeOffset.UtcNow, out var today, out _))
        {
            return null;
        }

        return today;
    }

    private static bool Iso(string? text) => LeaveRequestPolicy.TryParseIsoDate(text, out _);

    private static string Errors(IReadOnlyList<WorkflowError> errors) =>
        JsonSerializer.Serialize(errors, HarborJson.Options);

    private static CallToolResult Ok(object body) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(body, HarborJson.Options) }],
    };

    private static CallToolResult Fail(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private readonly record struct McpCaller(Guid EmployeeId, Guid ClientId);
}

public sealed class HarborMcpResources
{
    [McpServerResource(UriTemplate = "leave-policy://me", Name = "leave-policy", MimeType = "text/plain")]
    [Description("The tools act only as the signed-in employee.")]
    public static string LeavePolicy() => "These tools act only as the signed-in employee.";
}
