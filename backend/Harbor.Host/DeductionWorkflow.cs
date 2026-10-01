using Harbor;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Host;

public sealed record DeductionPreviewRequest
{
    public required Guid EmployeeId { get; init; }
    public required Guid ActorId { get; init; }
    public required string Kind { get; init; }
    public int PerPaycheckCents { get; init; }
    public string? QualifyingEvent { get; init; }
    public required string Channel { get; init; }
    public Guid? ClientId { get; init; }
    public string? RequestId { get; init; }
    public DateOnly AsOf { get; init; }
    public DateTimeOffset Now { get; init; }
}

public sealed record DeductionSubmitRequest
{
    public required Guid EmployeeId { get; init; }
    public required Guid ActorId { get; init; }
    public required Guid QuoteId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Kind { get; init; }
    public int PerPaycheckCents { get; init; }
    public string? QualifyingEvent { get; init; }
    public required string Channel { get; init; }
    public bool Confirm { get; init; }
    public Guid? ClientId { get; init; }
    public string? RequestId { get; init; }
    public DateOnly AsOf { get; init; }
    public DateTimeOffset Now { get; init; }
}

public sealed record DeductionPreview(Guid QuoteId, DateTimeOffset ExpiresAt, PaycheckEstimate Estimate);

public sealed record DeductionCommandResult(Guid ElectionId, ElectionStatus Status, bool Replay);

