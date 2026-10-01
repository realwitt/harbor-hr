using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation.Results;

namespace Harbor.Host;

public static class HarborJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    static HarborJson()
    {
        Options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }
}

public sealed class BusinessResult
{
    private BusinessResult(int status, object body)
    {
        Status = status;
        Body = body;
    }

    public int Status { get; }
    public object Body { get; }

    public IResult ToHttp() => Results.Json(Body, HarborJson.Options, statusCode: Status);

    public static BusinessResult Ok(object body) => new(StatusCodes.Status200OK, body);

    public static BusinessResult Fail(int status, string code, string message) =>
        new(status, new { errors = new[] { new { code, message } } });
}

public sealed record AuditQuery(Guid? ActorId, string? Table, string? Channel, DateTimeOffset? From, DateTimeOffset? To);

internal static class EmployeeClock
{
    public static bool TryToday(string timezone, DateTimeOffset now, out DateOnly today, out string? error)
    {
        today = default;
        error = null;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var local = TimeZoneInfo.ConvertTime(now, zone);
            today = DateOnly.FromDateTime(local.DateTime);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = "The employee timezone is not valid.";
            return false;
        }
    }
}

internal static class ApiResults
{
    public static IResult SignedOut() =>
        Results.Json(new { error = "sign_in_required" }, HarborJson.Options, statusCode: StatusCodes.Status401Unauthorized);

    public static IResult Forbidden(string code, string message) =>
        BusinessResult.Fail(StatusCodes.Status403Forbidden, code, message).ToHttp();

    public static IResult InvalidMessage(string message) =>
        BusinessResult.Fail(StatusCodes.Status400BadRequest, "invalid", message).ToHttp();

    public static IResult Invalid(ValidationResult result)
    {
        return Results.Json(
            new
            {
                errors = result.Errors.Select(error => new { code = "invalid", message = error.ErrorMessage }),
            },
            HarborJson.Options,
            statusCode: StatusCodes.Status400BadRequest);
    }

    public static IResult FromWorkflow(IReadOnlyList<WorkflowError> errors)
    {
        var status = errors.Any(error => error.Code is "account_not_ready" or "not_authorized")
            ? StatusCodes.Status403Forbidden
            : StatusCodes.Status400BadRequest;
        return Results.Json(
            new { errors = errors.Select(error => new { code = error.Code, message = error.Message }) },
            HarborJson.Options,
            statusCode: status);
    }

    public static IResult LeavePreview(WorkflowResult<LeavePreview> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            return FromWorkflow(result.Errors);
        }

        var value = result.Value;
        return Results.Json(
            new
            {
                quoteId = value.QuoteId,
                expiresAt = value.ExpiresAt,
                projection = value.Projection,
                warnings = value.Warnings,
            },
            HarborJson.Options);
    }

    public static IResult LeaveCommand(WorkflowResult<LeaveCommandResult> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            return FromWorkflow(result.Errors);
        }

        return Results.Json(
            new
            {
                requestId = result.Value.RequestId,
                status = result.Value.Status,
                replay = result.Value.Replay,
            },
            HarborJson.Options);
    }

    public static IResult DeductionPreview(WorkflowResult<DeductionPreview> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            return FromWorkflow(result.Errors);
        }

        return Results.Json(
            new
            {
                quoteId = result.Value.QuoteId,
                expiresAt = result.Value.ExpiresAt,
                estimate = result.Value.Estimate,
            },
            HarborJson.Options);
    }

    public static IResult DeductionCommand(WorkflowResult<DeductionCommandResult> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            return FromWorkflow(result.Errors);
        }

        return Results.Json(
            new
            {
                electionId = result.Value.ElectionId,
                status = result.Value.Status,
                replay = result.Value.Replay,
            },
            HarborJson.Options);
    }

    public static async Task<(T? Body, IResult? Error)> Read<T>(HttpRequest request, CancellationToken ct)
        where T : class
    {
        try
        {
            var body = await request.ReadFromJsonAsync<T>(HarborJson.Options, ct);
            if (body is null)
            {
                return (null, InvalidMessage("The request body is required."));
            }

            return (body, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or BadHttpRequestException)
        {
            return (null, InvalidMessage("The request body is not valid."));
        }
    }
}
