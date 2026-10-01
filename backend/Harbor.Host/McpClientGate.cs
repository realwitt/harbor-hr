using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Harbor.Host;

public static class McpClientGate
{
    public static async Task<string?> RejectReasonAsync(
        HarborDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (!Guid.TryParse(Subject(principal), out var employeeId)
            || !Guid.TryParse(Claim(principal, McpClaims.ClientRow), out var mcpClientId))
        {
            return "The token is missing the employee or the client.";
        }

        var oauthClient = Claim(principal, McpClaims.OAuthClient);
        if (string.IsNullOrEmpty(oauthClient))
        {
            return "The token is missing the client.";
        }

        var row = await db.McpClients.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == mcpClientId, ct);
        if (row is null || row.EmployeeId != employeeId || row.RevokedAt is not null || row.OauthApplicationId is null)
        {
            return "The client is revoked.";
        }

        var enabled = await db.Employees.AsNoTracking()
            .Where(item => item.Id == employeeId)
            .Select(item => item.McpEnabledAt)
            .FirstOrDefaultAsync(ct);
        if (enabled is null)
        {
            return "MCP is off.";
        }

        var applicationClientId = await db.Set<OpenIddictEntityFrameworkCoreApplication>()
            .AsNoTracking()
            .Where(item => item.Id == row.OauthApplicationId)
            .Select(item => item.ClientId)
            .FirstOrDefaultAsync(ct);
        if (!string.Equals(applicationClientId, oauthClient, StringComparison.Ordinal))
        {
            return "The client does not match.";
        }

        return null;
    }

    public static void DetachIdle(HarborDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            var ns = entry.Metadata.ClrType.Namespace ?? "";
            if (entry.State == EntityState.Unchanged && ns.StartsWith("OpenIddict", StringComparison.Ordinal))
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    public static string? Subject(ClaimsPrincipal principal) =>
        principal.GetClaim(Claims.Subject)
        ?? principal.FindFirst(Claims.Subject)?.Value
        ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? principal.FindFirst("sub")?.Value;

    public static string? Claim(ClaimsPrincipal principal, string type) =>
        principal.GetClaim(type) ?? principal.FindFirst(type)?.Value;
}
