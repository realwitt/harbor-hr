using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Harbor;
using Harbor.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Harbor.Tests;

public class JoinRequestTests
{
    private const string Notify = "ew@eliaswitt.com";

    static JoinRequestTests()
    {
        Apply();
    }

    [Fact]
    public async Task Anonymous_request_stores_a_row_and_notifies_without_an_invite_link()
    {
        var email = NewEmail();
        var mail = new RecordingMailer();
        await using var api = await Start(mail);

        var response = await api.Client.PostAsync("/api/auth/join-requests", Json(
            $$"""
            { "name": "Ada Lovelace", "email": "{{email}}", "note": "Platform" }
            """));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var id = json.RootElement.GetProperty("id").GetGuid();
        Assert.False(json.RootElement.GetProperty("mailSent").GetBoolean());

        var notice = Assert.Single(mail.Sent);
        Assert.Equal(Notify, notice.To);
        Assert.Equal("Harbor join request", notice.Subject);
        Assert.Contains($"/admin/join-requests/{id}", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/invite/", notice.Text, StringComparison.Ordinal);

        await using var db = Open();
        var status = await db.JoinRequests.Where(row => row.Id == id).Select(row => row.Status).SingleAsync();
        Assert.Equal("pending", status);
    }

    [Fact]
    public async Task Second_request_for_the_same_email_is_pending()
    {
        var email = NewEmail();
        await using var api = await Start(new RecordingMailer());
        var first = await api.Client.PostAsync("/api/auth/join-requests", Json(Person(email)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await api.Client.PostAsync("/api/auth/join-requests", Json(Person(email)));
        var body = await second.Content.ReadAsStringAsync();
        Assert.True(second.StatusCode == HttpStatusCode.Conflict, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("request_pending", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Existing_employee_cannot_request_to_join()
    {
        await using var db = Open();
        var (employeeId, _) = await ReadyEmployee(db, EmployeeRole.Employee);
        var email = await db.Employees.Where(row => row.Id == employeeId).Select(row => row.Email).SingleAsync();
        await using var api = await Start(new RecordingMailer());

        var response = await api.Client.PostAsync("/api/auth/join-requests", Json(Person(email)));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("account_exists", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task List_requires_a_ready_hr_admin()
    {
        await using var api = await Start(new RecordingMailer());
        var anonymous = await api.Client.GetAsync("/api/auth/join-requests");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await using var db = Open();
        var (_, session) = await ReadyEmployee(db, EmployeeRole.Employee);
        SignIn(api.Client, session);
        var employee = await api.Client.GetAsync("/api/auth/join-requests");
        var body = await employee.Content.ReadAsStringAsync();
        Assert.True(employee.StatusCode == HttpStatusCode.Forbidden, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("not_authorized", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Admin_approve_sends_the_invite_link_to_the_requester()
    {
        var email = NewEmail();
        var mail = new RecordingMailer();
        await using var api = await Start(mail);
        var created = await api.Client.PostAsync("/api/auth/join-requests", Json(Person(email)));
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.OK, createdBody);
        using var createdJson = JsonDocument.Parse(createdBody);
        var id = createdJson.RootElement.GetProperty("id").GetGuid();

        await using var db = Open();
        var (_, session) = await ReadyEmployee(db, EmployeeRole.HrAdmin);
        SignIn(api.Client, session);
        var response = await api.Client.PostAsync($"/api/auth/join-requests/{id}/approve", Json(
            """
            { "role": "employee", "hiredOn": "2026-10-02", "jurisdiction": "US-NC", "timezone": "America/New_York" }
            """));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var path = json.RootElement.GetProperty("path").GetString();
        Assert.StartsWith("/invite/", path, StringComparison.Ordinal);
        Assert.False(json.RootElement.GetProperty("mailSent").GetBoolean());

        var notice = mail.Sent.Single(item => item.To == Notify);
        Assert.DoesNotContain("/invite/", notice.Text, StringComparison.Ordinal);
        var ready = mail.Sent.Single(item => item.To == email);
        Assert.Equal("Your Harbor account is ready", ready.Subject);
        Assert.Contains("http://localhost:5088" + path, ready.Text, StringComparison.Ordinal);

        await using var check = Open();
        var status = await check.JoinRequests.Where(row => row.Id == id).Select(row => row.Status).SingleAsync();
        Assert.Equal("approved", status);
    }

    [Fact]
    public async Task Dismissed_request_cannot_be_approved()
    {
        var email = NewEmail();
        await using var api = await Start(new RecordingMailer());
        var created = await api.Client.PostAsync("/api/auth/join-requests", Json(Person(email)));
        var createdBody = await created.Content.ReadAsStringAsync();
        using var createdJson = JsonDocument.Parse(createdBody);
        var id = createdJson.RootElement.GetProperty("id").GetGuid();

        await using var db = Open();
        var (_, session) = await ReadyEmployee(db, EmployeeRole.HrAdmin);
        SignIn(api.Client, session);
        var dismissed = await api.Client.PostAsync($"/api/auth/join-requests/{id}/dismiss", Json("{}"));
        Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);

        var approved = await api.Client.PostAsync($"/api/auth/join-requests/{id}/approve", Json(
            """
            { "role": "employee", "hiredOn": "2026-10-02", "jurisdiction": "US-NC" }
            """));
        var body = await approved.Content.ReadAsStringAsync();
        Assert.True(approved.StatusCode == HttpStatusCode.Conflict, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("not_pending", json.RootElement.GetProperty("error").GetString());
    }

    private static string NewEmail() => $"join-{Guid.NewGuid():N}@example.com";

    private static string Person(string email) =>
        $$"""
        { "name": "Join Person", "email": "{{email}}" }
        """;

    private static async Task<(Guid EmployeeId, Guid SessionId)> ReadyEmployee(HarborDbContext db, EmployeeRole role)
    {
        var id = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = id,
            Email = $"join-admin-{id:N}@example.com",
            Name = "Join Admin",
            Role = role,
            Timezone = "America/New_York",
            Jurisdiction = "US-NC",
            HiredOn = new DateOnly(2024, 1, 15),
            RecoverySavedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var sessionId = Guid.NewGuid();
        db.AppSessions.Add(new AppSession
        {
            Id = sessionId,
            EmployeeId = id,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
        });
        await db.SaveChangesAsync();
        return (id, sessionId);
    }

    private static void SignIn(HttpClient client, Guid sessionId)
    {
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"harbor_session={sessionId:D}");
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<RunningApi> Start(RecordingMailer mail)
    {
        var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseEnvironment("Testing")
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddDbContext<HarborDbContext>(options =>
                    {
                        options.UseNpgsql(AppConnection(), HarborDbContext.MapEnums);
                    });
                    services.Configure<HarborAuthOptions>(options =>
                    {
                        options.PublicBaseUrl = "http://localhost:5088";
                        options.JoinNotifyEmail = Notify;
                        options.RelyingPartyId = "localhost";
                        options.ServerName = "Harbor";
                        options.Origins = ["http://localhost:5088"];
                        options.CloudflareAccountId = "";
                        options.CloudflareEmailToken = "";
                    });
                    services.AddHttpClient("turnstile", client => client.Timeout = TimeSpan.FromSeconds(5));
                    services.AddSingleton<IMailer>(mail);
                    services.AddScoped<AuthWorkflow>();
                    services.AddFido2(config =>
                    {
                        config.RPID = "localhost";
                        config.RPName = "Harbor";
                        config.Origins = new HashSet<string>(["http://localhost:5088"], StringComparer.Ordinal);
                        config.TimestampDriftTolerance = 300_000;
                    });
                    services.AddRateLimiter(limiter =>
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
                });
                web.Configure(app =>
                {
                    app.UseDeveloperExceptionPage();
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseMiddleware<HarborSessionMiddleware>();
                    app.UseEndpoints(endpoints => endpoints.MapAuth());
                });
            })
            .Build();
        await host.StartAsync();
        var client = host.GetTestClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        return new RunningApi(host, client);
    }

    private sealed class RecordingMailer : IMailer
    {
        public List<OutboundMail> Sent { get; } = [];

        public Task<bool> SendAsync(OutboundMail mail, CancellationToken ct)
        {
            Sent.Add(mail);
            return Task.FromResult(false);
        }
    }

    private sealed class RunningApi(IHost host, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await host.StopAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                host.Dispose();
            }
        }
    }

    private static HarborDbContext Open()
    {
        var options = new DbContextOptionsBuilder<HarborDbContext>()
            .UseNpgsql(AppConnection(), HarborDbContext.MapEnums)
            .Options;
        return new HarborDbContext(options);
    }

    private static void Apply()
    {
        var owner = ReadSecret("HARBOR_OWNER_PASSWORD");
        var connection = $"Host=127.0.0.1;Port=5432;Database=harbor_test;Username=harbor;Password={owner};Timeout=15";
        using var logger = LoggerFactory.Create(_ => { });
        try
        {
            MarkAppliedSeed(connection);
            SqlMigrator.Apply(connection, "/Users/ewitt/hr-app/db", logger.CreateLogger("sql"));
        }
        catch (Exception ex)
        {
            var text = ex.ToString();
            if (text.Contains("Password=", StringComparison.Ordinal) || text.Contains(owner, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SQL migration failed.");
            }

            throw;
        }
    }

    private static void MarkAppliedSeed(string connection)
    {
        using var db = new NpgsqlConnection(connection);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText =
            """
            insert into meta.schema_migration (filename)
            select pending.filename
            from (values ('004_seed.sql')) as pending(filename)
            where exists (
                select 1
                from information_schema.tables
                where table_schema = 'public' and table_name = 'employee'
            )
            and not exists (
                select 1 from meta.schema_migration as applied
                where applied.filename = pending.filename
            );
            """;
        command.ExecuteNonQuery();
    }

    private static string AppConnection()
    {
        var password = ReadSecret("HARBOR_APP_PASSWORD");
        return $"Host=127.0.0.1;Port=5432;Database=harbor_test;Username=harbor_app;Password={password};Timeout=15";
    }

    private static string ReadSecret(string key)
    {
        foreach (var line in File.ReadAllLines("/Users/ewitt/hr-app/.secrets/dev-db.env"))
        {
            var trimmed = line.Trim();
            var split = trimmed.IndexOf('=');
            if (split <= 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed[..split].Trim() == key)
            {
                var value = trimmed[(split + 1)..].Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        throw new InvalidOperationException($"{key} is missing.");
    }
}
