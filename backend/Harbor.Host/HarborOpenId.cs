using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;
using ValidationEvents = OpenIddict.Validation.OpenIddictValidationEvents;

namespace Harbor.Host;

public static class HarborOpenId
{
    public const string Policy = "mcp";

    public static void Add(IServiceCollection services, HarborAuthOptions harbor)
    {
        services.Configure<HarborAuthOptions>(copied => copied.PublicBaseUrl = harbor.PublicBaseUrl);
        var issuer = McpResource.BaseUrl(harbor.PublicBaseUrl);
        var resource = McpResource.Url(harbor.PublicBaseUrl);
        services.AddHttpContextAccessor();
        services.AddAuthentication();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(Policy, policy =>
            {
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });
        });

        services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<HarborDbContext>())
            .AddServer(server =>
            {
                server.SetIssuer(issuer);
                server.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow();
                server.SetAuthorizationEndpointUris("/connect/authorize")
                    .SetTokenEndpointUris("/connect/token")
                    .SetRevocationEndpointUris("/connect/revoke")
                    .SetConfigurationEndpointUris("/.well-known/oauth-authorization-server");
                server.RegisterScopes(Scopes.OfflineAccess);
                server.RegisterResources(resource);
                server.RequireProofKeyForCodeExchange();
                server.SetAccessTokenLifetime(TimeSpan.FromHours(1));
                server.SetRefreshTokenLifetime(TimeSpan.FromDays(14));
                server.SetRefreshTokenReuseLeeway(TimeSpan.Zero);
                server.DisableAccessTokenEncryption();
                server.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
                server.Configure(options =>
                {
                    options.CodeChallengeMethods.Clear();
                    options.CodeChallengeMethods.Add(CodeChallengeMethods.Sha256);
                    options.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.None);
                });
                server.AddEventHandler<HandleConfigurationRequestContext>(builder =>
                    builder.UseSingletonHandler<McpRegistrationEndpoint>().SetOrder(int.MaxValue - 10_000));
                server.AddEventHandler<ValidateAuthorizationRequestContext>(builder =>
                    builder.UseSingletonHandler<McpResourceGuard>().SetOrder(int.MaxValue - 100_000));
                server.AddEventHandler<ProcessSignInContext>(builder =>
                    builder.UseSingletonHandler<McpAudienceGuard>().SetOrder(int.MinValue + 110_000));
                server.AddEventHandler<ProcessAuthenticationContext>(builder =>
                    builder.UseScopedHandler<McpServerGate>().SetOrder(int.MaxValue - 50_000));
                var aspNet = server.UseAspNetCore().EnableAuthorizationEndpointPassthrough();
                if (!McpResource.IsHttps(harbor.PublicBaseUrl))
                {
                    aspNet.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.UseAspNetCore();
                validation.AddEventHandler<ValidationEvents.ProcessAuthenticationContext>(builder =>
                    builder.UseScopedHandler<McpAccessGate>().SetOrder(int.MaxValue - 50_000));
            });

        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<HarborMcpTools>()
            .WithResources<HarborMcpResources>();
    }
}

public sealed class McpRegistrationEndpoint(IOptions<HarborAuthOptions> options)
    : IOpenIddictServerHandler<HandleConfigurationRequestContext>
{
    public ValueTask HandleAsync(HandleConfigurationRequestContext context)
    {
        var issuer = McpResource.BaseUrl(options.Value.PublicBaseUrl).TrimEnd('/');
        context.Metadata["registration_endpoint"] = issuer + "/connect/register";
        return ValueTask.CompletedTask;
    }
}

public sealed class McpResourceGuard(IOptions<HarborAuthOptions> options)
    : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
{
    public ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
    {
        if (context.IsRejected || context.Request is null)
        {
            return ValueTask.CompletedTask;
        }

        var expected = McpResource.Url(options.Value.PublicBaseUrl);
        var resources = context.Request.GetResources();
        if (resources.Length != 1 || !string.Equals(resources[0], expected, StringComparison.Ordinal))
        {
            context.Reject(Errors.InvalidTarget, "The resource is not the MCP resource.");
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class McpAudienceGuard(IOptions<HarborAuthOptions> options)
    : IOpenIddictServerHandler<ProcessSignInContext>
{
    public ValueTask HandleAsync(ProcessSignInContext context)
    {
        context.Principal?.SetResources(McpResource.Url(options.Value.PublicBaseUrl));
        return ValueTask.CompletedTask;
    }
}

public sealed class McpServerGate(HarborDbContext db) : IOpenIddictServerHandler<ProcessAuthenticationContext>
{
    public async ValueTask HandleAsync(ProcessAuthenticationContext context)
    {
        if (context.IsRejected)
        {
            return;
        }

        var principal = context.RefreshTokenPrincipal ?? context.AuthorizationCodePrincipal;
        if (principal is null)
        {
            return;
        }

        var reason = await McpClientGate.RejectReasonAsync(db, principal, context.CancellationToken);
        if (reason is not null)
        {
            context.Reject(Errors.InvalidGrant, reason);
        }
    }
}

public sealed class McpAccessGate(HarborDbContext db)
    : IOpenIddictValidationHandler<ValidationEvents.ProcessAuthenticationContext>
{
    public async ValueTask HandleAsync(ValidationEvents.ProcessAuthenticationContext context)
    {
        if (context.IsRejected || context.AccessTokenPrincipal is null)
        {
            return;
        }

        var reason = await McpClientGate.RejectReasonAsync(db, context.AccessTokenPrincipal, context.CancellationToken);
        McpClientGate.DetachIdle(db);
        if (reason is not null)
        {
            context.Reject(Errors.InvalidToken, reason);
        }
    }
}
