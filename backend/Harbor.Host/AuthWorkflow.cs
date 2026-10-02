using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Fido2NetLib;
using Fido2NetLib.Exceptions;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbor.Host;

public sealed class AuthWorkflow(
    HarborDbContext db,
    IFido2 fido2,
    IOptions<HarborAuthOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<AuthWorkflow> logger)
{
    public async Task<AuthResult> CreateInvite(HarborCaller? caller, InviteBody body, CancellationToken ct)
    {
        if (caller is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "sign_in_required");
        }

        if (!caller.Ready || caller.Employee.Role != EmployeeRole.HrAdmin)
        {
            return AuthResult.Fail(StatusCodes.Status403Forbidden, "not_authorized");
        }

        var email = NormalizeEmail(body.Email);
        var name = body.Name?.Trim();
        var role = ParseRole(body.Role);
        var jurisdiction = body.Jurisdiction?.Trim();
        var timezone = string.IsNullOrWhiteSpace(body.Timezone) ? "America/New_York" : body.Timezone.Trim();
        if (email is null || string.IsNullOrEmpty(name) || role is null || string.IsNullOrEmpty(jurisdiction) || body.HiredOn is null || body.HiredOn == default)
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "invalid");
        }

        if (body.ManagerId is Guid managerId)
        {
            var managerExists = await db.Employees.AnyAsync(row => row.Id == managerId, ct);
            if (!managerExists)
            {
                return AuthResult.Fail(StatusCodes.Status400BadRequest, "manager_missing");
            }
        }

        var token = AuthTokens.NewToken();
        var now = DateTimeOffset.UtcNow;
        var invite = new Invite
        {
            Id = Guid.NewGuid(),
            Email = email,
            Name = name,
            Role = role.Value,
            ManagerId = body.ManagerId,
            HiredOn = body.HiredOn.Value,
            Jurisdiction = jurisdiction,
            Timezone = timezone,
            TokenHash = AuthTokens.Sha256Hex(token),
            ExpiresAt = now.Add(AuthLimits.Invite),
            CreatedBy = caller.Employee.Id,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Invites.Add(invite);
        await AuditGuc.Apply(db, Stamp(caller.Employee.Id, "session", "invite", null), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var path = "/invite/" + token;
        if (string.IsNullOrWhiteSpace(options.Value.MailWorkerUrl))
        {
            logger.LogInformation("Invite path {Path}", path);
        }

        return AuthResult.Success(new { path });
    }

    public async Task<AuthResult> OpenInvite(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthResult.Fail(StatusCodes.Status404NotFound, "invite_invalid");
        }

        var now = DateTimeOffset.UtcNow;
        var hash = AuthTokens.Sha256Hex(token.Trim());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var invite = await db.Invites.FirstOrDefaultAsync(row => row.TokenHash == hash, ct);
        if (invite is null || invite.ConsumedAt is not null || invite.ExpiresAt <= now)
        {
            await tx.RollbackAsync(ct);
            return AuthResult.Fail(StatusCodes.Status404NotFound, "invite_invalid");
        }

        var subject = await db.Employees.AsNoTracking()
            .Where(row => row.Email == invite.Email)
            .Select(row => (Guid?)row.Id)
            .FirstOrDefaultAsync(ct);
        await AuditGuc.Apply(db, Stamp(null, "session", "invite-open", null), ct);
        await AuditAccess.Write(db, "invite_opened", "ok", subject, ct);
        await tx.CommitAsync(ct);
        return AuthResult.Success(new { email = invite.Email, name = invite.Name });
    }

    public async Task<AuthResult> RegisterOptions(string? token, CancellationToken ct)
    {
        var invite = await ValidInvite(token, ct);
        if (invite is null)
        {
            return AuthResult.Fail(StatusCodes.Status404NotFound, "invite_invalid");
        }

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(row => row.Email == invite.Email, ct);
        var exclude = await Descriptors(employee?.Id, ct);
        var created = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = User(invite.Email, invite.Name),
            ExcludeCredentials = exclude,
            AuthenticatorSelection = Selection(),
            AttestationPreference = AttestationConveyancePreference.None,
            Extensions = null!,
            PubKeyCredParams = PubKeyCredParam.Defaults,
        });

        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.WebauthnChallenges.Add(new WebauthnChallenge
        {
            Id = Guid.NewGuid(),
            EmployeeId = employee?.Id,
            InviteId = invite.Id,
            Kind = "register",
            Challenge = created.Challenge,
            ExpiresAt = now.Add(AuthLimits.Challenge),
        });
        await AuditGuc.Apply(db, Stamp(employee?.Id, "session", "register-options", null), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AuthResult.Success(created);
    }

    public async Task<AuthResult> Register(RegisterBody body, Guid? previousSessionId, CancellationToken ct)
    {
        if (!await TurnstileOk(body.TurnstileToken, ct))
        {
            var code = string.IsNullOrWhiteSpace(body.TurnstileToken) ? "turnstile_required" : "turnstile_failed";
            return AuthResult.Fail(StatusCodes.Status400BadRequest, code);
        }

        if (body.Attestation?.Response?.ClientDataJson is null)
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "invalid");
        }

        var presented = ClientDataChallenge.Read(body.Attestation.Response.ClientDataJson);
        if (presented is null)
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "invalid");
        }

        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var invite = await TrackedInvite(body.Token, now, ct);
        if (invite is null)
        {
            await tx.RollbackAsync(ct);
            return AuthResult.Fail(StatusCodes.Status404NotFound, "invite_invalid");
        }

        var rows = await db.WebauthnChallenges
            .Where(row => row.Kind == "register" && row.InviteId == invite.Id && row.ConsumedAt == null && row.ExpiresAt > now)
            .ToListAsync(ct);
        var challenge = Match(rows, presented);
        if (challenge is null)
        {
            await tx.RollbackAsync(ct);
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "challenge_missing");
        }

        var employee = await db.Employees.FirstOrDefaultAsync(row => row.Email == invite.Email, ct);
        var exclude = Descriptors(await db.WebauthnCredentials.AsNoTracking()
            .Where(row => employee != null && row.EmployeeId == employee.Id)
            .ToListAsync(ct));
        RegisteredPublicKeyCredential created;
        try
        {
            created = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = body.Attestation,
                OriginalOptions = CredentialCreateOptions.Create(
                    CeremonyConfig(),
                    challenge.Challenge,
                    User(invite.Email, invite.Name),
                    Selection(),
                    AttestationConveyancePreference.None,
                    exclude,
                    null!,
                    PubKeyCredParam.Defaults),
                IsCredentialIdUniqueToUserCallback = UniqueCredential,
                RequestTokenBindingId = null!,
            }, ct);
        }
        catch (Exception ex) when (ex is Fido2VerificationException or FormatException or CryptographicException)
        {
            logger.LogInformation("Registration failed: {Message}", ex.Message);
            challenge.ConsumedAt = now;
            await AuditGuc.Apply(db, Stamp(employee?.Id, "webauthn", "register", null), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "assertion_failed");
        }

        if (created?.Id is null || created.Id.Length == 0 || created.PublicKey is null || created.PublicKey.Length == 0)
        {
            challenge.ConsumedAt = now;
            await AuditGuc.Apply(db, Stamp(employee?.Id, "webauthn", "register", null), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "assertion_failed");
        }

        try
        {
            if (employee is null)
            {
                employee = new Employee
                {
                    Id = Guid.NewGuid(),
                    Email = invite.Email,
                    Name = invite.Name,
                    Role = invite.Role,
                    Timezone = invite.Timezone,
                    Jurisdiction = invite.Jurisdiction,
                    HiredOn = invite.HiredOn,
                };
                db.Employees.Add(employee);
                if (invite.ManagerId is Guid managerId && await db.Employees.AnyAsync(row => row.Id == managerId, ct))
                {
                    db.ManagerLinks.Add(new ManagerLink
                    {
                        EmployeeId = employee.Id,
                        ManagerId = managerId,
                        EffectiveOn = invite.HiredOn,
                    });
                }
            }

            db.WebauthnCredentials.Add(Credential(employee.Id, created));
            invite.ConsumedAt = now;
            challenge.ConsumedAt = now;
            var passkeys = await db.WebauthnCredentials.CountAsync(row => row.EmployeeId == employee.Id, ct) + 1;
            var ready = AccountReady.IsReady(employee.RecoverySavedAt, passkeys);
            var session = await SessionIssuer.OpenAsync(db, employee.Id, previousSessionId, now, ct);
            await AuditGuc.Apply(db, Stamp(employee.Id, "webauthn", "register", null), ct);
            await AuditAccess.Write(db, "sign_in_ok", "ok", employee.Id, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AuthResult.Success(new { employeeId = employee.Id, ready }, session.Id, session.ExpiresAt);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUnique(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return AuthResult.Fail(StatusCodes.Status409Conflict, "conflict");
        }
    }

    public async Task<AuthResult> AssertOptions(string? email, CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null)
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "invalid");
        }

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(row => row.Email == normalized, ct);
        if (employee is null)
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "unknown_account");
        }

        var allowed = await Descriptors(employee.Id, ct);
        if (allowed.Count == 0)
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "no_passkey");
        }

        var created = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowed,
            UserVerification = UserVerificationRequirement.Required,
            Extensions = null!,
        });
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.WebauthnChallenges.Add(new WebauthnChallenge
        {
            Id = Guid.NewGuid(),
            EmployeeId = employee.Id,
            Kind = "assert",
            Challenge = created.Challenge,
            ExpiresAt = now.Add(AuthLimits.Challenge),
        });
        await AuditGuc.Apply(db, Stamp(employee.Id, "session", "assert-options", null), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AuthResult.Success(created);
    }

    public async Task<AuthResult> Assert(AssertBody body, Guid? previousSessionId, CancellationToken ct)
    {
        var email = NormalizeEmail(body.Email);
        if (email is null || body.Assertion?.Response?.ClientDataJson is null)
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "invalid");
        }

        var presented = ClientDataChallenge.Read(body.Assertion.Response.ClientDataJson);
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var employee = await db.Employees.FirstOrDefaultAsync(row => row.Email == email, ct);
        if (employee is null || presented is null)
        {
            return await DenyAssert(tx, employee?.Id, null, now, ct);
        }

        var rows = await db.WebauthnChallenges
            .Where(row => row.Kind == "assert" && row.EmployeeId == employee.Id && row.ConsumedAt == null && row.ExpiresAt > now)
            .ToListAsync(ct);
        var challenge = Match(rows, presented);
        if (challenge is null)
        {
            return await DenyAssert(tx, employee.Id, null, now, ct);
        }

        var credentials = await db.WebauthnCredentials.Where(row => row.EmployeeId == employee.Id).ToListAsync(ct);
        var credential = credentials.FirstOrDefault(row => row.CredentialId.AsSpan().SequenceEqual(body.Assertion.RawId));
        if (credential is null)
        {
            return await DenyAssert(tx, employee.Id, challenge, now, ct);
        }

        VerifyAssertionResult verified;
        try
        {
            verified = await Verify(body.Assertion, challenge.Challenge, credentials, credential, employee.Email, ct);
        }
        catch (Exception ex) when (ex is Fido2VerificationException or FormatException or CryptographicException)
        {
            logger.LogInformation("Assertion failed: {Message}", ex.Message);
            return await DenyAssert(tx, employee.Id, challenge, now, ct);
        }

        var decision = SignCountRule.Decide(credential.SignCount, verified.SignCount);
        if (!decision.Accept)
        {
            return await DenyAssert(tx, employee.Id, challenge, now, ct);
        }

        credential.SignCount = decision.Next;
        challenge.ConsumedAt = now;
        var passkeys = credentials.Count;
        var ready = AccountReady.IsReady(employee.RecoverySavedAt, passkeys);
        var session = await SessionIssuer.OpenAsync(db, employee.Id, previousSessionId, now, ct);
        await AuditGuc.Apply(db, Stamp(employee.Id, "webauthn", "assert", null), ct);
        await AuditAccess.Write(db, "sign_in_ok", "ok", employee.Id, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AuthResult.Success(new { employeeId = employee.Id, ready }, session.Id, session.ExpiresAt);
    }

    public async Task<AuthResult> GenerateRecovery(HarborCaller? caller, CancellationToken ct)
    {
        if (caller is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "sign_in_required");
        }

        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var employee = await db.Employees.FirstAsync(row => row.Id == caller.Employee.Id, ct);
        var passkeys = await db.WebauthnCredentials.CountAsync(row => row.EmployeeId == employee.Id, ct);
        var ready = AccountReady.IsReady(employee.RecoverySavedAt, passkeys);
        WebauthnChallenge? proof = null;
        if (ready)
        {
            proof = await StepUpProof.FindAsync(db, employee.Id, now, ct, StepUpActions.EnrollPasskey, StepUpActions.SaveRecovery);
            if (proof is null)
            {
                await tx.RollbackAsync(ct);
                return AuthResult.Fail(StatusCodes.Status403Forbidden, "step_up_required");
            }
        }

        var unused = await db.RecoveryCodes.Where(row => row.EmployeeId == employee.Id && row.UsedAt == null).ToListAsync(ct);
        db.RecoveryCodes.RemoveRange(unused);
        var codes = NewCodes();
        foreach (var code in codes)
        {
            db.RecoveryCodes.Add(new RecoveryCode
            {
                Id = Guid.NewGuid(),
                EmployeeId = employee.Id,
                CodeHash = RecoverySignIn.Hash(code),
            });
        }

        if (proof is not null)
        {
            StepUpProof.Spend(proof, now);
        }

        await AuditGuc.Apply(db, Stamp(employee.Id, "session", "recovery-generate", null), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AuthResult.Success(new { codes });
    }

    public async Task<AuthResult> AcknowledgeRecovery(HarborCaller? caller, CancellationToken ct)
    {
        if (caller is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "sign_in_required");
        }

        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var employee = await db.Employees.FirstAsync(row => row.Id == caller.Employee.Id, ct);
        var unused = await db.RecoveryCodes.CountAsync(row => row.EmployeeId == employee.Id && row.UsedAt == null, ct);
        if (unused != AuthLimits.RecoveryCodeCount)
        {
            await tx.RollbackAsync(ct);
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "recovery_not_saved");
        }

        employee.RecoverySavedAt = now;
        var session = await SessionIssuer.OpenAsync(db, employee.Id, caller.SessionId, now, ct);
        await AuditGuc.Apply(db, Stamp(employee.Id, "session", "recovery-ack", null), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AuthResult.Success(new { ready = true }, session.Id, session.ExpiresAt);
    }

    public Task<AuthResult> RecoveryAssert(string? email, string? code, Guid? previousSessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
        {
            return Task.FromResult(AuthResult.Fail(StatusCodes.Status400BadRequest, "invalid"));
        }

        return RecoveryAssertCore(email, code, previousSessionId, ct);
    }

    public async Task<AuthResult> ListPasskeys(HarborCaller? caller, CancellationToken ct)
    {
        if (caller is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "sign_in_required");
        }

        var rows = await db.WebauthnCredentials.AsNoTracking()
            .Where(row => row.EmployeeId == caller.Employee.Id)
            .OrderBy(row => row.CreatedAt)
            .Select(row => new { id = row.Id, createdAt = row.CreatedAt, nickname = row.Nickname })
            .ToListAsync(ct);
        return AuthResult.Success(rows);
    }

    public async Task<AuthResult> PasskeyOptions(HarborCaller? caller, CancellationToken ct)
    {
        if (caller is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "sign_in_required");
        }

        var now = DateTimeOffset.UtcNow;
        var passkeys = await db.WebauthnCredentials.CountAsync(row => row.EmployeeId == caller.Employee.Id, ct);
        if (AccountReady.IsReady(caller.Employee.RecoverySavedAt, passkeys))
        {
            var proof = await StepUpProof.FindAsync(db, caller.Employee.Id, now, ct, StepUpActions.EnrollPasskey);
            if (proof is null)
            {
                return AuthResult.Fail(StatusCodes.Status403Forbidden, "step_up_required");
            }
        }

        var exclude = await Descriptors(caller.Employee.Id, ct);
        var created = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = User(caller.Employee.Email, caller.Employee.Name),
            ExcludeCredentials = exclude,
            AuthenticatorSelection = Selection(),
            AttestationPreference = AttestationConveyancePreference.None,
            Extensions = null!,
            PubKeyCredParams = PubKeyCredParam.Defaults,
        });
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.WebauthnChallenges.Add(new WebauthnChallenge
        {
            Id = Guid.NewGuid(),
            EmployeeId = caller.Employee.Id,
            Kind = "register",
            Challenge = created.Challenge,
            ExpiresAt = now.Add(AuthLimits.Challenge),
        });
        await AuditGuc.Apply(db, Stamp(caller.Employee.Id, "session", "passkey-options", null), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AuthResult.Success(created);
    }

    public async Task<AuthResult> AddPasskey(HarborCaller? caller, AuthenticatorAttestationRawResponse? attestation, CancellationToken ct)
    {
        if (caller is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "sign_in_required");
        }

        if (attestation?.Response?.ClientDataJson is null)
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "invalid");
        }

        var presented = ClientDataChallenge.Read(attestation.Response.ClientDataJson);
        if (presented is null)
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "invalid");
        }

        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var employee = await db.Employees.FirstAsync(row => row.Id == caller.Employee.Id, ct);
        var existing = await db.WebauthnCredentials.Where(row => row.EmployeeId == employee.Id).ToListAsync(ct);
        var wasReady = AccountReady.IsReady(employee.RecoverySavedAt, existing.Count);
        WebauthnChallenge? proof = null;
        if (wasReady)
        {
            proof = await StepUpProof.FindAsync(db, employee.Id, now, ct, StepUpActions.EnrollPasskey);
            if (proof is null)
            {
                await tx.RollbackAsync(ct);
                return AuthResult.Fail(StatusCodes.Status403Forbidden, "step_up_required");
            }
        }

        var rows = await db.WebauthnChallenges
            .Where(row => row.Kind == "register"
                && row.EmployeeId == employee.Id
                && row.InviteId == null
                && row.ConsumedAt == null
                && row.ExpiresAt > now)
            .ToListAsync(ct);
        var challenge = Match(rows, presented);
        if (challenge is null)
        {
            await tx.RollbackAsync(ct);
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "challenge_missing");
        }

        RegisteredPublicKeyCredential created;
        try
        {
            created = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestation,
                OriginalOptions = CredentialCreateOptions.Create(
                    CeremonyConfig(),
                    challenge.Challenge,
                    User(employee.Email, employee.Name),
                    Selection(),
                    AttestationConveyancePreference.None,
                    Descriptors(existing),
                    null!,
                    PubKeyCredParam.Defaults),
                IsCredentialIdUniqueToUserCallback = UniqueCredential,
                RequestTokenBindingId = null!,
            }, ct);
        }
        catch (Exception ex) when (ex is Fido2VerificationException or FormatException or CryptographicException)
        {
            logger.LogInformation("Passkey enrollment failed: {Message}", ex.Message);
            challenge.ConsumedAt = now;
            await AuditGuc.Apply(db, Stamp(employee.Id, "webauthn", "passkey", null), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "assertion_failed");
        }

        if (created?.Id is null || created.Id.Length == 0 || created.PublicKey is null || created.PublicKey.Length == 0)
        {
            challenge.ConsumedAt = now;
            await AuditGuc.Apply(db, Stamp(employee.Id, "webauthn", "passkey", null), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "assertion_failed");
        }

        try
        {
            db.WebauthnCredentials.Add(Credential(employee.Id, created));
            challenge.ConsumedAt = now;
            if (proof is not null)
            {
                StepUpProof.Spend(proof, now);
            }

            var ready = AccountReady.IsReady(employee.RecoverySavedAt, existing.Count + 1);
            Guid? sessionId = null;
            DateTimeOffset? expires = null;
            if (!wasReady && ready)
            {
                var session = await SessionIssuer.OpenAsync(db, employee.Id, caller.SessionId, now, ct);
                sessionId = session.Id;
                expires = session.ExpiresAt;
            }

            await AuditGuc.Apply(db, Stamp(employee.Id, "webauthn", "passkey", null), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AuthResult.Success(new { ready }, sessionId, expires);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUnique(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return AuthResult.Fail(StatusCodes.Status409Conflict, "conflict");
        }
    }

    public async Task<AuthResult> RemovePasskey(HarborCaller? caller, Guid credentialId, CancellationToken ct)
    {
        if (caller is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "sign_in_required");
        }

        var result = await PasskeyRemoval.RemoveAsync(db, caller.Employee.Id, credentialId, ct);
        if (result.Succeeded)
        {
            return AuthResult.Success(new { ok = true });
        }

        var error = result.Errors[0];
        var status = error.Code switch
        {
            "last_passkey" => StatusCodes.Status409Conflict,
            "step_up_required" => StatusCodes.Status403Forbidden,
            "not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };
        return AuthResult.Fail(status, error.Code);
    }

    public async Task<AuthResult> StepUpOptions(HarborCaller? caller, string? action, Guid? quoteId, CancellationToken ct)
    {
        if (caller is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "sign_in_required");
        }

        if (!StepUpActions.Known(action))
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "invalid_action");
        }

        var now = DateTimeOffset.UtcNow;
        if (action == StepUpActions.ConfirmQuote)
        {
            if (quoteId is null)
            {
                return AuthResult.Fail(StatusCodes.Status400BadRequest, "quote_required");
            }

            var quote = await db.ActionQuotes.AsNoTracking().FirstOrDefaultAsync(row => row.Id == quoteId, ct);
            if (quote is null || quote.EmployeeId != caller.Employee.Id || quote.ConsumedAt is not null || quote.ExpiresAt <= now)
            {
                return AuthResult.Fail(StatusCodes.Status400BadRequest, "quote_invalid");
            }
        }

        var allowed = await Descriptors(caller.Employee.Id, ct);
        if (allowed.Count == 0)
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "no_passkey");
        }

        var created = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowed,
            UserVerification = UserVerificationRequirement.Required,
            Extensions = null!,
        });
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.WebauthnChallenges.Add(new WebauthnChallenge
        {
            Id = Guid.NewGuid(),
            EmployeeId = caller.Employee.Id,
            Kind = "step_up",
            Action = action,
            QuoteId = action == StepUpActions.ConfirmQuote ? quoteId : null,
            Challenge = created.Challenge,
            ExpiresAt = now.Add(AuthLimits.Challenge),
        });
        await AuditGuc.Apply(db, Stamp(caller.Employee.Id, "session", "step-up-options", quoteId), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AuthResult.Success(created);
    }

    public async Task<AuthResult> StepUp(HarborCaller? caller, AuthenticatorAssertionRawResponse? assertion, CancellationToken ct)
    {
        if (caller is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "sign_in_required");
        }

        if (assertion?.Response?.ClientDataJson is null)
        {
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "invalid");
        }

        var presented = ClientDataChallenge.Read(assertion.Response.ClientDataJson);
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var employee = await db.Employees.FirstAsync(row => row.Id == caller.Employee.Id, ct);
        if (presented is null)
        {
            return await DenyStepUp(tx, employee.Id, null, null, now, ct);
        }

        var rows = await db.WebauthnChallenges
            .Where(row => row.Kind == "step_up" && row.EmployeeId == employee.Id && row.ConsumedAt == null && row.ExpiresAt > now)
            .ToListAsync(ct);
        var challenge = Match(rows, presented);
        if (challenge is null || !StepUpActions.Known(challenge.Action))
        {
            return await DenyStepUp(tx, employee.Id, challenge, challenge?.QuoteId, now, ct);
        }

        var credentials = await db.WebauthnCredentials.Where(row => row.EmployeeId == employee.Id).ToListAsync(ct);
        var credential = credentials.FirstOrDefault(row => row.CredentialId.AsSpan().SequenceEqual(assertion.RawId));
        if (credential is null)
        {
            return await DenyStepUp(tx, employee.Id, challenge, challenge.QuoteId, now, ct);
        }

        VerifyAssertionResult verified;
        try
        {
            verified = await Verify(assertion, challenge.Challenge, credentials, credential, employee.Email, ct);
        }
        catch (Exception ex) when (ex is Fido2VerificationException or FormatException or CryptographicException)
        {
            logger.LogInformation("Step-up failed: {Message}", ex.Message);
            return await DenyStepUp(tx, employee.Id, challenge, challenge.QuoteId, now, ct);
        }

        var decision = SignCountRule.Decide(credential.SignCount, verified.SignCount);
        if (!decision.Accept)
        {
            return await DenyStepUp(tx, employee.Id, challenge, challenge.QuoteId, now, ct);
        }

        if (challenge.Action == StepUpActions.ConfirmQuote)
        {
            var quote = challenge.QuoteId is Guid quoteId
                ? await db.ActionQuotes.FirstOrDefaultAsync(row => row.Id == quoteId, ct)
                : null;
            if (quote is null || quote.EmployeeId != employee.Id || quote.ConsumedAt is not null || quote.ExpiresAt <= now)
            {
                return await DenyStepUp(tx, employee.Id, challenge, challenge.QuoteId, now, ct);
            }

            quote.ConfirmedAt ??= now;
        }

        credential.SignCount = decision.Next;
        challenge.ConsumedAt = now;
        challenge.ExpiresAt = now.Add(AuthLimits.Challenge);
        await AuditGuc.Apply(db, Stamp(employee.Id, "webauthn", "step-up", challenge.QuoteId), ct);
        await AuditAccess.Write(db, "step_up_ok", "ok", employee.Id, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AuthResult.Success(new { ok = true });
    }

    public async Task<AuthResult> SignOut(HarborCaller? caller, CancellationToken ct)
    {
        if (caller is not null)
        {
            var now = DateTimeOffset.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var session = await db.AppSessions.FirstOrDefaultAsync(row => row.Id == caller.SessionId && row.RevokedAt == null, ct);
            if (session is not null)
            {
                session.RevokedAt = now;
                await AuditGuc.Apply(db, Stamp(caller.Employee.Id, "session", "sign-out", null), ct);
                await db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
        }

        return AuthResult.SignedOut();
    }

    public async Task<AuthResult> Me(HarborCaller? caller, CancellationToken ct)
    {
        if (caller is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "sign_in_required");
        }

        var employee = await db.Employees.AsNoTracking().FirstAsync(row => row.Id == caller.Employee.Id, ct);
        var passkeys = await db.WebauthnCredentials.CountAsync(row => row.EmployeeId == employee.Id, ct);
        var unused = await db.RecoveryCodes.CountAsync(row => row.EmployeeId == employee.Id && row.UsedAt == null, ct);
        return AuthResult.Success(new
        {
            employeeId = employee.Id,
            email = employee.Email,
            name = employee.Name,
            role = employee.Role == EmployeeRole.HrAdmin ? "hr_admin" : "employee",
            ready = AccountReady.IsReady(employee.RecoverySavedAt, passkeys),
            passkeyCount = passkeys,
            unusedRecoveryCodeCount = unused,
            mcpEnabledAt = employee.McpEnabledAt,
        });
    }

    public async Task<string?> CreateBootstrapInviteAsync(CancellationToken ct)
    {
        var elias = await db.Employees.FirstOrDefaultAsync(row => row.Email == "ew@eliaswitt.com", ct);
        if (elias is null)
        {
            return null;
        }

        var hasPasskey = await db.WebauthnCredentials.AnyAsync(row => row.EmployeeId == elias.Id, ct);
        if (hasPasskey)
        {
            return null;
        }

        var token = AuthTokens.NewToken();
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Invites.Add(new Invite
        {
            Id = Guid.NewGuid(),
            Email = elias.Email,
            Name = elias.Name,
            Role = elias.Role,
            ManagerId = null,
            HiredOn = elias.HiredOn,
            Jurisdiction = elias.Jurisdiction,
            Timezone = elias.Timezone,
            TokenHash = AuthTokens.Sha256Hex(token),
            ExpiresAt = now.Add(AuthLimits.Invite),
            CreatedBy = null,
        });
        await AuditGuc.Apply(db, new AuditStamp(null, "system", null, "system", "bootstrap", null, null), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return "/invite/" + token;
    }

    private async Task<AuthResult> RecoveryAssertCore(string email, string code, Guid? previousSessionId, CancellationToken ct)
    {
        var result = await RecoverySignIn.AssertAsync(db, email, code, ct);
        if (!result.Succeeded || result.Value == Guid.Empty)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "invalid_code");
        }

        if (previousSessionId is Guid previous && previous != result.Value)
        {
            var now = DateTimeOffset.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var old = await db.AppSessions.FirstOrDefaultAsync(row => row.Id == previous && row.RevokedAt == null, ct);
            if (old is not null)
            {
                old.RevokedAt = now;
                await AuditGuc.Apply(db, Stamp(old.EmployeeId, "recovery_code", "recovery-assert", null), ct);
                await db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
        }

        var session = await db.AppSessions.AsNoTracking().FirstAsync(row => row.Id == result.Value, ct);
        var passkeys = await db.WebauthnCredentials.CountAsync(row => row.EmployeeId == session.EmployeeId, ct);
        var employee = await db.Employees.AsNoTracking().FirstAsync(row => row.Id == session.EmployeeId, ct);
        var ready = AccountReady.IsReady(employee.RecoverySavedAt, passkeys);
        return AuthResult.Success(new { employeeId = employee.Id, ready }, session.Id, session.ExpiresAt);
    }

    private async Task<AuthResult> DenyAssert(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        Guid? actorId,
        WebauthnChallenge? challenge,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (challenge is not null)
        {
            challenge.ConsumedAt = now;
            challenge.ExpiresAt = now;
        }

        await AuditGuc.Apply(db, Stamp(actorId, "webauthn", "assert", null), ct);
        await AuditAccess.Write(db, "sign_in_fail", "denied", actorId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AuthResult.Fail(StatusCodes.Status401Unauthorized, "assertion_failed");
    }

    private async Task<AuthResult> DenyStepUp(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        Guid actorId,
        WebauthnChallenge? challenge,
        Guid? quoteId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (challenge is not null)
        {
            challenge.ConsumedAt = now;
            challenge.ExpiresAt = now;
        }

        await AuditGuc.Apply(db, Stamp(actorId, "webauthn", "step-up", quoteId), ct);
        await AuditAccess.Write(db, "step_up_fail", "denied", actorId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AuthResult.Fail(StatusCodes.Status401Unauthorized, "assertion_failed");
    }

    private async Task<VerifyAssertionResult> Verify(
        AuthenticatorAssertionRawResponse assertion,
        byte[] challenge,
        List<WebauthnCredential> credentials,
        WebauthnCredential credential,
        string email,
        CancellationToken ct)
    {
        var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = assertion,
            OriginalOptions = AssertionOptions.Create(
                CeremonyConfig(),
                challenge,
                Descriptors(credentials),
                UserVerificationRequirement.Required,
                null!),
            StoredPublicKey = credential.PublicKey,
            // Fido2 rejects a new count of 0 when the stored count is above 0.
            // Harbor accepts 0 and stores 0. The library still checks the signature and user verification.
            StoredSignatureCounter = 0,
            IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
            {
                if (args.UserHandle is null || args.UserHandle.Length == 0)
                {
                    return Task.FromResult(true);
                }

                return Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(AuthTokens.UserHandle(email)));
            },
            RequestTokenBindingId = null!,
        }, ct);
        return result;
    }

    private async Task<bool> UniqueCredential(IsCredentialIdUniqueToUserParams args, CancellationToken ct)
    {
        var credentialId = args.CredentialId;
        var taken = await db.WebauthnCredentials.AnyAsync(row => row.CredentialId == credentialId, ct);
        return !taken;
    }

    private async Task<bool> TurnstileOk(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.TurnstileSecret))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var client = httpClientFactory.CreateClient("turnstile");
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"] = options.Value.TurnstileSecret,
                ["response"] = token,
            });
            using var response = await client.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify", body, ct);
            var payload = await response.Content.ReadFromJsonAsync<TurnstileReply>(ct);
            return payload?.Success == true;
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("Turnstile check failed: {Message}", ex.Message);
            return false;
        }
        catch (TaskCanceledException ex)
        {
            logger.LogInformation("Turnstile check failed: {Message}", ex.Message);
            return false;
        }
    }

    private async Task<Invite?> ValidInvite(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var hash = AuthTokens.Sha256Hex(token.Trim());
        return await db.Invites.AsNoTracking().FirstOrDefaultAsync(
            row => row.TokenHash == hash && row.ConsumedAt == null && row.ExpiresAt > now,
            ct);
    }

    private async Task<Invite?> TrackedInvite(string? token, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = AuthTokens.Sha256Hex(token.Trim());
        return await db.Invites.FirstOrDefaultAsync(
            row => row.TokenHash == hash && row.ConsumedAt == null && row.ExpiresAt > now,
            ct);
    }

    private async Task<IReadOnlyList<PublicKeyCredentialDescriptor>> Descriptors(Guid? employeeId, CancellationToken ct)
    {
        if (employeeId is null)
        {
            return [];
        }

        var rows = await db.WebauthnCredentials.AsNoTracking().Where(row => row.EmployeeId == employeeId).ToListAsync(ct);
        return Descriptors(rows);
    }

    private Fido2Configuration CeremonyConfig() => new()
    {
        RPID = options.Value.RelyingPartyId,
        RPName = options.Value.ServerName,
        Origins = options.Value.Origins.ToHashSet(StringComparer.Ordinal),
        TimestampDriftTolerance = 300_000,
    };

    private static IReadOnlyList<PublicKeyCredentialDescriptor> Descriptors(IEnumerable<WebauthnCredential> rows) =>
        rows.Select(row => new PublicKeyCredentialDescriptor(
            PublicKeyCredentialType.PublicKey,
            row.CredentialId,
            AuthTokens.ParseTransports(row.Transports))).ToArray();

    private static WebauthnCredential Credential(Guid employeeId, RegisteredPublicKeyCredential created) => new()
    {
        Id = Guid.NewGuid(),
        EmployeeId = employeeId,
        CredentialId = created.Id,
        PublicKey = created.PublicKey,
        SignCount = created.SignCount,
        Aaguid = created.AaGuid,
        Transports = AuthTokens.TransportNames(created.Transports),
    };

    private static Fido2User User(string email, string name) => new()
    {
        Name = email,
        Id = AuthTokens.UserHandle(email),
        DisplayName = name,
    };

    private static AuthenticatorSelection Selection() => new()
    {
        UserVerification = UserVerificationRequirement.Required,
        ResidentKey = ResidentKeyRequirement.Required,
    };

    private static WebauthnChallenge? Match(IEnumerable<WebauthnChallenge> rows, byte[] presented) =>
        rows.FirstOrDefault(row => row.Challenge.AsSpan().SequenceEqual(presented));

    private static AuditStamp Stamp(Guid? actorId, string factor, string requestId, Guid? quoteId) =>
        new(actorId, "web", null, factor, requestId, quoteId, null);

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var normalized = email.Trim().ToLowerInvariant();
        return normalized.Contains('@') ? normalized : null;
    }

    private static EmployeeRole? ParseRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "employee" => EmployeeRole.Employee,
        "hr_admin" => EmployeeRole.HrAdmin,
        _ => null,
    };

    private static string[] NewCodes()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var codes = new string[AuthLimits.RecoveryCodeCount];
        Span<char> raw = stackalloc char[8];
        for (var i = 0; i < codes.Length; i++)
        {
            for (var n = 0; n < raw.Length; n++)
            {
                raw[n] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }

            codes[i] = new string(raw[..4]) + "-" + new string(raw[4..]);
        }

        return codes;
    }

    private sealed class TurnstileReply
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }
    }
}
