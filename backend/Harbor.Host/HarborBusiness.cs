using System.Text.Json;
using System.Text.Json.Nodes;
using Harbor;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Harbor.Host;

public sealed record LeaveTypeResponse(
    Guid Id,
    string Code,
    GrantModel Model,
    decimal? HoursPerGrant,
    string YearBoundary,
    decimal? CarryCapHours,
    decimal? MaxBalanceHours,
    int WaitingDays);

public sealed record LeaveBalanceResponse(
    Guid LeaveTypeId,
    string Code,
    GrantModel Model,
    bool HasBalance,
    decimal? AvailableHours,
    decimal? BookedHours,
    IReadOnlyList<ProjectionEvent> Events);

public sealed record LeaveDayResponse(DateOnly On, decimal Hours);

public sealed record LeaveRequestResponse(
    Guid Id,
    Guid LeaveTypeId,
    LeaveStatus Status,
    string IdempotencyKey,
    Guid QuoteId,
    bool AdminOverride,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    Guid? DecidedBy,
    IReadOnlyList<LeaveDayResponse> Days);

public sealed record QueueItemResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    Guid LeaveTypeId,
    LeaveStatus Status,
    DateTimeOffset CreatedAt,
    IReadOnlyList<LeaveDayResponse> Days);

public sealed record CalendarDayResponse(
    Guid EmployeeId,
    string Name,
    Guid RequestId,
    Guid LeaveTypeId,
    LeaveStatus Status,
    DateOnly On,
    decimal Hours);

