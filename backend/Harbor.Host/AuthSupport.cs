using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Harbor.Host;

internal static class AuthLimits
{
    public static readonly TimeSpan Challenge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Session = TimeSpan.FromDays(14);
    public static readonly TimeSpan Invite = TimeSpan.FromDays(7);
    public const int RecoveryCodeCount = 10;
}

internal static class StepUpActions
{
    public const string EnrollPasskey = "enroll_passkey";
    public const string RemovePasskey = "remove_passkey";
    public const string McpOn = "mcp_on";
    public const string RevokeClient = "revoke_client";
    public const string ReadAudit = "read_audit";
    public const string ConfirmQuote = "confirm_quote";
    public const string SaveRecovery = "save_recovery";

    public static bool Known(string? action) => action is
        EnrollPasskey or RemovePasskey or McpOn or RevokeClient or ReadAudit or ConfirmQuote or SaveRecovery;
}

public static class PasskeyRules
{
    // The last passkey stays. Unused recovery codes do not change that.
    public static string? Block(int passkeyCount, int unusedRecoveryCodes)
    {
        if (passkeyCount <= 1 && unusedRecoveryCodes > 0)
        {
            return "last_passkey";
        }

        if (passkeyCount <= 1)
        {
            return "last_passkey";
        }

        return null;
    }
}

internal static class AuthTokens
{
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static byte[] UserHandle(string email) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()));

    public static string[] TransportNames(AuthenticatorTransport[]? transports)
    {
        if (transports is null || transports.Length == 0)
        {
            return [];
        }

        return transports.Select(Name).ToArray();
    }

    public static AuthenticatorTransport[] ParseTransports(string[]? names)
    {
        if (names is null || names.Length == 0)
        {
            return [];
        }

        var list = new List<AuthenticatorTransport>(names.Length);
        foreach (var name in names)
        {
            switch (name)
            {
                case "usb":
                    list.Add(AuthenticatorTransport.Usb);
                    break;
                case "nfc":
                    list.Add(AuthenticatorTransport.Nfc);
                    break;
                case "ble":
                    list.Add(AuthenticatorTransport.Ble);
                    break;
                case "smart-card":
                    list.Add(AuthenticatorTransport.SmartCard);
                    break;
                case "hybrid":
                    list.Add(AuthenticatorTransport.Hybrid);
                    break;
                case "internal":
                    list.Add(AuthenticatorTransport.Internal);
                    break;
            }
        }

        return list.ToArray();
    }

    private static string Name(AuthenticatorTransport transport) => transport switch
    {
        AuthenticatorTransport.Usb => "usb",
        AuthenticatorTransport.Nfc => "nfc",
        AuthenticatorTransport.Ble => "ble",
        AuthenticatorTransport.SmartCard => "smart-card",
        AuthenticatorTransport.Hybrid => "hybrid",
        AuthenticatorTransport.Internal => "internal",
        _ => "internal",
    };
}

internal static class ClientDataChallenge
{
    public static byte[]? Read(byte[]? clientDataJson)
    {
        if (clientDataJson is null || clientDataJson.Length == 0)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(clientDataJson);
            if (!doc.RootElement.TryGetProperty("challenge", out var challenge)
                || challenge.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = challenge.GetString();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            return Base64Url.DecodeFromChars(text);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class AuditAccess
{
    public static Task Write(HarborDbContext db, string action, string outcome, Guid? subjectId, CancellationToken ct) =>
        Write(db, action, outcome, subjectId, null, ct);

    public static Task Write(
        HarborDbContext db,
        string action,
        string outcome,
        Guid? subjectId,
        string? tool,
        CancellationToken ct)
    {
        object[] parameters =
        [
            new NpgsqlParameter("action", action),
            new NpgsqlParameter("outcome", outcome),
            new NpgsqlParameter("subject", NpgsqlDbType.Uuid) { Value = subjectId ?? (object)DBNull.Value },
            new NpgsqlParameter("tool", NpgsqlDbType.Text) { Value = (object?)tool ?? DBNull.Value },
            new NpgsqlParameter("detail", NpgsqlDbType.Jsonb) { Value = DBNull.Value },
        ];
        return db.Database.ExecuteSqlRawAsync(
            "select audit.record_access(@action, @outcome, @subject, @tool, @detail)",
            parameters,
            ct);
    }
}

internal static class AccountGate
{
    public static async Task<WorkflowError?> RejectIfNotReady(HarborDbContext db, Guid actorId, CancellationToken ct)
    {
        var actor = await db.Employees.AsNoTracking()
            .Where(row => row.Id == actorId)
            .Select(row => new { row.RecoverySavedAt })
            .FirstOrDefaultAsync(ct);
        if (actor is null)
        {
            return new WorkflowError("not_found", "The caller is missing.");
        }

        var passkeys = await db.WebauthnCredentials.CountAsync(row => row.EmployeeId == actorId, ct);
        if (!AccountReady.IsReady(actor.RecoverySavedAt, passkeys))
        {
            return new WorkflowError("account_not_ready", "The account is not ready.");
        }

        return null;
    }
}

internal static class SessionIssuer
{
    public static async Task<AppSession> OpenAsync(
        HarborDbContext db,
        Guid employeeId,
        Guid? previousSessionId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (previousSessionId is Guid previous)
        {
            var old = await db.AppSessions.FirstOrDefaultAsync(row => row.Id == previous && row.RevokedAt == null, ct);
            if (old is not null)
            {
                old.RevokedAt = now;
            }
        }

        var session = new AppSession
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            ExpiresAt = now.Add(AuthLimits.Session),
        };
        db.AppSessions.Add(session);
        return session;
    }
}

internal static class StepUpProof
{
    public static async Task<WebauthnChallenge?> FindAsync(
        HarborDbContext db,
        Guid employeeId,
        DateTimeOffset now,
        CancellationToken ct,
        params string[] actions)
    {
        var rows = await db.WebauthnChallenges
            .Where(row => row.EmployeeId == employeeId
                && row.Kind == "step_up"
                && row.ConsumedAt != null
                && row.ExpiresAt > now)
            .ToListAsync(ct);
        return rows
            .Where(row => row.Action is not null && actions.Contains(row.Action, StringComparer.Ordinal))
            .OrderByDescending(row => row.ConsumedAt)
            .FirstOrDefault();
    }

    public static void Spend(WebauthnChallenge challenge, DateTimeOffset now)
    {
        challenge.ExpiresAt = now;
    }
}

internal static class PostgresErrors
{
    public static bool IsUnique(Exception ex) => Is(ex, "23505");

    public static bool IsExclusion(Exception ex) => Is(ex, "23P01");

    public static bool IsCheck(Exception ex) => Is(ex, "23514");

    public static bool IsForeignKey(Exception ex) => Is(ex, "23503");

    public static bool IsNumeric(Exception ex) => Is(ex, "22003");

    private static bool Is(Exception ex, string sqlState)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg && pg.SqlState == sqlState)
            {
                return true;
            }
        }

        return false;
    }
}