public sealed class DeductionWorkflow(HarborDbContext db)
{
    public async Task<WorkflowResult<DeductionPreview>> PreviewAsync(
        DeductionPreviewRequest request,
        CancellationToken ct = default)
    {
        if (request.Channel is not ("web" or "mcp"))
        {
            return WorkflowResult<DeductionPreview>.Fail(new WorkflowError("channel", "The channel is not valid."));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var built = await BuildAsync(request.EmployeeId, request.Kind, request.PerPaycheckCents, request.QualifyingEvent, request.AsOf, ct);
        if (built.Error is WorkflowError error)
        {
            return await RollBack<DeductionPreview>(tx, ct, error);
        }

        if (built.Outcome is DeductionOutcome.Failed failed)
        {
            return await RollBack<DeductionPreview>(tx, ct, new WorkflowError(failed.Code, failed.Message));
        }

        var estimate = ((DeductionOutcome.Estimated)built.Outcome!).Estimate;
        var quote = new ActionQuote
        {
            Id = Guid.NewGuid(),
            EmployeeId = request.EmployeeId,
            Kind = QuoteKind.Deduction,
            Payload = QuoteJson.Write(built.Payload),
            ExpiresAt = request.Now.AddMinutes(15),
        };
        db.ActionQuotes.Add(quote);
        await AuditGuc.Apply(
            db,
            new AuditStamp(request.ActorId, request.Channel, request.ClientId, "session", request.RequestId, quote.Id, null),
            ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return WorkflowResult<DeductionPreview>.Ok(new DeductionPreview(quote.Id, quote.ExpiresAt, estimate));
    }

    public async Task<WorkflowResult<DeductionCommandResult>> SubmitAsync(
        DeductionSubmitRequest request,
        CancellationToken ct = default)
    {
        if (request.Channel is not ("web" or "mcp"))
        {
            return WorkflowResult<DeductionCommandResult>.Fail(new WorkflowError("channel", "The channel is not valid."));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return WorkflowResult<DeductionCommandResult>.Fail(new WorkflowError("invalid_key", "The idempotency key is required."));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await AccountGate.RejectIfNotReady(db, request.ActorId, ct) is WorkflowError notReady)
        {
            return await RollBack<DeductionCommandResult>(tx, ct, notReady);
        }

        var parsedKind = ParseKind(request.Kind);
        if (parsedKind is null)
        {
            return await RollBack<DeductionCommandResult>(tx, ct, new WorkflowError(DeductionEstimate.Unavailable, "That deduction type is not available."));
        }

        var kind = parsedKind.Value;

        var existing = await db.DeductionElections
            .FirstOrDefaultAsync(row => row.EmployeeId == request.EmployeeId && row.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null)
        {
            await tx.CommitAsync(ct);
            return WorkflowResult<DeductionCommandResult>.Ok(new DeductionCommandResult(existing.Id, existing.Status, true));
        }

        var quote = await db.ActionQuotes.FirstOrDefaultAsync(row => row.Id == request.QuoteId, ct);
        if (QuoteGate.Reject(quote, request.EmployeeId, QuoteKind.Deduction, request.Now, request.Channel, request.Confirm) is WorkflowError quoteError)
        {
            return await RollBack<DeductionCommandResult>(tx, ct, quoteError);
        }

        var built = await BuildAsync(request.EmployeeId, request.Kind, request.PerPaycheckCents, request.QualifyingEvent, request.AsOf, ct);
        if (built.Error is WorkflowError error)
        {
            return await RollBack<DeductionCommandResult>(tx, ct, error);
        }

        if (built.Outcome is DeductionOutcome.Failed failed)
        {
            return await RollBack<DeductionCommandResult>(tx, ct, new WorkflowError(failed.Code, failed.Message));
        }

        var stored = QuoteJson.Read<DeductionQuotePayload>(quote!.Payload);
        if (stored is null
            || stored.Kind != built.Payload!.Kind
            || stored.PerPaycheckCents != built.Payload.PerPaycheckCents
            || stored.QualifyingEvent != built.Payload.QualifyingEvent)
        {
            return await RollBack<DeductionCommandResult>(tx, ct, new WorkflowError("quote_mismatch", "The quote does not match this request."));
        }

        if (request.Channel == "mcp")
        {
            quote.ConfirmedAt = request.Now;
        }

        var prior = await db.DeductionElections
            .Where(row => row.EmployeeId == request.EmployeeId
                && row.Kind == kind
                && row.Status == ElectionStatus.Active)
            .ToListAsync(ct);
        foreach (var old in prior)
        {
            old.Status = ElectionStatus.Ended;
            old.EndedOn = built.EffectiveOn.AddDays(-1);
        }

        var election = new DeductionElection
        {
            Id = Guid.NewGuid(),
            EmployeeId = request.EmployeeId,
            Kind = kind,
            PerPaycheckCents = request.PerPaycheckCents,
            Status = ElectionStatus.Active,
            EffectiveOn = built.EffectiveOn,
            QualifyingEvent = built.Payload.QualifyingEvent,
            QuoteId = quote.Id,
            IdempotencyKey = request.IdempotencyKey,
        };
        db.DeductionElections.Add(election);
        quote.ConsumedAt = request.Now;
        await AuditGuc.Apply(
            db,
            new AuditStamp(request.ActorId, request.Channel, request.ClientId, "session", request.RequestId, quote.Id, request.IdempotencyKey),
            ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresState(ex) == "23505")
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var replay = await db.DeductionElections.AsNoTracking()
                .FirstOrDefaultAsync(row => row.EmployeeId == request.EmployeeId && row.IdempotencyKey == request.IdempotencyKey, ct);
            if (replay is null)
            {
                throw;
            }

            return WorkflowResult<DeductionCommandResult>.Ok(new DeductionCommandResult(replay.Id, replay.Status, true));
        }

        return WorkflowResult<DeductionCommandResult>.Ok(new DeductionCommandResult(election.Id, election.Status, false));
    }

    private async Task<BuiltDeduction> BuildAsync(
        Guid employeeId,
        string kindText,
        int perPaycheckCents,
        string? qualifyingEvent,
        DateOnly asOf,
        CancellationToken ct)
    {
        var parsedKind = ParseKind(kindText);
        if (parsedKind is null)
        {
            return BuiltDeduction.Fail(new WorkflowError(DeductionEstimate.Unavailable, "That deduction type is not available."));
        }

        var kind = parsedKind.Value;

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(row => row.Id == employeeId, ct);
        if (employee is null)
        {
            return BuiltDeduction.Fail(new WorkflowError("not_found", "The employee is missing."));
        }

        var profile = await db.WithholdingProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.EmployeeId == employeeId, ct);
        if (profile is null || !FederalTaxTable.TryReadW4(profile.W4, out var gross, out var ytd))
        {
            return BuiltDeduction.Fail(new WorkflowError("not_configured", "The withholding profile is missing."));
        }

        var nextPay = await db.PayPeriods.AsNoTracking()
            .Where(row => row.PayDate > asOf)
            .OrderBy(row => row.PayDate)
            .Select(row => (DateOnly?)row.PayDate)
            .FirstOrDefaultAsync(ct);
        if (nextPay is null)
        {
            return BuiltDeduction.Fail(new WorkflowError("no_pay_date", "There is no pay date after this day."));
        }

        var taxYear = nextPay.Value.Year;
        var tax = await db.TaxYearParams.AsNoTracking().FirstOrDefaultAsync(row => row.TaxYear == taxYear, ct);
        if (tax is null)
        {
            return BuiltDeduction.Fail(new WorkflowError("not_configured", "The tax year is not configured."));
        }

        if (!FederalTaxTable.TryRead(tax.FederalBrackets, profile.FilingStatus, out var standard, out var brackets, out var taxError))
        {
            return BuiltDeduction.Fail(new WorkflowError("not_configured", taxError ?? "The tax year is not configured."));
        }

        var state = await db.StateIncomeTaxes.AsNoTracking()
            .FirstOrDefaultAsync(row => row.TaxYear == taxYear && row.StateCode == profile.StateCode, ct);
        if (state is null)
        {
            return BuiltDeduction.Fail(new WorkflowError("not_configured", "The state tax rate is not configured."));
        }

        var coverage = CoverageFor(kind, employee);
        if (coverage is null)
        {
            return BuiltDeduction.Fail(new WorkflowError("hsa_coverage", "HSA coverage is not set."));
        }

        var cap = await db.DeductionCaps.AsNoTracking()
            .FirstOrDefaultAsync(row => row.TaxYear == taxYear && row.Kind == kind && row.Coverage == coverage, ct);
        if (cap is null)
        {
            return BuiltDeduction.Fail(new WorkflowError("not_configured", "The cap for this year is not set."));
        }

        var catchUp = await db.DeductionCaps.AsNoTracking()
            .Where(row => row.TaxYear == taxYear && row.Kind == DeductionKind.Hsa && row.Coverage == "catch_up")
            .Select(row => (int?)row.LimitCents)
            .FirstOrDefaultAsync(ct);
        var payDates = await db.PayPeriods.AsNoTracking().Select(row => row.PayDate).ToListAsync(ct);
        var periodsInYear = payDates.Count(date => date.Year == taxYear);
        var remaining = payDates.Count(date => date.Year == taxYear && date > asOf);
        var postedRows = await db.PayrollPostings.AsNoTracking()
            .Where(row => row.EmployeeId == employeeId && row.Kind == kind)
            .Join(
                db.PayPeriods.AsNoTracking(),
                posting => posting.PayPeriodId,
                period => period.Id,
                (posting, period) => new { posting.AmountCents, period.PayDate })
            .ToListAsync(ct);
        var posted = postedRows.Where(row => row.PayDate.Year == taxYear).Sum(row => row.AmountCents);
        var active = await db.DeductionElections.AsNoTracking().AnyAsync(row =>
            row.EmployeeId == employeeId
            && row.Kind == kind
            && row.Status == ElectionStatus.Active, ct);
        var cleanEvent = string.IsNullOrWhiteSpace(qualifyingEvent) ? null : qualifyingEvent.Trim();
        var outcome = DeductionEstimate.Calculate(new DeductionEstimateInput
        {
            GrossPerPaycheckCents = gross,
            YtdWagesCents = ytd,
            PayPeriodsInYear = periodsInYear,
            StandardDeductionCents = standard,
            Brackets = brackets,
            StateRateBps = state.RateBps,
            ConformsCafeteria = state.ConformsCafeteria,
            SsWageBaseCents = tax.SsWageBaseCents,
            SsRateBps = tax.SsRateBps,
            MedicareRateBps = tax.MedicareRateBps,
            Kind = FederalTaxTable.NormalizeKind(kindText),
            ProposedPerPaycheckCents = perPaycheckCents,
            PeriodsRemaining = remaining,
            PayrollPostingCents = posted,
            CapCents = cap.LimitCents,
            CatchUpCapCents = catchUp ?? 0,
            HdhpEligible = employee.HdhpEligible,
            BornOn = employee.BornOn,
            TaxYear = taxYear,
            ActiveElectionExists = active,
            EffectiveOn = nextPay,
            QualifyingEvent = cleanEvent,
        });
        return new BuiltDeduction(
            outcome,
            nextPay.Value,
            new DeductionQuotePayload
            {
                Kind = FederalTaxTable.NormalizeKind(kindText),
                PerPaycheckCents = perPaycheckCents,
                QualifyingEvent = cleanEvent,
            },
            null);
    }