public sealed class HarborBusiness(HarborDbContext db)
{
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "code_hash",
        "token_hash",
        "challenge",
        "client_secret",
        "json_web_key_set",
        "payload",
    };

    private static readonly JsonSerializerOptions W4Json = new();

    public async Task<BusinessResult> LeaveTypes(CancellationToken ct)
    {
        var rows = await db.LeaveTypes.AsNoTracking().OrderBy(row => row.Code).ToListAsync(ct);
        return BusinessResult.Ok(rows.Select(ToType).ToList());
    }

    public async Task<BusinessResult> Balances(Employee employee, DateOnly asOf, DateOnly on, CancellationToken ct)
    {
        var types = await db.LeaveTypes.AsNoTracking().OrderBy(row => row.Code).ToListAsync(ct);
        var payDates = await db.PayPeriods.AsNoTracking().Select(row => row.PayDate).ToListAsync(ct);
        var rows = new List<LeaveBalanceResponse>(types.Count);
        foreach (var type in types)
        {
            var ledgerRows = await db.LeaveLedgers.AsNoTracking()
                .Where(row => row.EmployeeId == employee.Id && row.LeaveTypeId == type.Id)
                .Select(row => new { row.EffectiveOn, row.Hours, row.Kind, row.Source, row.ExternalRef })
                .ToListAsync(ct);
            var ledger = ledgerRows
                .Select(row => new LedgerSlice(row.EffectiveOn, row.Hours, row.Kind, row.Source, row.ExternalRef))
                .ToList();
            var pendingRows = await (
                from day in db.LeaveRequestDays.AsNoTracking()
                join leave in db.LeaveRequests.AsNoTracking() on day.RequestId equals leave.Id
                where leave.EmployeeId == employee.Id
                    && leave.LeaveTypeId == type.Id
                    && leave.Status == LeaveStatus.Pending
                select new { day.OnDate, day.Hours }
            ).ToListAsync(ct);
            var pending = pendingRows.Select(row => new PendingDay(row.OnDate, row.Hours)).ToList();
            var projection = LeaveProjection.Project(
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
                on,
                ledger,
                payDates,
                approvedFutureAlreadyInLedger: true,
                pending);
            rows.Add(new LeaveBalanceResponse(
                type.Id,
                type.Code,
                type.Model,
                projection.HasBalance,
                projection.AvailableHours,
                projection.BookedHours,
                projection.Events));
        }

        return BusinessResult.Ok(rows);
    }

    public async Task<BusinessResult> OwnRequests(Guid employeeId, CancellationToken ct)
    {
        var requests = await db.LeaveRequests.AsNoTracking()
            .Where(row => row.EmployeeId == employeeId)
            .OrderByDescending(row => row.CreatedAt)
            .ToListAsync(ct);
        var days = await DaysFor(requests.Select(row => row.Id).ToList(), ct);
        var rows = requests.Select(row => new LeaveRequestResponse(
            row.Id,
            row.LeaveTypeId,
            row.Status,
            row.IdempotencyKey,
            row.QuoteId,
            row.AdminOverride,
            row.CreatedAt,
            row.DecidedAt,
            row.DecidedBy,
            Days(days, row.Id))).ToList();
        return BusinessResult.Ok(rows);
    }

    public async Task<BusinessResult> TeamQueue(Guid managerId, DateOnly asOf, CancellationToken ct)
    {
        var reportIds = await OpenReportIds(managerId, asOf, ct);
        var requests = await db.LeaveRequests.AsNoTracking()
            .Where(row => reportIds.Contains(row.EmployeeId) && row.Status == LeaveStatus.Pending)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(ct);
        var employeeIds = requests.Select(row => row.EmployeeId).Distinct().ToList();
        var names = await db.Employees.AsNoTracking()
            .Where(row => employeeIds.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, row => row.Name, ct);
        var days = await DaysFor(requests.Select(row => row.Id).ToList(), ct);
        var rows = requests.Select(row => new QueueItemResponse(
            row.Id,
            row.EmployeeId,
            names[row.EmployeeId],
            row.LeaveTypeId,
            row.Status,
            row.CreatedAt,
            Days(days, row.Id))).ToList();
        return BusinessResult.Ok(rows);
    }

    public async Task<BusinessResult> TeamCalendar(Guid callerId, DateOnly asOf, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var people = await OpenReportIds(callerId, asOf, ct);
        people.Add(callerId);
        var rangeStart = from;
        var rangeEnd = to;
        var rows = await (
            from day in db.LeaveRequestDays.AsNoTracking()
            join leave in db.LeaveRequests.AsNoTracking() on day.RequestId equals leave.Id
            join person in db.Employees.AsNoTracking() on leave.EmployeeId equals person.Id
            where people.Contains(leave.EmployeeId)
                && (leave.Status == LeaveStatus.Pending || leave.Status == LeaveStatus.Approved)
                && day.OnDate >= rangeStart
                && day.OnDate <= rangeEnd
            orderby day.OnDate, person.Name
            select new
            {
                person.Id,
                person.Name,
                RequestId = leave.Id,
                leave.LeaveTypeId,
                leave.Status,
                day.OnDate,
                day.Hours,
            }
        ).ToListAsync(ct);
        return BusinessResult.Ok(rows.Select(row => new CalendarDayResponse(
            row.Id,
            row.Name,
            row.RequestId,
            row.LeaveTypeId,
            row.Status,
            row.OnDate,
            row.Hours)).ToList());
    }

    public async Task<BusinessResult> Deductions(Guid employeeId, int taxYear, CancellationToken ct)
    {
        var elections = await db.DeductionElections.AsNoTracking()
            .Where(row => row.EmployeeId == employeeId)
            .OrderByDescending(row => row.CreatedAt)
            .ToListAsync(ct);
        var caps = await db.DeductionCaps.AsNoTracking()
            .Where(row => row.TaxYear == taxYear)
            .OrderBy(row => row.Kind)
            .ThenBy(row => row.Coverage)
            .ToListAsync(ct);
        return BusinessResult.Ok(new
        {
            elections = elections.Select(row => new
            {
                id = row.Id,
                kind = row.Kind,
                perPaycheckCents = row.PerPaycheckCents,
                status = row.Status,
                effectiveOn = row.EffectiveOn,
                endedOn = row.EndedOn,
                qualifyingEvent = row.QualifyingEvent,
            }),
            caps = caps.Select(row => new
            {
                taxYear = row.TaxYear,
                kind = row.Kind,
                coverage = row.Coverage,
                limitCents = row.LimitCents,
            }),
        });
    }

    public async Task<BusinessResult> GetWithholding(Guid employeeId, CancellationToken ct)
    {
        var profile = await db.WithholdingProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.EmployeeId == employeeId, ct);
        if (profile is null)
        {
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "not_found", "The withholding profile is missing.");
        }

        return BusinessResult.Ok(WithholdingView(profile));
    }

    public async Task<BusinessResult> PutWithholding(Guid employeeId, string? traceId, WithholdingBody body, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var profile = await db.WithholdingProfiles.FirstOrDefaultAsync(row => row.EmployeeId == employeeId, ct);
        if (profile is null)
        {
            var employee = await db.Employees.AsNoTracking().FirstAsync(row => row.Id == employeeId, ct);
            profile = new WithholdingProfile
            {
                EmployeeId = employeeId,
                FilingStatus = body.FilingStatus!,
                StateCode = StateCode(employee),
                W4 = WriteW4(body),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.WithholdingProfiles.Add(profile);
        }
        else
        {
            profile.FilingStatus = body.FilingStatus!;
            profile.W4 = WriteW4(body);
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await Stamp(employeeId, traceId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return BusinessResult.Ok(WithholdingView(profile));
    }

    public async Task<BusinessResult> CreateLeaveType(Guid actorId, string? traceId, LeaveTypeBody body, CancellationToken ct)
    {
        var model = ParseModel(body.Model);
        if (model is null)
        {
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "invalid", "The grant model is not valid.");
        }

        var type = new LeaveType
        {
            Id = Guid.NewGuid(),
            Code = body.Code!.Trim(),
            Model = model.Value,
            HoursPerGrant = body.HoursPerGrant,
            YearBoundary = string.IsNullOrWhiteSpace(body.YearBoundary) ? "calendar" : body.YearBoundary.Trim(),
            CarryCapHours = body.CarryCapHours,
            MaxBalanceHours = body.MaxBalanceHours,
            WaitingDays = body.WaitingDays ?? 0,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.LeaveTypes.Add(type);
        await Stamp(actorId, traceId, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUnique(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "duplicate", "That leave type code already exists.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsCheck(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "invalid", "The leave type values are not valid.");
        }

        return BusinessResult.Ok(ToType(type));
    }

    public async Task<BusinessResult> Blackouts(CancellationToken ct)
    {
        var rows = await db.BlackoutDates.AsNoTracking().OrderBy(row => row.OnDate).ToListAsync(ct);
        return BusinessResult.Ok(rows.Select(row => new { on = row.OnDate, reason = row.Reason }).ToList());
    }

    public async Task<BusinessResult> AddBlackout(Guid actorId, string? traceId, DateOnly on, string reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.BlackoutDates.Add(new BlackoutDate { OnDate = on, Reason = reason.Trim() });
        await Stamp(actorId, traceId, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUnique(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "duplicate", "That date is already a blackout.");
        }

        return BusinessResult.Ok(new { on, reason = reason.Trim() });
    }

    public async Task<BusinessResult> DeleteBlackout(Guid actorId, string? traceId, DateOnly on, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.BlackoutDates.FirstOrDefaultAsync(item => item.OnDate == on, ct);
        if (row is null)
        {
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "not_found", "The blackout date is missing.");
        }

        db.BlackoutDates.Remove(row);
        await Stamp(actorId, traceId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return BusinessResult.Ok(new { on });
    }

    public async Task<BusinessResult> DeductionCaps(CancellationToken ct)
    {
        var rows = await db.DeductionCaps.AsNoTracking()
            .OrderBy(row => row.TaxYear)
            .ThenBy(row => row.Kind)
            .ThenBy(row => row.Coverage)
            .ToListAsync(ct);
        return BusinessResult.Ok(rows.Select(row => new
        {
            taxYear = row.TaxYear,
            kind = row.Kind,
            coverage = row.Coverage,
            limitCents = row.LimitCents,
        }).ToList());
    }

    public async Task<BusinessResult> AddDeductionCap(Guid actorId, string? traceId, DeductionCapBody body, CancellationToken ct)
    {
        var kind = ParseKind(body.Kind);
        if (kind is null)
        {
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "invalid", "The deduction type is not valid.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.DeductionCaps.Add(new DeductionCap
        {
            TaxYear = body.TaxYear!.Value,
            Kind = kind.Value,
            Coverage = body.Coverage!.Trim(),
            LimitCents = body.LimitCents!.Value,
        });
        await Stamp(actorId, traceId, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUnique(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "duplicate", "That cap already exists.");
        }

        return BusinessResult.Ok(new
        {
            taxYear = body.TaxYear,
            kind = kind.Value,
            coverage = body.Coverage!.Trim(),
            limitCents = body.LimitCents,
        });
    }

    public async Task<BusinessResult> SetHdhp(
        Guid actorId,
        string? traceId,
        Guid employeeId,
        bool eligible,
        HsaCoverage? coverage,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var employee = await db.Employees.FirstOrDefaultAsync(row => row.Id == employeeId, ct);
        if (employee is null)
        {
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "not_found", "The employee is missing.");
        }

        employee.HdhpEligible = eligible;
        employee.HsaCoverage = eligible ? coverage : null;
        await Stamp(actorId, traceId, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsCheck(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "invalid", "The HDHP values are not valid.");
        }

        return BusinessResult.Ok(new
        {
            id = employee.Id,
            hdhpEligible = employee.HdhpEligible,
            hsaCoverage = employee.HsaCoverage,
        });
    }

    public async Task<BusinessResult> AddAdjustment(
        Guid actorId,
        Guid employeeId,
        Guid leaveTypeId,
        decimal hours,
        DateOnly effectiveOn,
        string note,
        CancellationToken ct)
    {
        var row = new LeaveLedger
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            LeaveTypeId = leaveTypeId,
            Kind = LedgerKind.Adjustment,
            Hours = hours,
            EffectiveOn = effectiveOn,
            Source = "admin",
            ExternalRef = null,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.LeaveLedgers.Add(row);
        // leave_ledger has no note column. The note is the audit request id.
        await Stamp(actorId, note.Trim(), ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsForeignKey(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "not_found", "The employee or leave type is missing.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsNumeric(ex) || PostgresErrors.IsCheck(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "invalid", "The hours are not valid.");
        }

        return BusinessResult.Ok(new
        {
            id = row.Id,
            employeeId = row.EmployeeId,
            leaveTypeId = row.LeaveTypeId,
            kind = row.Kind,
            hours = row.Hours,
            effectiveOn = row.EffectiveOn,
            source = row.Source,
        });
    }

    public async Task<BusinessResult> PayPeriods(CancellationToken ct)
    {
        var rows = await db.PayPeriods.AsNoTracking().OrderBy(row => row.StartsOn).ToListAsync(ct);
        return BusinessResult.Ok(rows.Select(row => new
        {
            id = row.Id,
            startsOn = row.StartsOn,
            endsOn = row.EndsOn,
            payDate = row.PayDate,
        }).ToList());
    }

    public async Task<BusinessResult> AddPayPeriod(Guid actorId, string? traceId, DateOnly startsOn, DateOnly endsOn, DateOnly payDate, CancellationToken ct)
    {
        if (endsOn < startsOn)
        {
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "invalid", "The end date is before the start date.");
        }

        var row = new PayPeriod
        {
            Id = Guid.NewGuid(),
            StartsOn = startsOn,
            EndsOn = endsOn,
            PayDate = payDate,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.PayPeriods.Add(row);
        await Stamp(actorId, traceId, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsExclusion(ex) || PostgresErrors.IsUnique(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "overlap", "This pay period overlaps another period.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsCheck(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "invalid", "The pay period dates are not valid.");
        }

        return BusinessResult.Ok(new { id = row.Id, startsOn, endsOn, payDate });
    }

    public async Task<BusinessResult> Holidays(CancellationToken ct)
    {
        var rows = await db.CompanyHolidays.AsNoTracking().OrderBy(row => row.OnDate).ToListAsync(ct);
        return BusinessResult.Ok(rows.Select(row => new { on = row.OnDate, name = row.Name }).ToList());
    }

    public async Task<BusinessResult> AddHoliday(Guid actorId, string? traceId, DateOnly on, string name, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.CompanyHolidays.Add(new CompanyHoliday { OnDate = on, Name = name.Trim() });
        await Stamp(actorId, traceId, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUnique(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return BusinessResult.Fail(StatusCodes.Status400BadRequest, "duplicate", "That date is already a holiday.");
        }

        return BusinessResult.Ok(new { on, name = name.Trim() });
    }

    public async Task<BusinessResult> ReadAudit(Guid actorId, string? traceId, AuditQuery query, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-5);
        var proved = await db.WebauthnChallenges.AsNoTracking().AnyAsync(row =>
            row.EmployeeId == actorId
            && row.Kind == "step_up"
            && row.Action == StepUpActions.ReadAudit
            && row.ConsumedAt != null
            && row.ConsumedAt >= cutoff
            && row.ExpiresAt > now, ct);
        if (!proved)
        {
            return BusinessResult.Fail(StatusCodes.Status403Forbidden, "step_up_required", "A step-up is required.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Stamp(actorId, traceId, ct);
        await AuditAccess.Write(db, "read_audit", "ok", actorId, ct);
        var records = await ReadRecords(query, ct);
        var access = await ReadAccess(query, ct);
        await tx.CommitAsync(ct);
        return BusinessResult.Ok(new { records, accessEvents = access });
    }

    private async Task<List<Guid>> OpenReportIds(Guid managerId, DateOnly asOf, CancellationToken ct)
    {
        return await db.ManagerLinks.AsNoTracking()
            .Where(link => link.ManagerId == managerId && link.EndedOn == null && link.EffectiveOn <= asOf)
            .Select(link => link.EmployeeId)
            .ToListAsync(ct);
    }

    private async Task<Dictionary<Guid, List<LeaveDayResponse>>> DaysFor(List<Guid> requestIds, CancellationToken ct)
    {
        if (requestIds.Count == 0)
        {
            return [];
        }

        var rows = await db.LeaveRequestDays.AsNoTracking()
            .Where(row => requestIds.Contains(row.RequestId))
            .OrderBy(row => row.OnDate)
            .ToListAsync(ct);
        return rows
            .GroupBy(row => row.RequestId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => new LeaveDayResponse(row.OnDate, row.Hours)).ToList());
    }

    private static IReadOnlyList<LeaveDayResponse> Days(Dictionary<Guid, List<LeaveDayResponse>> days, Guid requestId)
    {
        return days.TryGetValue(requestId, out var rows) ? rows : [];
    }

    private Task Stamp(Guid actorId, string? requestId, CancellationToken ct)
    {
        return AuditGuc.Apply(
            db,
            new AuditStamp(actorId, "web", null, "session", requestId, null, null),
            ct);
    }

    private async Task<List<AuditRecordResponse>> ReadRecords(AuditQuery query, CancellationToken ct)
    {
        const string sql =
            """
            select
              id,
              record_id,
              CAST(op AS text),
              ts,
              CAST(table_schema AS text),
              CAST(table_name AS text),
              CAST(record AS text),
              CAST(old_record AS text),
              changed_fields,
              actor_id,
              CAST(channel AS text),
              client_id,
              CAST(auth_factor AS text),
              request_id,
              quote_id,
              idempotency_key
            from audit.record_version
            where (@actor is null or actor_id = @actor)
              and (@table_name is null or table_name = @table_name)
              and (@channel is null or CAST(channel AS text) = @channel)
              and (@from_ts is null or ts >= @from_ts)
              and (@to_ts is null or ts < @to_ts)
            order by ts desc
            limit 100
            """;
        await using var command = AuditCommand(sql);
        AddFilters(command, query, includeTable: true);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<AuditRecordResponse>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AuditRecordResponse(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetString(4),
                reader.GetString(5),
                Redacted(ReadString(reader, 6)),
                Redacted(ReadString(reader, 7)),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<string[]>(8),
                ReadGuid(reader, 9),
                reader.GetString(10),
                ReadGuid(reader, 11),
                reader.GetString(12),
                ReadString(reader, 13),
                ReadGuid(reader, 14),
                ReadString(reader, 15)));
        }

        return rows;
    }

    private async Task<List<AuditAccessResponse>> ReadAccess(AuditQuery query, CancellationToken ct)
    {
        const string sql =
            """
            select
              id,
              at,
              actor_id,
              action,
              outcome,
              CAST(channel AS text),
              client_id,
              CAST(auth_factor AS text),
              subject_id,
              quote_id,
              tool_name,
              request_id,
              CAST(detail AS text)
            from audit.access_event
            where (@actor is null or actor_id = @actor)
              and (@channel is null or CAST(channel AS text) = @channel)
              and (@from_ts is null or at >= @from_ts)
              and (@to_ts is null or at < @to_ts)
            order by at desc
            limit 100
            """;
        await using var command = AuditCommand(sql);
        AddFilters(command, query, includeTable: false);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<AuditAccessResponse>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AuditAccessResponse(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                ReadGuid(reader, 2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                ReadGuid(reader, 6),
                reader.GetString(7),
                ReadGuid(reader, 8),
                ReadGuid(reader, 9),
                ReadString(reader, 10),
                ReadString(reader, 11),
                Redacted(ReadString(reader, 12))));
        }

        return rows;
    }

    private NpgsqlCommand AuditCommand(string sql)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = 15,
        };
        if (db.Database.CurrentTransaction?.GetDbTransaction() is NpgsqlTransaction tx)
        {
            command.Transaction = tx;
        }

        return command;
    }

    private static void AddFilters(NpgsqlCommand command, AuditQuery query, bool includeTable)
    {
        command.Parameters.Add(new NpgsqlParameter("actor", NpgsqlDbType.Uuid)
        {
            Value = query.ActorId is Guid actor ? actor : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("channel", NpgsqlDbType.Text)
        {
            Value = (object?)query.Channel ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("from_ts", NpgsqlDbType.TimestampTz)
        {
            Value = query.From is DateTimeOffset from ? from : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("to_ts", NpgsqlDbType.TimestampTz)
        {
            Value = query.To is DateTimeOffset to ? to : DBNull.Value,
        });
        if (includeTable)
        {
            command.Parameters.Add(new NpgsqlParameter("table_name", NpgsqlDbType.Text)
            {
                Value = (object?)query.Table ?? DBNull.Value,
            });
        }
    }

    private static JsonNode? Redacted(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject obj)
        {
            return node;
        }

        foreach (var key in obj.Select(pair => pair.Key).ToList())
        {
            if (!SecretKeys.Contains(key))
            {
                continue;
            }

            if (obj[key] is JsonValue scalar
                && scalar.TryGetValue<string>(out var text)
                && text == "[redacted]")
            {
                continue;
            }

            obj[key] = "[redacted]";
        }

        return obj;
    }

    private static string? ReadString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static Guid? ReadGuid(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static LeaveTypeResponse ToType(LeaveType type) => new(
        type.Id,
        type.Code,
        type.Model,
        type.HoursPerGrant,
        type.YearBoundary,
        type.CarryCapHours,
        type.MaxBalanceHours,
        type.WaitingDays);

    private static object WithholdingView(WithholdingProfile profile)
    {
        using var doc = JsonDocument.Parse(profile.W4);
        var root = doc.RootElement;
        return new
        {
            filingStatus = profile.FilingStatus,
            stateCode = profile.StateCode,
            otherIncomeCents = ReadCents(root, "other_income_cents"),
            deductionsCents = ReadCents(root, "deductions_cents"),
            extraWithholdingCents = ReadCents(root, "extra_withholding_cents"),
            dependentsCreditCents = ReadCents(root, "dependents_credit_cents"),
            grossPerPeriodCents = ReadCents(root, "gross_per_period_cents"),
            ytdWagesCents = ReadCents(root, "ytd_wages_cents"),
        };
    }

    private static int ReadCents(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return value.GetInt32();
    }

    private static string WriteW4(WithholdingBody body)
    {
        return JsonSerializer.Serialize(new Dictionary<string, int>
        {
            ["other_income_cents"] = body.OtherIncomeCents!.Value,
            ["deductions_cents"] = body.DeductionsCents!.Value,
            ["extra_withholding_cents"] = body.ExtraWithholdingCents!.Value,
            ["dependents_credit_cents"] = body.DependentsCreditCents!.Value,
            ["gross_per_period_cents"] = body.GrossPerPeriodCents!.Value,
            ["ytd_wages_cents"] = body.YtdWagesCents!.Value,
        }, W4Json);
    }

    private static string StateCode(Employee employee)
    {
        var jurisdiction = employee.Jurisdiction.Trim();
        var dash = jurisdiction.LastIndexOf('-');
        var code = (dash >= 0 ? jurisdiction[(dash + 1)..] : jurisdiction).Trim();
        return code.ToUpperInvariant();
    }

    private static GrantModel? ParseModel(string? model) => model switch
    {
        "accrued" => GrantModel.Accrued,
        "instant" => GrantModel.Instant,
        "unpaid" => GrantModel.Unpaid,
        _ => null,
    };

    private static DeductionKind? ParseKind(string? kind) => kind switch
    {
        "hsa" => DeductionKind.Hsa,
        "health_fsa" => DeductionKind.HealthFsa,
        "dependent_care_fsa" => DeductionKind.DependentCareFsa,
        _ => null,
    };

    private sealed record AuditRecordResponse(
        long Id,
        string RecordId,
        string Op,
        DateTimeOffset Ts,
        string TableSchema,
        string TableName,
        JsonNode? Record,
        JsonNode? OldRecord,
        IReadOnlyList<string>? ChangedFields,
        Guid? ActorId,
        string Channel,
        Guid? ClientId,
        string AuthFactor,
        string? RequestId,
        Guid? QuoteId,
        string? IdempotencyKey);

    private sealed record AuditAccessResponse(
        long Id,
        DateTimeOffset At,
        Guid? ActorId,
        string Action,
        string Outcome,
        string Channel,
        Guid? ClientId,
        string AuthFactor,
        Guid? SubjectId,
        Guid? QuoteId,
        string? ToolName,
        string? RequestId,
        JsonNode? Detail);
}
