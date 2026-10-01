namespace Harbor.Host;

public sealed class AuthResult
{
    public int Status { get; init; } = 200;
    public string? Error { get; init; }
    public object? Body { get; init; }
    public Guid? SessionId { get; init; }
    public DateTimeOffset? SessionExpiresAt { get; init; }
    public bool ClearSession { get; init; }

    public static AuthResult Success(object? body, Guid? sessionId = null, DateTimeOffset? expires = null) => new()
    {
        Status = 200,
        Body = body,
        SessionId = sessionId,
        SessionExpiresAt = expires,
    };

    public static AuthResult Fail(int status, string error) => new()
    {
        Status = status,
        Error = error,
    };

    public static AuthResult SignedOut() => new()
    {
        Status = StatusCodes.Status204NoContent,
        ClearSession = true,
    };
}
