using Harbor;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Harbor.Host;

public sealed record LeavePreviewRequest
{
    public required Guid EmployeeId { get; init; }
    public required Guid ActorId { get; init; }
    public required Guid LeaveTypeId { get; init; }
    public string? Start { get; init; }
    public string? End { get; init; }
    public decimal? HoursPerDay { get; init; }
    public bool AdminOverride { get; init; }
    public required string Channel { get; init; }
    public Guid? ClientId { get; init; }
    public string? RequestId { get; init; }
    public DateOnly AsOf { get; init; }
    public DateTimeOffset Now { get; init; }
}

public sealed record LeaveSubmitRequest
{
    public required Guid EmployeeId { get; init; }
    public required Guid ActorId { get; init; }
    public required Guid QuoteId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required Guid LeaveTypeId { get; init; }
    public string? Start { get; init; }
    public string? End { get; init; }
    public decimal? HoursPerDay { get; init; }
    public bool AdminOverride { get; init; }
    public required string Channel { get; init; }
    public bool Confirm { get; init; }
    public Guid? ClientId { get; init; }
    public string? RequestId { get; init; }
    public DateOnly AsOf { get; init; }
    public DateTimeOffset Now { get; init; }
}

public sealed record LeaveCancelPreviewRequest
{
    public required Guid EmployeeId { get; init; }
    public required Guid ActorId { get; init; }
    public required Guid RequestId { get; init; }
    public required string Channel { get; init; }
    public Guid? ClientId { get; init; }
    public string? TraceId { get; init; }
    public DateTimeOffset Now { get; init; }
}

public sealed record LeaveCancelRequest
{
    public required Guid EmployeeId { get; init; }
    public required Guid ActorId { get; init; }
    public required Guid QuoteId { get; init; }
    public required Guid RequestId { get; init; }
    public required string Channel { get; init; }
    public bool Confirm { get; init; }
    public Guid? ClientId { get; init; }
    public string? TraceId { get; init; }
    public DateTimeOffset Now { get; init; }
}

public sealed record LeaveDecisionRequest
{
    public required Guid ActorId { get; init; }
    public required Guid RequestId { get; init; }
    public required string Channel { get; init; }
    public Guid? ClientId { get; init; }
    public string? TraceId { get; init; }
    public DateOnly AsOf { get; init; }
    public DateTimeOffset Now { get; init; }
}

public sealed record LeavePreview(
    Guid QuoteId,
    DateTimeOffset ExpiresAt,
    LeaveProjectionResult? Projection,
    IReadOnlyList<LeaveWarning> Warnings);

public sealed record LeaveCommandResult(Guid RequestId, LeaveStatus Status, bool Replay);

