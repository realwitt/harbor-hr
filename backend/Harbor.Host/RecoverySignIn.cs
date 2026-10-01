using Microsoft.EntityFrameworkCore;

namespace Harbor.Host;

public static class RecoverySignIn
{
    public static string Normalize(string code) =>
        code.Trim().ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    public static string Hash(string code) => AuthTokens.Sha256Hex(Normalize(code));

    public static async Task<WorkflowResult<Guid>> AssertAsync(
        HarborDbContext db,
        string email,
        string code,
        CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var employee = await db.Employees.FirstOrDefaultAsync(row => row.Email == normalized, ct);
        if (employee is null)
        {
            await tx.RollbackAsync(ct);
            return WorkflowResult<Guid>.Fail(new WorkflowError("invalid_code", "The recovery code is not valid."));
        }

        var hash = Hash(code);
        var match = await db.RecoveryCodes.FirstOrDefaultAsync(
            row => row.EmployeeId == employee.Id && row.CodeHash == hash && row.UsedAt == null,
            ct);
        if (match is null)
        {
            await tx.RollbackAsync(ct);
            return WorkflowResult<Guid>.Fail(new WorkflowError("invalid_code", "The recovery code is not valid."));
        }

        match.UsedAt = now;
        var session = await SessionIssuer.OpenAsync(db, employee.Id, null, now, ct);
        await AuditGuc.Apply(
            db,
            new AuditStamp(employee.Id, "web", null, "recovery_code", "recovery-assert", null, null),
            ct);
        await AuditAccess.Write(db, "recovery_code_used", "ok", employee.Id, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return WorkflowResult<Guid>.Ok(session.Id);
    }
}
