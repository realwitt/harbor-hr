using System.Threading.RateLimiting;
using Harbor.Host;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

var seedDev = args.Contains("--seed-dev");
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--seed-dev").ToArray());
if (seedDev)
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
}

var ownerConnection = builder.Configuration["HARBOR_OWNER_CONNECTION"]
    ?? throw new InvalidOperationException("HARBOR_OWNER_CONNECTION is required.");
var appConnection = builder.Configuration["HARBOR_APP_CONNECTION"]
    ?? throw new InvalidOperationException("HARBOR_APP_CONNECTION is required.");

using var bootstrapLog = LoggerFactory.Create(logging =>
{
    logging.AddConsole(options =>
    {
        if (seedDev)
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        }
    });
    if (seedDev)
    {
        logging.SetMinimumLevel(LogLevel.Warning);
    }
});
SqlMigrator.Apply(
    ownerConnection,
    SqlMigrator.FindSqlDirectory(),
    bootstrapLog.CreateLogger("SqlMigrator"));

builder.Services.Configure<HarborAuthOptions>(builder.Configuration.GetSection("Harbor"));
var harbor = builder.Configuration.GetSection("Harbor").Get<HarborAuthOptions>() ?? new HarborAuthOptions();

builder.Services.AddDbContext<HarborDbContext>(options =>
{
    options.UseNpgsql(appConnection, HarborDbContext.MapEnums);
    options.UseOpenIddict();
});
HarborOpenId.Add(builder.Services, harbor);
builder.Services.AddScoped<LeaveWorkflow>();
builder.Services.AddScoped<DeductionWorkflow>();
builder.Services.AddScoped<AccrualPoster>();
builder.Services.AddScoped<AuthWorkflow>();
builder.Services.AddScoped<HarborBusiness>();
builder.Services.AddHttpClient("turnstile", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient("cloudflare-email", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<IMailer, CloudflareMailer>();
builder.Services.AddFido2(config =>
{
    config.RPID = harbor.RelyingPartyId;
    config.RPName = harbor.ServerName;
    config.Origins = harbor.Origins.ToHashSet(StringComparer.Ordinal);
    config.TimestampDriftTolerance = 300_000;
});
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("auth", http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});
builder.Services.AddHostedService<AccrualPosterService>();
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(5);
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Caddy is the only caller, and it is not a loopback proxy.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.Use(async (http, next) =>
{
    http.Response.OnStarting(() =>
    {
        if (http.Response.StatusCode == StatusCodes.Status401Unauthorized
            && http.Request.Path.StartsWithSegments("/mcp"))
        {
            var metadata = McpResource.BaseUrl(harbor.PublicBaseUrl).TrimEnd('/')
                + "/.well-known/oauth-protected-resource";
            http.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{metadata}\"";
        }

        return Task.CompletedTask;
    });
    await next();
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<HarborSessionMiddleware>();

app.MapGet("/api/healthz", () => Results.Json(new { status = "ok" }));
app.MapAuth();
app.MapOAuth();
app.MapBusiness();
app.MapMcp("/mcp").RequireAuthorization(HarborOpenId.Policy);

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HarborDbContext>();
    _ = db.Model;
    _ = await db.Employees
        .OrderBy(e => e.Email)
        .Select(e => new { e.Role, e.HsaCoverage })
        .FirstOrDefaultAsync();
    var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
    await OAuthEndpoints.EnsureGrokClientAsync(applications, harbor.PublicBaseUrl, CancellationToken.None);
}

if (seedDev)
{
    if (!app.Environment.IsDevelopment())
    {
        Console.Error.WriteLine("The dev invite runs only in Development.");
        Environment.ExitCode = 1;
        await app.DisposeAsync();
        return;
    }

    string? path;
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var auth = scope.ServiceProvider.GetRequiredService<AuthWorkflow>();
        path = await auth.CreateDevInviteAsync(CancellationToken.None);
    }

    if (path is null)
    {
        Console.Error.WriteLine("The dev invite was not created. ew@eliaswitt.com is missing.");
        Environment.ExitCode = 1;
        await app.DisposeAsync();
        return;
    }

    var web = Environment.GetEnvironmentVariable("HARBOR_DEV_WEB_ORIGIN");
    if (string.IsNullOrWhiteSpace(web))
    {
        web = "http://localhost:5190";
    }

    Console.WriteLine("Open this link to create a passkey for ew@eliaswitt.com.");
    Console.WriteLine(web.TrimEnd('/') + path);
    await app.DisposeAsync();
    return;
}

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var auth = scope.ServiceProvider.GetRequiredService<AuthWorkflow>();
    var path = await auth.CreateBootstrapInviteAsync(CancellationToken.None);
    if (path is not null)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Harbor.Invite");
        logger.LogInformation("Invite path {Path}", path);
    }
}

app.Run();
