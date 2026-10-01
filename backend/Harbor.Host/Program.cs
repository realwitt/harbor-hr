using System.Threading.RateLimiting;
using Harbor.Host;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var ownerConnection = builder.Configuration["HARBOR_OWNER_CONNECTION"]
    ?? throw new InvalidOperationException("HARBOR_OWNER_CONNECTION is required.");
var appConnection = builder.Configuration["HARBOR_APP_CONNECTION"]
    ?? throw new InvalidOperationException("HARBOR_APP_CONNECTION is required.");

using var bootstrapLog = LoggerFactory.Create(logging => logging.AddConsole());
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

var app = builder.Build();

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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HarborDbContext>();
    _ = db.Model;
    _ = await db.Employees
        .OrderBy(e => e.Email)
        .Select(e => new { e.Role, e.HsaCoverage })
        .FirstOrDefaultAsync();
}

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var auth = scope.ServiceProvider.GetRequiredService<AuthWorkflow>();
    var path = await auth.CreateBootstrapInviteAsync(CancellationToken.None);
    if (path is not null)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Harbor.Invite");
        logger.LogInformation("Invite path {Path}", path);
    }
}

app.Run();
