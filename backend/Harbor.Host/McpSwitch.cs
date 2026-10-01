using Microsoft.EntityFrameworkCore;

namespace Harbor.Host;

public static class McpSwitch
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/mcp/enable", EnableAsync);
        group.MapGet("/mcp/clients", ListAsync);
        group.MapPost("/mcp/clients/{id:guid}/revoke", RevokeAsync);
    }

    private static async Task<IResult> EnableAsync(HttpContext http, HarborDbContext db, CancellationToken ct)
    {
        if (Gate(http) is IResult blocked)
        {
            return blocked;
        }

        var caller = HarborCaller.Read(http)!;
        if (!await StepUp(db, caller.Employee.Id, StepUpActions.McpOn, ct))
        {
            return Fail("step_up_required", StatusCodes.Status403Forbidden);
        }

        var employee = await db.Employees.FirstAsync(row => row.Id == caller.Employee.Id, ct);
        var now = DateTimeOffset.UtcNow;
        if (employee.McpEnabledAt is null)
        {
            employee.McpEnabledAt = now;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await AuditGuc.Apply(db, Stamp(caller.Employee.Id, null, http.TraceIdentifier), ct);
            McpClientGate.DetachIdle(db);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return Results.Json(new { mcpEnabledAt = employee.McpEnabledAt });
    }

    private static async Task<IResult> ListAsync(HttpContext http, HarborDbContext db, CancellationToken ct)
    {
        if (Gate(http) is IResult blocked)
        {
            return blocked;
        }

        var caller = HarborCaller.Read(http)!;
        var rows = await db.McpClients.AsNoTracking()
            .Where(row => row.EmployeeId == caller.Employee.Id)
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => new
            {
                id = row.Id,
                clientName = row.ClientName,
                createdAt = row.CreatedAt,
                revokedAt = row.RevokedAt,
            })
            .ToListAsync(ct);
        return Results.Json(rows);
    }

    private static async Task<IResult> RevokeAsync(Guid id, HttpContext http, HarborDbContext db, CancellationToken ct)
    {
        if (Gate(http) is IResult blocked)
        {
            return blocked;
        }

        var caller = HarborCaller.Read(http)!;
        if (!await StepUp(db, caller.Employee.Id, StepUpActions.RevokeClient, ct))
        {
            return Fail("step_up_required", StatusCodes.Status403Forbidden);
        }

        var row = await db.McpClients.FirstOrDefaultAsync(
            item => item.Id == id && item.EmployeeId == caller.Employee.Id,
            ct);
        if (row is null)
        {
            return Fail("not_found", StatusCodes.Status404NotFound);
        }

        if (row.RevokedAt is null)
        {
            row.RevokedAt = DateTimeOffset.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await AuditGuc.Apply(db, Stamp(caller.Employee.Id, row.Id, http.TraceIdentifier), ct);
            McpClientGate.DetachIdle(db);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return Results.Json(new { id = row.Id, revokedAt = row.RevokedAt });
    }

    private static async Task<bool> StepUp(HarborDbContext db, Guid employeeId, string action, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-5);
        return await db.WebauthnChallenges.AsNoTracking().AnyAsync(row =>
            row.EmployeeId == employeeId
            && row.Kind == "step_up"
            && row.Action == action
            && row.ConsumedAt != null
            && row.ConsumedAt >= cutoff
            && row.ExpiresAt > now, ct);
    }

    private static IResult? Gate(HttpContext http)
    {
        var caller = HarborCaller.Read(http);
        if (caller is null)
        {
            return Fail("sign_in_required", StatusCodes.Status401Unauthorized);
        }

        if (!caller.Ready)
        {
            return Fail("account_not_ready", StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static AuditStamp Stamp(Guid actorId, Guid? clientId, string? requestId) =>
        new(actorId, "web", clientId, "session", requestId, null, null);

    private static IResult Fail(string code, int status) =>
        Results.Json(new { error = code }, statusCode: status);
}
