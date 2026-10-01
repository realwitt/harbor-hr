using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Harbor;
using Microsoft.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Harbor.Host;

public static class OAuthEndpoints
{
    private static readonly string[] AllowedGrants = [GrantTypes.AuthorizationCode, GrantTypes.RefreshToken];

    public static void MapOAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-protected-resource", (IOptions<HarborAuthOptions> options) =>
        {
            var harbor = options.Value;
            return Results.Json(new
            {
                resource = McpResource.Url(harbor.PublicBaseUrl),
                authorization_servers = new[] { McpResource.BaseUrl(harbor.PublicBaseUrl) },
                bearer_methods_supported = new[] { "header" },
            });
        });

        app.MapPost("/connect/register", RegisterAsync).RequireRateLimiting("auth");
        app.MapMethods("/connect/authorize", [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync);
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext http,
        IOpenIddictApplicationManager applications,
        IOptions<HarborAuthOptions> options,
        CancellationToken ct)
    {
        JsonDocument body;
        try
        {
            body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return Error("invalid_client_metadata", "The body is not JSON.");
        }

        using (body)
        {
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error("invalid_client_metadata", "The body is not an object.");
            }

            if (root.TryGetProperty("client_secret", out var secret) && secret.ValueKind != JsonValueKind.Null)
            {
                return Error("invalid_client_metadata", "A public client has no secret.");
            }

            if (root.TryGetProperty("token_endpoint_auth_method", out var authMethod)
                && authMethod.ValueKind != JsonValueKind.Null
                && !string.Equals(authMethod.GetString(), "none", StringComparison.Ordinal))
            {
                return Error("invalid_client_metadata", "The client must use token auth method none.");
            }

            if (!AllowedList(root, "grant_types", AllowedGrants, out var grantError))
            {
                return Error("invalid_client_metadata", grantError!);
            }

            if (!AllowedList(root, "response_types", [ResponseTypes.Code], out var responseError))
            {
                return Error("invalid_client_metadata", responseError!);
            }

            if (!Redirects(root, out var redirects, out var redirectError))
            {
                return Error("invalid_client_metadata", redirectError!);
            }

            var displayName = "MCP client";
            if (root.TryGetProperty("client_name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                var text = name.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    displayName = text.Length > 200 ? text[..200] : text;
                }
            }

            var resource = McpResource.Url(options.Value.PublicBaseUrl);
            var clientId = AuthTokens.NewToken();
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                DisplayName = displayName,
                ClientType = ClientTypes.Public,
                ApplicationType = ApplicationTypes.Native,
                ConsentType = ConsentTypes.Explicit,
            };
            foreach (var uri in redirects)
            {
                descriptor.RedirectUris.Add(uri);
            }

            descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
            descriptor.Permissions.Add(Permissions.Endpoints.Token);
            descriptor.Permissions.Add(Permissions.Endpoints.Revocation);
            descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
            descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
            descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + Scopes.OfflineAccess);
            descriptor.Permissions.Add(Permissions.Prefixes.Resource + resource);
            descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
            await applications.CreateAsync(descriptor, ct);

            return Results.Json(new
            {
                client_id = clientId,
                client_name = displayName,
                redirect_uris = redirects.Select(uri => uri.AbsoluteUri).ToArray(),
                grant_types = AllowedGrants,
                response_types = new[] { ResponseTypes.Code },
                token_endpoint_auth_method = "none",
            }, statusCode: StatusCodes.Status201Created);
        }
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http,
        HarborDbContext db,
        IOpenIddictApplicationManager applications,
        IOptions<HarborAuthOptions> options,
        CancellationToken ct)
    {
        var caller = HarborCaller.Read(http);
        if (caller is null)
        {
            return Results.Json(new { error = "sign_in_required" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!caller.Ready)
        {
            return Results.Json(new { error = "account_not_ready" }, statusCode: StatusCodes.Status403Forbidden);
        }

        var request = http.GetOpenIddictServerRequest();
        if (request is null || string.IsNullOrEmpty(request.ClientId))
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var employee = await db.Employees.AsNoTracking()
            .FirstAsync(row => row.Id == caller.Employee.Id, ct);
        if (employee.McpEnabledAt is null)
        {
            return Results.Json(new { error = "mcp_disabled" }, statusCode: StatusCodes.Status403Forbidden);
        }

        var application = await applications.FindByClientIdAsync(request.ClientId, ct);
        if (application is null)
        {
            return Results.Json(new { error = "invalid_client" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var displayName = await applications.GetDisplayNameAsync(application, ct) ?? "MCP client";
        if (HttpMethods.IsGet(http.Request.Method))
        {
            return Results.Content(ConsentPage(displayName, http.Request.Query), "text/html; charset=utf-8");
        }

        if (!http.Request.HasFormContentType)
        {
            return Results.Json(new { error = "invalid_decision" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var decision = http.Request.Form["decision"].ToString();
        if (string.Equals(decision, "deny", StringComparison.Ordinal))
        {
            return Results.Forbid(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        if (!string.Equals(decision, "accept", StringComparison.Ordinal))
        {
            return Results.Json(new { error = "invalid_decision" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var applicationId = await applications.GetIdAsync(application, ct)
            ?? throw new InvalidOperationException("The application id is missing.");
        var mcp = await db.McpClients.FirstOrDefaultAsync(row =>
            row.EmployeeId == employee.Id
            && row.OauthApplicationId == applicationId
            && row.RevokedAt == null, ct);
        if (mcp is null)
        {
            mcp = new McpClient
            {
                Id = Guid.NewGuid(),
                EmployeeId = employee.Id,
                ClientName = displayName,
                OauthApplicationId = applicationId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.McpClients.Add(mcp);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await AuditGuc.Apply(db, new AuditStamp(employee.Id, "web", mcp.Id, "session", http.TraceIdentifier, null, null), ct);
            McpClientGate.DetachIdle(db);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var identity = new ClaimsIdentity(
            TokenValidationParameters.DefaultAuthenticationType,
            Claims.Name,
            Claims.Role);
        identity.SetClaim(Claims.Subject, employee.Id.ToString("D"));
        identity.SetClaim(McpClaims.ClientRow, mcp.Id.ToString("D"));
        identity.SetClaim(McpClaims.OAuthClient, request.ClientId);
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(Scopes.OfflineAccess);
        principal.SetResources(McpResource.Url(options.Value.PublicBaseUrl));
        principal.SetDestinations(claim => claim.Type is Claims.Subject or McpClaims.ClientRow or McpClaims.OAuthClient
            ? [Destinations.AccessToken, Destinations.IssuedToken]
            : []);
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static string ConsentPage(string clientName, IQueryCollection query)
    {
        var fields = new StringBuilder();
        foreach (var pair in query)
        {
            foreach (var value in pair.Value)
            {
                fields.Append("<input type=\"hidden\" name=\"")
                    .Append(WebUtility.HtmlEncode(pair.Key))
                    .Append("\" value=\"")
                    .Append(WebUtility.HtmlEncode(value))
                    .Append("\">");
            }
        }

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Harbor</title></head>
            <body>
            <p>{WebUtility.HtmlEncode(clientName)}</p>
            <p>This client can act as you.</p>
            <form method="post" action="/connect/authorize">
            {fields}
            <button type="submit" name="decision" value="accept">Accept</button>
            <button type="submit" name="decision" value="deny">Deny</button>
            </form>
            </body>
            </html>
            """;
    }

    private static bool AllowedList(JsonElement root, string name, string[] allowed, out string? error)
    {
        error = null;
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            error = "The " + name + " value is not a list.";
            return false;
        }

        foreach (var item in value.EnumerateArray())
        {
            var text = item.GetString();
            if (text is null || !allowed.Contains(text, StringComparer.Ordinal))
            {
                error = "The " + name + " value is not allowed.";
                return false;
            }
        }

        return true;
    }

    private static bool Redirects(JsonElement root, out List<Uri> uris, out string? error)
    {
        uris = [];
        error = null;
        if (!root.TryGetProperty("redirect_uris", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            error = "A redirect URI is required.";
            return false;
        }

        foreach (var item in value.EnumerateArray())
        {
            var text = item.GetString();
            if (string.IsNullOrWhiteSpace(text)
                || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "The redirect URI is not valid.";
                return false;
            }

            uris.Add(uri);
        }

        if (uris.Count == 0)
        {
            error = "A redirect URI is required.";
            return false;
        }

        return true;
    }

    private static IResult Error(string code, string message) =>
        Results.Json(new { error = code, error_description = message }, statusCode: StatusCodes.Status400BadRequest);
}
