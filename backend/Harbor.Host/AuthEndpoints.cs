using Fido2NetLib;
using Microsoft.Extensions.Options;

namespace Harbor.Host;

public sealed class JoinRequestBody
{
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? Note { get; set; }
    public string? TurnstileToken { get; set; }
}

public sealed class InviteBody
{
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? Role { get; set; }
    public Guid? ManagerId { get; set; }
    public DateOnly? HiredOn { get; set; }
    public string? Jurisdiction { get; set; }
    public string? Timezone { get; set; }
}

public sealed class TokenBody
{
    public string? Token { get; set; }
}

public sealed class RegisterBody
{
    public string? Token { get; set; }
    public string? TurnstileToken { get; set; }
    public AuthenticatorAttestationRawResponse? Attestation { get; set; }
}

public sealed class EmailBody
{
    public string? Email { get; set; }
}

public sealed class AssertBody
{
    public string? Email { get; set; }
    public AuthenticatorAssertionRawResponse? Assertion { get; set; }
}

public sealed class RecoveryAssertBody
{
    public string? Email { get; set; }
    public string? Code { get; set; }
}

public sealed class PasskeyBody
{
    public AuthenticatorAttestationRawResponse? Attestation { get; set; }
}

public sealed class StepUpOptionsBody
{
    public string? Action { get; set; }
    public Guid? QuoteId { get; set; }
}

public sealed class StepUpBody
{
    public AuthenticatorAssertionRawResponse? Assertion { get; set; }
}

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").RequireRateLimiting("auth");
        McpSwitch.Map(group);
        group.MapPost("/invites", async (InviteBody body, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.CreateInvite(HarborCaller.Read(http), body, ct), options.Value));
        group.MapPost("/join-requests", async (JoinRequestBody body, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.RequestToJoin(body, ct), options.Value));
        group.MapGet("/join-requests", async (HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.ListJoinRequests(HarborCaller.Read(http), ct), options.Value));
        group.MapGet("/join-requests/{id:guid}", async (Guid id, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.OpenJoinRequest(HarborCaller.Read(http), id, ct), options.Value));
        group.MapPost("/join-requests/{id:guid}/approve", async (Guid id, InviteBody body, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.ApproveJoinRequest(HarborCaller.Read(http), id, body, ct), options.Value));
        group.MapPost("/join-requests/{id:guid}/dismiss", async (Guid id, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.DismissJoinRequest(HarborCaller.Read(http), id, ct), options.Value));
        group.MapGet("/invites/{token}", async (string token, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.OpenInvite(token, ct), options.Value));
        group.MapPost("/register/options", async (TokenBody body, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.RegisterOptions(body.Token, ct), options.Value));
        group.MapPost("/register", async (RegisterBody body, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.Register(body, HarborCaller.Read(http)?.SessionId, ct), options.Value));
        group.MapPost("/assert/options", async (EmailBody body, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.AssertOptions(body.Email, ct), options.Value));
        group.MapPost("/assert", async (AssertBody body, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.Assert(body, HarborCaller.Read(http)?.SessionId, ct), options.Value));
        group.MapPost("/recovery/generate", async (HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.GenerateRecovery(HarborCaller.Read(http), ct), options.Value));
        group.MapPost("/recovery/acknowledge", async (HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.AcknowledgeRecovery(HarborCaller.Read(http), ct), options.Value));
        group.MapPost("/recovery/assert", async (RecoveryAssertBody body, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.RecoveryAssert(body.Email, body.Code, HarborCaller.Read(http)?.SessionId, ct), options.Value));
        group.MapGet("/passkeys", async (HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.ListPasskeys(HarborCaller.Read(http), ct), options.Value));
        group.MapPost("/passkeys/options", async (HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.PasskeyOptions(HarborCaller.Read(http), ct), options.Value));
        group.MapPost("/passkeys", async (PasskeyBody body, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.AddPasskey(HarborCaller.Read(http), body.Attestation, ct), options.Value));
        group.MapDelete("/passkeys/{id:guid}", async (Guid id, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.RemovePasskey(HarborCaller.Read(http), id, ct), options.Value));
        group.MapPost("/step-up/options", async (StepUpOptionsBody body, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.StepUpOptions(HarborCaller.Read(http), body.Action, body.QuoteId, ct), options.Value));
        group.MapPost("/step-up", async (StepUpBody body, HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.StepUp(HarborCaller.Read(http), body.Assertion, ct), options.Value));
        group.MapPost("/sign-out", async (HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.SignOut(HarborCaller.Read(http), ct), options.Value));
        group.MapGet("/me", async (HttpContext http, AuthWorkflow auth, IOptions<HarborAuthOptions> options, CancellationToken ct) =>
            Respond(http, await auth.Me(HarborCaller.Read(http), ct), options.Value));
    }

    private static IResult Respond(HttpContext http, AuthResult result, HarborAuthOptions options)
    {
        if (result.SessionId is Guid sessionId && result.SessionExpiresAt is DateTimeOffset expires)
        {
            SessionCookies.Set(http.Response, sessionId, options.CookieSecure, expires);
        }

        if (result.ClearSession)
        {
            SessionCookies.Clear(http.Response, options.CookieSecure);
        }

        if (result.Error is not null)
        {
            return Results.Json(new { error = result.Error }, statusCode: result.Status);
        }

        if (result.Status == StatusCodes.Status204NoContent || result.Body is null)
        {
            return Results.StatusCode(result.Status);
        }

        return Results.Json(result.Body, statusCode: result.Status);
    }
}