    private async Task<WorkflowResult<T>> RollBack<T>(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        CancellationToken ct,
        params WorkflowError[] errors)
    {
        await tx.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        return WorkflowResult<T>.Fail(errors);
    }

    private static DeductionKind? ParseKind(string kind)
    {
        return FederalTaxTable.NormalizeKind(kind) switch
        {
            "hsa" => DeductionKind.Hsa,
            "health_fsa" => DeductionKind.HealthFsa,
            "dependent_care_fsa" => DeductionKind.DependentCareFsa,
            _ => null,
        };
    }

    private static string? CoverageFor(DeductionKind kind, Employee employee)
    {
        if (kind != DeductionKind.Hsa)
        {
            return "employee";
        }

        return employee.HsaCoverage switch
        {
            HsaCoverage.Self => "self",
            HsaCoverage.Family => "family",
            _ => null,
        };
    }

    private static string? PostgresState(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException pg)
            {
                return pg.SqlState;
            }
        }

        return null;
    }

    private sealed record BuiltDeduction(
        DeductionOutcome? Outcome,
        DateOnly EffectiveOn,
        DeductionQuotePayload? Payload,
        WorkflowError? Error)
    {
        public static BuiltDeduction Fail(WorkflowError error) => new(null, default, null, error);
    }
}
