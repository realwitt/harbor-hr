using Microsoft.EntityFrameworkCore;

namespace Harbor.Host;

public sealed class HarborSessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, HarborDbContext db)
    {
        var caller = await LoadAsync(http, db, http.RequestAborted);
        if (caller is not null)
        {
            http.Items[HarborCaller.ItemKey] = caller;
        }

        if (IsOpen(http.Request.Path))
        {
            await next(http);
            return;
        }

        if (caller is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await http.Response.WriteAsJsonAsync(new { error = "sign_in_required" }, http.RequestAborted);
            return;
        }

        if (!caller.Ready)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { error = "account_not_ready" }, http.RequestAborted);
            return;
        }

        await next(http);
    }

    private static bool IsOpen(PathString path)
    {
        var value = path.Value ?? "";
        if (value.Equals("/api/healthz", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/api/auth", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("/.well-known/oauth-protected-resource", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/.well-known/oauth-authorization-server", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/connect/register", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/connect/token", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/connect/revoke", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return value.Equals("/mcp", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/mcp/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HarborCaller?> LoadAsync(HttpContext http, HarborDbContext db, CancellationToken ct)
    {
        if (!http.Request.Cookies.TryGetValue(SessionCookies.Name, out var raw)
            || !Guid.TryParse(raw, out var sessionId))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var session = await db.AppSessions.AsNoTracking().FirstOrDefaultAsync(
            row => row.Id == sessionId && row.RevokedAt == null && row.ExpiresAt > now,
            ct);
        if (session is null)
        {
            return null;
        }

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(row => row.Id == session.EmployeeId, ct);
        if (employee is null)
        {
            return null;
        }

        var passkeys = await db.WebauthnCredentials.CountAsync(row => row.EmployeeId == employee.Id, ct);
        var ready = AccountReady.IsReady(employee.RecoverySavedAt, passkeys);
        return new HarborCaller(session.Id, employee, ready);
    }
}
