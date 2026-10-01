using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbor.Host;

public sealed record WorkflowError(string Code, string Message);

public sealed class WorkflowResult<T>
{
    public bool Succeeded { get; init; }
    public T? Value { get; init; }
    public IReadOnlyList<WorkflowError> Errors { get; init; } = [];

    public static WorkflowResult<T> Ok(T value) => new() { Succeeded = true, Value = value };

    public static WorkflowResult<T> Fail(params WorkflowError[] errors) => new()
    {
        Succeeded = false,
        Errors = errors,
    };

    public static WorkflowResult<T> Fail(IReadOnlyList<WorkflowError> errors) => new()
    {
        Succeeded = false,
        Errors = errors,
    };
}

public sealed record LeaveQuotePayload
{
    public required string Action { get; init; }
    public Guid LeaveTypeId { get; init; }
    public string? Start { get; init; }
    public string? End { get; init; }
    public decimal HoursPerDay { get; init; }
    public bool AdminOverride { get; init; }
    public Guid RequestId { get; init; }
}

public sealed record DeductionQuotePayload
{
    public required string Kind { get; init; }
    public int PerPaycheckCents { get; init; }
    public string? QualifyingEvent { get; init; }
}

public static class QuoteJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
