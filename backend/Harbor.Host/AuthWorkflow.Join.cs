using System.Net;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Host;

internal sealed record InviteCreated(string Path, Guid InviteId);

public sealed partial class AuthWorkflow
{
    public async Task<AuthResult> RequestToJoin(JoinRequestBody body, CancellationToken ct)
    {
        if (await TurnstileFailure(body.TurnstileToken, ct) is AuthResult denied)
        {
            return denied;
        }

        var email = NormalizeEmail(body.Email);
        var name = body.Name?.Trim();
        var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        if (email is null || string.IsNullOrEmpty(name) || name.Length > 200 || (note?.Length ?? 0) > 500)
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "invalid");
        }

        if (await db.Employees.AnyAsync(row => row.Email == email, ct))
        {
            return AuthResult.Fail(StatusCodes.Status409Conflict, "account_exists");
        }

        if (await db.JoinRequests.AnyAsync(row => row.Email == email && row.Status == "pending", ct))
        {
            return AuthResult.Fail(StatusCodes.Status409Conflict, "request_pending");
        }

        var row = new JoinRequest
        {
            Id = Guid.NewGuid(),
            Email = email,
            Name = name,
            Note = note,
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.JoinRequests.Add(row);
        await AuditGuc.Apply(db, Stamp(null, "system", "join-request", null), ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUnique(ex))
        {
            await tx.RollbackAsync(ct);
            return AuthResult.Fail(StatusCodes.Status409Conflict, "request_pending");
        }

        var sent = await SendJoinNotice(row, ct);
        return AuthResult.Success(new { id = row.Id, mailSent = sent });
    }

    public async Task<AuthResult> ListJoinRequests(HarborCaller? caller, CancellationToken ct)
    {
        if (RequireAdmin(caller) is AuthResult denied)
        {
            return denied;
        }

        var rows = await db.JoinRequests.AsNoTracking()
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => new
            {
                row.Id,
                row.Email,
                row.Name,
                row.Note,
                row.Status,
                row.CreatedAt,
            })
            .ToListAsync(ct);
        return AuthResult.Success(rows);
    }

    public async Task<AuthResult> OpenJoinRequest(HarborCaller? caller, Guid id, CancellationToken ct)
    {
        if (RequireAdmin(caller) is AuthResult denied)
        {
            return denied;
        }

        var row = await db.JoinRequests.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new
            {
                item.Id,
                item.Email,
                item.Name,
                item.Note,
                item.Status,
                item.CreatedAt,
            })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return AuthResult.Fail(StatusCodes.Status404NotFound, "not_found");
        }

        return AuthResult.Success(row);
    }

    public async Task<AuthResult> ApproveJoinRequest(HarborCaller? caller, Guid id, InviteBody body, CancellationToken ct)
    {
        if (RequireAdmin(caller) is AuthResult denied)
        {
            return denied;
        }

        var row = await db.JoinRequests.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (row is null)
        {
            return AuthResult.Fail(StatusCodes.Status404NotFound, "not_found");
        }

        if (row.Status != "pending")
        {
            return AuthResult.Fail(StatusCodes.Status409Conflict, "not_pending");
        }

        var created = await CreateInvite(caller, new InviteBody
        {
            Email = row.Email,
            Name = row.Name,
            Role = body.Role,
            ManagerId = body.ManagerId,
            HiredOn = body.HiredOn,
            Jurisdiction = body.Jurisdiction,
            Timezone = body.Timezone,
        }, ct);
        if (created.Error is not null)
        {
            return created;
        }

        if (created.Body is not InviteCreated made)
        {
            return AuthResult.Fail(StatusCodes.Status500InternalServerError, "invalid");
        }

        row.Status = "approved";
        row.DecidedAt = DateTimeOffset.UtcNow;
        row.DecidedBy = caller!.Employee.Id;
        row.InviteId = made.InviteId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await AuditGuc.Apply(db, Stamp(caller.Employee.Id, "session", "join-approve", null), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var link = Absolute(options.Value.PublicBaseUrl, made.Path);
        var text = $"Your Harbor account is ready.\nOpen this link and create a passkey. The link expires in 7 days.\n{link}";
        var html = $"<p>Your Harbor account is ready.</p><p><a href=\"{Html(link)}\">Create your passkey</a></p><p>The link expires in 7 days.</p>";
        var sent = await mailer.SendAsync(
            new OutboundMail(row.Email, "Your Harbor account is ready", text, html),
            ct);
        return AuthResult.Success(new { path = made.Path, mailSent = sent });
    }

    public async Task<AuthResult> DismissJoinRequest(HarborCaller? caller, Guid id, CancellationToken ct)
    {
        if (RequireAdmin(caller) is AuthResult denied)
        {
            return denied;
        }

        var row = await db.JoinRequests.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (row is null)
        {
            return AuthResult.Fail(StatusCodes.Status404NotFound, "not_found");
        }

        if (row.Status != "pending")
        {
            return AuthResult.Fail(StatusCodes.Status409Conflict, "not_pending");
        }

        row.Status = "dismissed";
        row.DecidedAt = DateTimeOffset.UtcNow;
        row.DecidedBy = caller!.Employee.Id;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await AuditGuc.Apply(db, Stamp(caller.Employee.Id, "session", "join-dismiss", null), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new AuthResult { Status = StatusCodes.Status204NoContent };
    }

    private async Task<bool> SendJoinNotice(JoinRequest row, CancellationToken ct)
    {
        var notify = options.Value.JoinNotifyEmail?.Trim();
        if (string.IsNullOrEmpty(notify))
        {
            logger.LogInformation("Mail is not configured.");
            return false;
        }

        var link = Absolute(options.Value.PublicBaseUrl, $"/admin/join-requests/{row.Id}");
        var note = row.Note ?? "No note.";
        var text = $"{row.Name} ({row.Email}) requested to join Harbor.\nNote: {note}\nOpen this page to set up the account:\n{link}";
        var html = $"<p>{Html(row.Name)} ({Html(row.Email)}) requested to join Harbor.</p><p>Note: {Html(note)}</p><p><a href=\"{Html(link)}\">Set up this account</a></p>";
        return await mailer.SendAsync(new OutboundMail(notify, "Harbor join request", text, html), ct);
    }

    private static AuthResult? RequireAdmin(HarborCaller? caller)
    {
        if (caller is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "sign_in_required");
        }

        if (!caller.Ready || caller.Employee.Role != EmployeeRole.HrAdmin)
        {
            return AuthResult.Fail(StatusCodes.Status403Forbidden, "not_authorized");
        }

        return null;
    }

    private static string Absolute(string baseUrl, string path)
    {
        var root = baseUrl.TrimEnd('/');
        var suffix = path.StartsWith('/') ? path : "/" + path;
        return root + suffix;
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);
}