public sealed class LeaveWorkflow(HarborDbContext db)
{
    public async Task<WorkflowResult<LeavePreview>> PreviewAsync(LeavePreviewRequest request, CancellationToken ct = default)
    {
        if (ChannelError(request.Channel) is WorkflowError channel)
        {
            return WorkflowResult<LeavePreview>.Fail(channel);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var employee = await db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.EmployeeId, ct);
        var actor = await db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.ActorId, ct);
        var type = await db.LeaveTypes.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.LeaveTypeId, ct);
        if (employee is null || actor is null || type is null)
        {
            return await RollBack<LeavePreview>(tx, ct, new WorkflowError("not_found", "The employee or leave type is missing."));
        }

        var evaluated = await EvaluateAsync(employee, actor, type, request, ct);
        var quote = new ActionQuote
        {
            Id = Guid.NewGuid(),
            EmployeeId = employee.Id,
            Kind = QuoteKind.Leave,
            Payload = QuoteJson.Write(new LeaveQuotePayload
            {
                Action = "submit",
                LeaveTypeId = type.Id,
                Start = request.Start,
                End = request.End,
                HoursPerDay = request.HoursPerDay ?? 8m,
                AdminOverride = request.AdminOverride,
            }),
            ExpiresAt = request.Now.AddMinutes(15),
        };
        db.ActionQuotes.Add(quote);
        await AuditGuc.Apply(db, Stamp(request.ActorId, request.Channel, request.ClientId, request.RequestId, quote.Id, null), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return WorkflowResult<LeavePreview>.Ok(new LeavePreview(
            quote.Id,
            quote.ExpiresAt,
            evaluated.Projection,
            evaluated.Policy.Warnings));
    }

    public async Task<WorkflowResult<LeaveCommandResult>> SubmitAsync(LeaveSubmitRequest request, CancellationToken ct = default)
    {
        if (ChannelError(request.Channel) is WorkflowError channel)
        {
            return WorkflowResult<LeaveCommandResult>.Fail(channel);
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return WorkflowResult<LeaveCommandResult>.Fail(new WorkflowError("invalid_key", "The idempotency key is required."));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await AccountGate.RejectIfNotReady(db, request.ActorId, ct) is WorkflowError notReady)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, notReady);
        }

        var existing = await db.LeaveRequests
            .FirstOrDefaultAsync(row => row.EmployeeId == request.EmployeeId && row.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null)
        {
            await tx.CommitAsync(ct);
            return WorkflowResult<LeaveCommandResult>.Ok(new LeaveCommandResult(existing.Id, existing.Status, true));
        }

        var quote = await db.ActionQuotes.FirstOrDefaultAsync(row => row.Id == request.QuoteId, ct);
        if (QuoteGate.Reject(quote, request.EmployeeId, QuoteKind.Leave, request.Now, request.Channel, request.Confirm) is WorkflowError quoteError)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, quoteError);
        }

        if (!SameSubmit(quote!.Payload, request))
        {
            return await RollBack<LeaveCommandResult>(tx, ct, new WorkflowError("quote_mismatch", "The quote does not match this request."));
        }

        if (request.Channel == "mcp")
        {
            quote.ConfirmedAt = request.Now;
        }

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(row => row.Id == request.EmployeeId, ct);
        var actor = await db.Employees.AsNoTracking().FirstOrDefaultAsync(row => row.Id == request.ActorId, ct);
        var type = await db.LeaveTypes.AsNoTracking().FirstOrDefaultAsync(row => row.Id == request.LeaveTypeId, ct);
        if (employee is null || actor is null || type is null)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, new WorkflowError("not_found", "The employee or leave type is missing."));
        }

        var evaluated = await EvaluateAsync(employee, actor, type, request, ct);
        if (evaluated.Policy.Warnings.Count > 0)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, evaluated.Policy.Warnings
                .Select(warning => new WorkflowError(warning.Code, warning.Message))
                .ToArray());
        }

        var leave = new LeaveRequest
        {
            Id = Guid.NewGuid(),
            EmployeeId = employee.Id,
            LeaveTypeId = type.Id,
            Status = LeaveStatus.Pending,
            IdempotencyKey = request.IdempotencyKey,
            QuoteId = quote.Id,
            AdminOverride = request.Channel != "mcp" && request.AdminOverride && actor.Role == EmployeeRole.HrAdmin,
        };
        db.LeaveRequests.Add(leave);
        quote.ConsumedAt = request.Now;
        await AuditGuc.Apply(db, Stamp(request.ActorId, request.Channel, request.ClientId, request.RequestId, quote.Id, request.IdempotencyKey), ct);
        try
        {
            await db.SaveChangesAsync(ct);
            foreach (var day in evaluated.Policy.Days)
            {
                db.LeaveRequestDays.Add(new LeaveRequestDay
                {
                    RequestId = leave.Id,
                    OnDate = day,
                    Hours = evaluated.Policy.HoursPerDay,
                    EmployeeId = employee.Id,
                    Status = LeaveStatus.Pending,
                });
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsPostgres(ex, "23P01"))
        {
            return await RollBack<LeaveCommandResult>(tx, ct, new WorkflowError(LeaveRequestPolicy.Overlap, "This request overlaps an open leave day."));
        }
        catch (DbUpdateException ex) when (IsPostgres(ex, "23505"))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var replay = await db.LeaveRequests.AsNoTracking()
                .FirstOrDefaultAsync(row => row.EmployeeId == request.EmployeeId && row.IdempotencyKey == request.IdempotencyKey, ct);
            if (replay is null)
            {
                throw;
            }

            return WorkflowResult<LeaveCommandResult>.Ok(new LeaveCommandResult(replay.Id, replay.Status, true));
        }

        return WorkflowResult<LeaveCommandResult>.Ok(new LeaveCommandResult(leave.Id, leave.Status, false));
    }

    public async Task<WorkflowResult<LeavePreview>> PreviewCancelAsync(LeaveCancelPreviewRequest request, CancellationToken ct = default)
    {
        if (ChannelError(request.Channel) is WorkflowError channel)
        {
            return WorkflowResult<LeavePreview>.Fail(channel);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var leave = await db.LeaveRequests.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.RequestId && row.EmployeeId == request.EmployeeId, ct);
        if (leave is null)
        {
            return await RollBack<LeavePreview>(tx, ct, new WorkflowError("not_found", "The request is missing."));
        }

        var quote = new ActionQuote
        {
            Id = Guid.NewGuid(),
            EmployeeId = request.EmployeeId,
            Kind = QuoteKind.Leave,
            Payload = QuoteJson.Write(new LeaveQuotePayload
            {
                Action = "cancel",
                RequestId = leave.Id,
            }),
            ExpiresAt = request.Now.AddMinutes(15),
        };
        db.ActionQuotes.Add(quote);
        await AuditGuc.Apply(db, Stamp(request.ActorId, request.Channel, request.ClientId, request.TraceId, quote.Id, null), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return WorkflowResult<LeavePreview>.Ok(new LeavePreview(quote.Id, quote.ExpiresAt, null, []));
    }

    public async Task<WorkflowResult<LeaveCommandResult>> CancelAsync(LeaveCancelRequest request, CancellationToken ct = default)
    {
        if (ChannelError(request.Channel) is WorkflowError channel)
        {
            return WorkflowResult<LeaveCommandResult>.Fail(channel);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await AccountGate.RejectIfNotReady(db, request.ActorId, ct) is WorkflowError notReady)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, notReady);
        }

        var quote = await db.ActionQuotes.FirstOrDefaultAsync(row => row.Id == request.QuoteId, ct);
        if (QuoteGate.Reject(quote, request.EmployeeId, QuoteKind.Leave, request.Now, request.Channel, request.Confirm) is WorkflowError quoteError)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, quoteError);
        }

        var stored = QuoteJson.Read<LeaveQuotePayload>(quote!.Payload);
        if (stored is null || stored.Action != "cancel" || stored.RequestId != request.RequestId)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, new WorkflowError("quote_mismatch", "The quote does not match this request."));
        }

        var leave = await db.LeaveRequests.FirstOrDefaultAsync(row => row.Id == request.RequestId, ct);
        if (leave is null || leave.EmployeeId != request.ActorId)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, new WorkflowError("not_owner", "You can cancel only your own pending request."));
        }

        if (leave.Status != LeaveStatus.Pending)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, new WorkflowError("not_pending", "The request is not pending."));
        }

        if (request.Channel == "mcp")
        {
            quote.ConfirmedAt = request.Now;
        }

        leave.Status = LeaveStatus.Cancelled;
        quote.ConsumedAt = request.Now;
        await AuditGuc.Apply(db, Stamp(request.ActorId, request.Channel, request.ClientId, request.TraceId, quote.Id, null), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return WorkflowResult<LeaveCommandResult>.Ok(new LeaveCommandResult(leave.Id, leave.Status, false));
    }

    public Task<WorkflowResult<LeaveCommandResult>> ApproveAsync(LeaveDecisionRequest request, CancellationToken ct = default)
    {
        return DecideAsync(request, approve: true, ct);
    }

    public Task<WorkflowResult<LeaveCommandResult>> DenyAsync(LeaveDecisionRequest request, CancellationToken ct = default)
    {
        return DecideAsync(request, approve: false, ct);
    }

    private async Task<WorkflowResult<LeaveCommandResult>> DecideAsync(LeaveDecisionRequest request, bool approve, CancellationToken ct)
    {
        if (request.Channel == "mcp")
        {
            return WorkflowResult<LeaveCommandResult>.Fail(new WorkflowError("not_mcp", "Approval is not an MCP operation."));
        }

        if (request.Channel != "web")
        {
            return WorkflowResult<LeaveCommandResult>.Fail(new WorkflowError("channel", "The channel is not valid."));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var leave = await db.LeaveRequests.FirstOrDefaultAsync(row => row.Id == request.RequestId, ct);
        if (leave is null)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, new WorkflowError("not_found", "The request is missing."));
        }

        if (leave.Status != LeaveStatus.Pending)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, new WorkflowError("not_pending", "The request is not pending."));
        }

        var actor = await db.Employees.AsNoTracking().FirstOrDefaultAsync(row => row.Id == request.ActorId, ct);
        if (actor is null)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, new WorkflowError("not_found", "The caller is missing."));
        }

        var isManager = await db.ManagerLinks.AsNoTracking().AnyAsync(link =>
            link.EmployeeId == leave.EmployeeId
            && link.ManagerId == request.ActorId
            && link.EndedOn == null
            && link.EffectiveOn <= request.AsOf, ct);
        if (actor.Role != EmployeeRole.HrAdmin && !isManager)
        {
            return await RollBack<LeaveCommandResult>(tx, ct, new WorkflowError("not_authorized", "You cannot decide this request."));
        }

        leave.Status = approve ? LeaveStatus.Approved : LeaveStatus.Denied;
        leave.DecidedAt = request.Now;
        leave.DecidedBy = request.ActorId;
        if (approve)
        {
            var days = await db.LeaveRequestDays
                .Where(day => day.RequestId == leave.Id)
                .ToListAsync(ct);
            foreach (var day in days)
            {
                db.LeaveLedgers.Add(new LeaveLedger
                {
                    Id = Guid.NewGuid(),
                    EmployeeId = leave.EmployeeId,
                    LeaveTypeId = leave.LeaveTypeId,
                    Kind = LedgerKind.Usage,
                    Hours = -day.Hours,
                    EffectiveOn = day.OnDate,
                    RequestId = leave.Id,
                    Source = "approval",
                    ExternalRef = $"usage:{leave.Id}:{day.OnDate:yyyy-MM-dd}",
                });
            }
        }

        await AuditGuc.Apply(db, Stamp(request.ActorId, request.Channel, request.ClientId, request.TraceId, leave.QuoteId, leave.IdempotencyKey), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return WorkflowResult<LeaveCommandResult>.Ok(new LeaveCommandResult(leave.Id, leave.Status, false));
    }

    private Task<EvaluatedLeave> EvaluateAsync(
        Employee employee,
        Employee actor,
        LeaveType type,
        LeavePreviewRequest request,
        CancellationToken ct)
    {
        return EvaluateCoreAsync(
            employee,
            actor,
            type,
            request.Channel,
            request.AdminOverride,
            request.Start,
            request.End,
            request.HoursPerDay,
            request.AsOf,
            ct);
    }

    private Task<EvaluatedLeave> EvaluateAsync(
        Employee employee,
        Employee actor,
        LeaveType type,
        LeaveSubmitRequest request,
        CancellationToken ct)
    {
        return EvaluateCoreAsync(
            employee,
            actor,
            type,
            request.Channel,
            request.AdminOverride,
            request.Start,
            request.End,
            request.HoursPerDay,
            request.AsOf,
            ct);
    }

    private async Task<EvaluatedLeave> EvaluateCoreAsync(
        Employee employee,
        Employee actor,
        LeaveType type,
        string channel,
        bool adminOverride,
        string? start,
        string? end,
        decimal? hoursPerDay,
        DateOnly asOf,
        CancellationToken ct)
    {
        LeaveProjectionResult? projection = null;
        if (LeaveRequestPolicy.TryParseIsoDate(start, out var startDate))
        {
            var ledgerRows = await db.LeaveLedgers.AsNoTracking()
                .Where(row => row.EmployeeId == employee.Id && row.LeaveTypeId == type.Id)
                .Select(row => new { row.EffectiveOn, row.Hours, row.Kind, row.Source, row.ExternalRef })
                .ToListAsync(ct);
            var ledger = ledgerRows
                .Select(row => new LedgerSlice(row.EffectiveOn, row.Hours, row.Kind, row.Source, row.ExternalRef))
                .ToList();
            var payDates = await db.PayPeriods.AsNoTracking().Select(row => row.PayDate).ToListAsync(ct);
            var pendingRows = await (
                from day in db.LeaveRequestDays.AsNoTracking()
                join leave in db.LeaveRequests.AsNoTracking() on day.RequestId equals leave.Id
                where leave.EmployeeId == employee.Id
                    && leave.LeaveTypeId == type.Id
                    && leave.Status == LeaveStatus.Pending
                select new { day.OnDate, day.Hours }
            ).ToListAsync(ct);
            var pending = pendingRows.Select(row => new PendingDay(row.OnDate, row.Hours)).ToList();
            projection = LeaveProjection.Project(
                new LeaveGrantPolicy
                {
                    Model = type.Model,
                    HoursPerGrant = type.HoursPerGrant,
                    YearBoundary = type.YearBoundary,
                    CarryCapHours = type.CarryCapHours,
                    MaxBalanceHours = type.MaxBalanceHours,
                    WaitingDays = type.WaitingDays,
                },
                employee.HiredOn,
                asOf,
                startDate,
                ledger,
                payDates,
                approvedFutureAlreadyInLedger: true,
                pending);
        }

        var openDays = await (
            from day in db.LeaveRequestDays.AsNoTracking()
            join leave in db.LeaveRequests.AsNoTracking() on day.RequestId equals leave.Id
            where leave.EmployeeId == employee.Id
                && (leave.Status == LeaveStatus.Pending || leave.Status == LeaveStatus.Approved)
            select day.OnDate
        ).ToListAsync(ct);
        var blackouts = await db.BlackoutDates.AsNoTracking().Select(day => day.OnDate).ToListAsync(ct);
        var policy = LeaveRequestPolicy.Evaluate(
            type.Model,
            employee.TerminatedOn,
            projection?.AvailableHours,
            projection?.HasBalance ?? false,
            actor.Role,
            channel == "mcp" ? false : adminOverride,
            start,
            end,
            hoursPerDay,
            openDays,
            blackouts);
        return new EvaluatedLeave(projection, policy);
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

    private static bool SameSubmit(string payload, LeaveSubmitRequest request)
    {
        var stored = QuoteJson.Read<LeaveQuotePayload>(payload);
        if (stored is null || stored.Action != "submit")
        {
            return false;
        }

        return stored.LeaveTypeId == request.LeaveTypeId
            && stored.Start == request.Start
            && stored.End == request.End
            && stored.HoursPerDay == (request.HoursPerDay ?? 8m)
            && stored.AdminOverride == request.AdminOverride;
    }

    private static AuditStamp Stamp(
        Guid actorId,
        string channel,
        Guid? clientId,
        string? requestId,
        Guid? quoteId,
        string? idempotencyKey)
    {
        return new AuditStamp(actorId, channel, clientId, "session", requestId, quoteId, idempotencyKey);
    }

    private static WorkflowError? ChannelError(string channel)
    {
        if (channel is "web" or "mcp")
        {
            return null;
        }

        return new WorkflowError("channel", "The channel is not valid.");
    }

    private static bool IsPostgres(DbUpdateException ex, string sqlState)
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

    private sealed record EvaluatedLeave(LeaveProjectionResult? Projection, LeavePolicyResult Policy);
}

internal static class QuoteGate
{
    public static WorkflowError? Reject(
        ActionQuote? quote,
        Guid employeeId,
        QuoteKind kind,
        DateTimeOffset now,
        string channel,
        bool confirm)
    {
        if (quote is null)
        {
            return new WorkflowError("quote_missing", "The quote is missing.");
        }

        if (quote.EmployeeId != employeeId)
        {
            return new WorkflowError("quote_employee", "The quote is for another employee.");
        }

        if (quote.Kind != kind)
        {
            return new WorkflowError("quote_mismatch", "The quote does not match this request.");
        }

        if (quote.ConsumedAt is not null)
        {
            return new WorkflowError("quote_consumed", "The quote is already used.");
        }

        if (quote.ExpiresAt <= now)
        {
            return new WorkflowError("quote_expired", "The quote is expired.");
        }

        if (channel == "web")
        {
            if (quote.ConfirmedAt is null)
            {
                return new WorkflowError("quote_unconfirmed", "The quote is not confirmed.");
            }

            return null;
        }

        if (channel == "mcp")
        {
            if (!confirm)
            {
                return new WorkflowError("confirm_required", "Confirm is required.");
            }

            return null;
        }

        return new WorkflowError("channel", "The channel is not valid.");
    }
}
