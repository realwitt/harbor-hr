using Microsoft.EntityFrameworkCore;

namespace Harbor.Host;

public static class PasskeyRemoval
{
    public static async Task<WorkflowResult<bool>> RemoveAsync(
        HarborDbContext db,
        Guid employeeId,
        Guid credentialId,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var credential = await db.WebauthnCredentials.FirstOrDefaultAsync(
            row => row.Id == credentialId && row.EmployeeId == employeeId,
            ct);
        if (credential is null)
        {
            await tx.RollbackAsync(ct);
            return WorkflowResult<bool>.Fail(new WorkflowError("not_found", "The passkey is missing."));
        }

        var passkeys = await db.WebauthnCredentials.CountAsync(row => row.EmployeeId == employeeId, ct);
        var unused = await db.RecoveryCodes.CountAsync(
            row => row.EmployeeId == employeeId && row.UsedAt == null,
            ct);
        if (PasskeyRules.Block(passkeys, unused) is string blocked)
        {
            await tx.RollbackAsync(ct);
            return WorkflowResult<bool>.Fail(new WorkflowError(blocked, "The last passkey stays."));
        }

        var employee = await db.Employees.FirstAsync(row => row.Id == employeeId, ct);
        WebauthnChallenge? proof = null;
        if (AccountReady.IsReady(employee.RecoverySavedAt, passkeys))
        {
            proof = await StepUpProof.FindAsync(db, employeeId, now, ct, StepUpActions.RemovePasskey);
            if (proof is null)
            {
                await tx.RollbackAsync(ct);
                return WorkflowResult<bool>.Fail(new WorkflowError("step_up_required", "A step-up is required."));
            }
        }

        db.WebauthnCredentials.Remove(credential);
        if (proof is not null)
        {
            StepUpProof.Spend(proof, now);
        }

        await AuditGuc.Apply(
            db,
            new AuditStamp(employeeId, "web", null, "webauthn", "remove-passkey", null, null),
            ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return WorkflowResult<bool>.Ok(true);
    }
}
