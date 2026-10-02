using FluentValidation;
using Harbor;

namespace Harbor.Host;

public static class BusinessEndpoints
{
    private const string Web = "web";

    private static readonly LeavePreviewValidator LeavePreviewRules = new();
    private static readonly LeaveSubmitValidator LeaveSubmitRules = new();
    private static readonly LeaveCancelPreviewValidator LeaveCancelPreviewRules = new();
    private static readonly LeaveCancelValidator LeaveCancelRules = new();
    private static readonly DeductionPreviewValidator DeductionPreviewRules = new();
    private static readonly DeductionSubmitValidator DeductionSubmitRules = new();
    private static readonly WithholdingValidator WithholdingRules = new();
    private static readonly LeaveTypeValidator LeaveTypeRules = new();
    private static readonly BlackoutValidator BlackoutRules = new();
    private static readonly DeductionCapValidator DeductionCapRules = new();
    private static readonly HdhpValidator HdhpRules = new();
    private static readonly LedgerValidator LedgerRules = new();
    private static readonly PayPeriodValidator PayPeriodRules = new();
    private static readonly HolidayValidator HolidayRules = new();

    public static void MapBusiness(this IEndpointRouteBuilder app)
    {
        var leave = app.MapGroup("/api/leave");
        leave.MapGet("/types", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (!TryCaller(http, out _, out var error))
            {
                return error!;
            }

            return (await business.LeaveTypes(ct)).ToHttp();
        });
        leave.MapGet("/balances", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            if (!TryToday(caller.Employee, out var today, out error))
            {
                return error!;
            }

            DateOnly on;
            if (!http.Request.Query.ContainsKey("on"))
            {
                on = today;
            }
            else if (!IsoDate.Try(http.Request.Query["on"], out on))
            {
                return ApiResults.InvalidMessage("The date is not an ISO date.");
            }

            var asOf = on < today ? on : today;
            return (await business.Balances(caller.Employee, asOf, on, ct)).ToHttp();
        });
        leave.MapGet("/requests", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            return (await business.OwnRequests(caller.Employee.Id, ct)).ToHttp();
        });
        leave.MapPost("/preview", async (HttpContext http, LeaveWorkflow workflow, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            var (body, invalid) = await Bind(http.Request, LeavePreviewRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            if (!TryToday(caller.Employee, out var asOf, out error))
            {
                return error!;
            }

            var result = await workflow.PreviewAsync(new LeavePreviewRequest
            {
                EmployeeId = caller.Employee.Id,
                ActorId = caller.Employee.Id,
                LeaveTypeId = body.LeaveTypeId,
                Start = body.Start,
                End = body.End,
                HoursPerDay = body.HoursPerDay,
                AdminOverride = body.AdminOverride,
                Channel = Web,
                RequestId = http.TraceIdentifier,
                AsOf = asOf,
                Now = DateTimeOffset.UtcNow,
            }, ct);
            return ApiResults.LeavePreview(result);
        });
        leave.MapPost("/submit", async (HttpContext http, LeaveWorkflow workflow, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            var (body, invalid) = await Bind(http.Request, LeaveSubmitRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            if (ReadKey(http.Request, out var key) is IResult missing)
            {
                return missing;
            }

            if (!TryToday(caller.Employee, out var asOf, out error))
            {
                return error!;
            }

            var result = await workflow.SubmitAsync(new LeaveSubmitRequest
            {
                EmployeeId = caller.Employee.Id,
                ActorId = caller.Employee.Id,
                QuoteId = body.QuoteId,
                IdempotencyKey = key,
                LeaveTypeId = body.LeaveTypeId,
                Start = body.Start,
                End = body.End,
                HoursPerDay = body.HoursPerDay,
                AdminOverride = body.AdminOverride,
                Channel = Web,
                Confirm = false,
                RequestId = http.TraceIdentifier,
                AsOf = asOf,
                Now = DateTimeOffset.UtcNow,
            }, ct);
            return ApiResults.LeaveCommand(result);
        });
        leave.MapPost("/cancel/preview", async (HttpContext http, LeaveWorkflow workflow, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            var (body, invalid) = await Bind(http.Request, LeaveCancelPreviewRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            var result = await workflow.PreviewCancelAsync(new LeaveCancelPreviewRequest
            {
                EmployeeId = caller.Employee.Id,
                ActorId = caller.Employee.Id,
                RequestId = body.RequestId,
                Channel = Web,
                TraceId = http.TraceIdentifier,
                Now = DateTimeOffset.UtcNow,
            }, ct);
            return ApiResults.LeavePreview(result);
        });
        leave.MapPost("/cancel", async (HttpContext http, LeaveWorkflow workflow, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            var (body, invalid) = await Bind(http.Request, LeaveCancelRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            // Cancel stores no idempotency key. The header is still required.
            if (ReadKey(http.Request, out _) is IResult missing)
            {
                return missing;
            }

            var result = await workflow.CancelAsync(new LeaveCancelRequest
            {
                EmployeeId = caller.Employee.Id,
                ActorId = caller.Employee.Id,
                QuoteId = body.QuoteId,
                RequestId = body.RequestId,
                Channel = Web,
                Confirm = false,
                TraceId = http.TraceIdentifier,
                Now = DateTimeOffset.UtcNow,
            }, ct);
            return ApiResults.LeaveCommand(result);
        });
        leave.MapPost("/requests/{id:guid}/approve", async (Guid id, HttpContext http, LeaveWorkflow workflow, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            if (!TryToday(caller.Employee, out var asOf, out error))
            {
                return error!;
            }

            var result = await workflow.ApproveAsync(new LeaveDecisionRequest
            {
                ActorId = caller.Employee.Id,
                RequestId = id,
                Channel = Web,
                TraceId = http.TraceIdentifier,
                AsOf = asOf,
                Now = DateTimeOffset.UtcNow,
            }, ct);
            return ApiResults.LeaveCommand(result);
        });
        leave.MapPost("/requests/{id:guid}/deny", async (Guid id, HttpContext http, LeaveWorkflow workflow, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            if (!TryToday(caller.Employee, out var asOf, out error))
            {
                return error!;
            }

            var result = await workflow.DenyAsync(new LeaveDecisionRequest
            {
                ActorId = caller.Employee.Id,
                RequestId = id,
                Channel = Web,
                TraceId = http.TraceIdentifier,
                AsOf = asOf,
                Now = DateTimeOffset.UtcNow,
            }, ct);
            return ApiResults.LeaveCommand(result);
        });

        var team = app.MapGroup("/api/team");
        team.MapGet("/queue", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            if (!TryToday(caller.Employee, out var today, out error))
            {
                return error!;
            }

            return (await business.TeamQueue(caller.Employee.Id, today, ct)).ToHttp();
        });
        team.MapGet("/calendar", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            if (!IsoDate.Try(http.Request.Query["from"], out var from) || !IsoDate.Try(http.Request.Query["to"], out var to))
            {
                return ApiResults.InvalidMessage("The date is not an ISO date.");
            }

            if (to < from)
            {
                return ApiResults.InvalidMessage("The end date is before the start date.");
            }

            if (!TryToday(caller.Employee, out var today, out error))
            {
                return error!;
            }

            return (await business.TeamCalendar(caller.Employee.Id, today, from, to, ct)).ToHttp();
        });

        app.MapGet("/api/deductions", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            if (!TryToday(caller.Employee, out var today, out error))
            {
                return error!;
            }

            return (await business.Deductions(caller.Employee.Id, today.Year, ct)).ToHttp();
        });
        var deductions = app.MapGroup("/api/deductions");
        deductions.MapPost("/preview", async (HttpContext http, DeductionWorkflow workflow, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            var (body, invalid) = await Bind(http.Request, DeductionPreviewRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            if (!TryToday(caller.Employee, out var asOf, out error))
            {
                return error!;
            }

            var result = await workflow.PreviewAsync(new DeductionPreviewRequest
            {
                EmployeeId = caller.Employee.Id,
                ActorId = caller.Employee.Id,
                Kind = body.Kind!,
                PerPaycheckCents = body.PerPaycheckCents!.Value,
                QualifyingEvent = body.QualifyingEvent,
                Channel = Web,
                RequestId = http.TraceIdentifier,
                AsOf = asOf,
                Now = DateTimeOffset.UtcNow,
            }, ct);
            return ApiResults.DeductionPreview(result);
        });
        deductions.MapPost("/submit", async (HttpContext http, DeductionWorkflow workflow, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            var (body, invalid) = await Bind(http.Request, DeductionSubmitRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            if (ReadKey(http.Request, out var key) is IResult missing)
            {
                return missing;
            }

            if (!TryToday(caller.Employee, out var asOf, out error))
            {
                return error!;
            }

            var result = await workflow.SubmitAsync(new DeductionSubmitRequest
            {
                EmployeeId = caller.Employee.Id,
                ActorId = caller.Employee.Id,
                QuoteId = body.QuoteId,
                IdempotencyKey = key,
                Kind = body.Kind!,
                PerPaycheckCents = body.PerPaycheckCents!.Value,
                QualifyingEvent = body.QualifyingEvent,
                Channel = Web,
                Confirm = false,
                RequestId = http.TraceIdentifier,
                AsOf = asOf,
                Now = DateTimeOffset.UtcNow,
            }, ct);
            return ApiResults.DeductionCommand(result);
        });

        app.MapGet("/api/withholding", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            return (await business.GetWithholding(caller.Employee.Id, ct)).ToHttp();
        });
        app.MapPut("/api/withholding", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (!TryCaller(http, out var caller, out var error))
            {
                return error!;
            }

            var (body, invalid) = await Bind(http.Request, WithholdingRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            return (await business.PutWithholding(caller.Employee.Id, http.TraceIdentifier, body, ct)).ToHttp();
        });

        var admin = app.MapGroup("/api/admin");
        admin.MapGet("/employees", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            return (await business.Employees(ct)).ToHttp();
        });
        admin.MapGet("/leave-types", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            return (await business.LeaveTypes(ct)).ToHttp();
        });
        admin.MapPost("/leave-types", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            var (body, invalid) = await Bind(http.Request, LeaveTypeRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            var caller = HarborCaller.Read(http)!;
            return (await business.CreateLeaveType(caller.Employee.Id, http.TraceIdentifier, body, ct)).ToHttp();
        });
        admin.MapGet("/blackouts", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            return (await business.Blackouts(ct)).ToHttp();
        });
        admin.MapPost("/blackouts", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            var (body, invalid) = await Bind(http.Request, BlackoutRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            var caller = HarborCaller.Read(http)!;
            IsoDate.Try(body.On, out var on);
            return (await business.AddBlackout(caller.Employee.Id, http.TraceIdentifier, on, body.Reason!, ct)).ToHttp();
        });
        admin.MapDelete("/blackouts/{on}", async (string on, HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            if (!IsoDate.Try(on, out var date))
            {
                return ApiResults.InvalidMessage("The date is not an ISO date.");
            }

            var caller = HarborCaller.Read(http)!;
            return (await business.DeleteBlackout(caller.Employee.Id, http.TraceIdentifier, date, ct)).ToHttp();
        });
        admin.MapGet("/deduction-caps", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            return (await business.DeductionCaps(ct)).ToHttp();
        });
        admin.MapPost("/deduction-caps", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            var (body, invalid) = await Bind(http.Request, DeductionCapRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            var caller = HarborCaller.Read(http)!;
            return (await business.AddDeductionCap(caller.Employee.Id, http.TraceIdentifier, body, ct)).ToHttp();
        });
        admin.MapPost("/employees/{id:guid}/hdhp", async (Guid id, HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            var (body, invalid) = await Bind(http.Request, HdhpRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            if (!TryCoverage(body.HsaCoverage, out var coverage))
            {
                return ApiResults.InvalidMessage("The HSA coverage is not valid.");
            }

            var caller = HarborCaller.Read(http)!;
            return (await business.SetHdhp(caller.Employee.Id, http.TraceIdentifier, id, body.HdhpEligible!.Value, coverage, ct)).ToHttp();
        });
        admin.MapPost("/ledger", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            var (body, invalid) = await Bind(http.Request, LedgerRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            IsoDate.Try(body.EffectiveOn, out var on);
            var caller = HarborCaller.Read(http)!;
            return (await business.AddAdjustment(
                caller.Employee.Id,
                body.EmployeeId,
                body.LeaveTypeId,
                body.Hours!.Value,
                on,
                body.Note!,
                ct)).ToHttp();
        });
        admin.MapGet("/audit", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            if (TryAuditQuery(http.Request, out var query) is IResult invalid)
            {
                return invalid;
            }

            var caller = HarborCaller.Read(http)!;
            return (await business.ReadAudit(caller.Employee.Id, http.TraceIdentifier, query, ct)).ToHttp();
        });
        admin.MapGet("/pay-periods", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            return (await business.PayPeriods(ct)).ToHttp();
        });
        admin.MapPost("/pay-periods", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            var (body, invalid) = await Bind(http.Request, PayPeriodRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            IsoDate.Try(body.StartsOn, out var starts);
            IsoDate.Try(body.EndsOn, out var ends);
            IsoDate.Try(body.PayDate, out var payDate);
            var caller = HarborCaller.Read(http)!;
            return (await business.AddPayPeriod(caller.Employee.Id, http.TraceIdentifier, starts, ends, payDate, ct)).ToHttp();
        });
        admin.MapGet("/holidays", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            return (await business.Holidays(ct)).ToHttp();
        });
        admin.MapPost("/holidays", async (HttpContext http, HarborBusiness business, CancellationToken ct) =>
        {
            if (RequireHr(http) is IResult denied)
            {
                return denied;
            }

            var (body, invalid) = await Bind(http.Request, HolidayRules, ct);
            if (invalid is not null || body is null)
            {
                return invalid!;
            }

            IsoDate.Try(body.On, out var on);
            var caller = HarborCaller.Read(http)!;
            return (await business.AddHoliday(caller.Employee.Id, http.TraceIdentifier, on, body.Name!, ct)).ToHttp();
        });
    }

    private static async Task<(T? Body, IResult? Error)> Bind<T>(HttpRequest request, IValidator<T> validator, CancellationToken ct)
        where T : class
    {
        var (body, error) = await ApiResults.Read<T>(request, ct);
        if (error is not null || body is null)
        {
            return (null, error);
        }

        var validation = await validator.ValidateAsync(body, ct);
        if (!validation.IsValid)
        {
            return (null, ApiResults.Invalid(validation));
        }

        return (body, null);
    }

    private static bool TryCaller(HttpContext http, out HarborCaller caller, out IResult? error)
    {
        var found = HarborCaller.Read(http);
        if (found is null)
        {
            caller = null!;
            error = ApiResults.SignedOut();
            return false;
        }

        caller = found;
        error = null;
        return true;
    }

    private static IResult? RequireHr(HttpContext http)
    {
        if (!TryCaller(http, out var caller, out var error))
        {
            return error;
        }

        if (caller.Employee.Role != EmployeeRole.HrAdmin)
        {
            return ApiResults.Forbidden("not_authorized", "HR admin is required.");
        }

        return null;
    }

    private static bool TryToday(Employee employee, out DateOnly today, out IResult? error)
    {
        if (!EmployeeClock.TryToday(employee.Timezone, DateTimeOffset.UtcNow, out today, out var message))
        {
            error = ApiResults.InvalidMessage(message ?? "The employee timezone is not valid.");
            return false;
        }

        error = null;
        return true;
    }

    private static IResult? ReadKey(HttpRequest request, out string key)
    {
        key = "";
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values))
        {
            return ApiResults.InvalidMessage("The idempotency key is required.");
        }

        key = values.ToString().Trim();
        if (key.Length == 0 || key.Length > 200)
        {
            return ApiResults.InvalidMessage("The idempotency key is required.");
        }

        return null;
    }

    private static IResult? TryAuditQuery(HttpRequest request, out AuditQuery query)
    {
        query = new AuditQuery(null, null, null, null, null);
        Guid? actorId = null;
        if (request.Query.ContainsKey("actorId"))
        {
            if (!Guid.TryParse(request.Query["actorId"], out var parsed) || parsed == Guid.Empty)
            {
                return ApiResults.InvalidMessage("The actor id is not valid.");
            }

            actorId = parsed;
        }

        string? table = null;
        if (request.Query.ContainsKey("table"))
        {
            table = request.Query["table"].ToString().Trim();
            if (table.Length == 0 || table.Length > 63)
            {
                return ApiResults.InvalidMessage("The table name is not valid.");
            }
        }

        string? channel = null;
        if (request.Query.ContainsKey("channel"))
        {
            channel = request.Query["channel"].ToString();
            if (channel is not ("web" or "mcp" or "job" or "system"))
            {
                return ApiResults.InvalidMessage("The channel is not valid.");
            }
        }

        DateTimeOffset? from = null;
        if (request.Query.ContainsKey("from"))
        {
            if (!IsoDate.Try(request.Query["from"], out var day))
            {
                return ApiResults.InvalidMessage("The date is not an ISO date.");
            }

            from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        DateTimeOffset? to = null;
        if (request.Query.ContainsKey("to"))
        {
            if (!IsoDate.Try(request.Query["to"], out var day))
            {
                return ApiResults.InvalidMessage("The date is not an ISO date.");
            }

            to = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        if (from is not null && to is not null && to <= from)
        {
            return ApiResults.InvalidMessage("The end date is before the start date.");
        }

        query = new AuditQuery(actorId, table, channel, from, to);
        return null;
    }

    private static bool TryCoverage(string? text, out HsaCoverage? coverage)
    {
        coverage = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        switch (text)
        {
            case "self":
                coverage = HsaCoverage.Self;
                return true;
            case "family":
                coverage = HsaCoverage.Family;
                return true;
            default:
                return false;
        }
    }
}
