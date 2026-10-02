using System.Net;
using System.Text;
using System.Text.Json;
using Harbor;
using Harbor.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harbor.Tests;

public class BusinessApiTests
{
    [Fact]
    public async Task Ready_employee_can_preview_leave()
    {
        await using var db = Open();
        var (_, session) = await ReadyEmployee(db);
        var unpaid = await db.LeaveTypes.Where(row => row.Code == "unpaid").Select(row => row.Id).SingleAsync();
        await using var api = await Start();
        SignIn(api.Client, session);

        var types = await api.Client.GetAsync("/api/leave/types");
        var typesBody = await types.Content.ReadAsStringAsync();
        Assert.True(types.StatusCode == HttpStatusCode.OK, typesBody);
        using var typesJson = JsonDocument.Parse(typesBody);
        var pto = typesJson.RootElement.EnumerateArray().Single(row => row.GetProperty("code").GetString() == "pto");
        Assert.Equal("accrued", pto.GetProperty("model").GetString());

        var response = await api.Client.PostAsync("/api/leave/preview", Json(
            $$"""
            {
              "leaveTypeId": "{{unpaid}}",
              "start": "2026-11-02",
              "end": "2026-11-02",
              "hoursPerDay": 8,
              "adminOverride": false
            }
            """));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        Assert.NotEqual(Guid.Empty, json.RootElement.GetProperty("quoteId").GetGuid());
    }

    [Fact]
    public async Task Submit_without_idempotency_key_is_400()
    {
        await using var db = Open();
        var (_, session) = await ReadyEmployee(db);
        var unpaid = await db.LeaveTypes.Where(row => row.Code == "unpaid").Select(row => row.Id).SingleAsync();
        await using var api = await Start();
        SignIn(api.Client, session);

        var response = await api.Client.PostAsync("/api/leave/submit", Json(
            $$"""
            {
              "quoteId": "{{Guid.NewGuid()}}",
              "leaveTypeId": "{{unpaid}}",
              "start": "2026-11-03",
              "end": "2026-11-03",
              "hoursPerDay": 8,
              "adminOverride": false
            }
            """));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        Assert.Contains("idempotency", body, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("invalid", json.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Employee_cannot_post_a_blackout()
    {
        await using var db = Open();
        var (_, session) = await ReadyEmployee(db);
        var day = new DateOnly(2026, 3, 3);
        var before = await db.BlackoutDates.CountAsync(row => row.OnDate == day);
        await using var api = await Start();
        SignIn(api.Client, session);

        var response = await api.Client.PostAsync("/api/admin/blackouts", Json(
            """
            { "on": "2026-03-03", "reason": "phase 4" }
            """));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("not_authorized", json.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());

        await using var check = Open();
        var after = await check.BlackoutDates.CountAsync(row => row.OnDate == day);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Employee_cannot_list_the_directory()
    {
        await using var db = Open();
        var (_, session) = await ReadyEmployee(db);
        await using var api = await Start();
        SignIn(api.Client, session);

        var response = await api.Client.GetAsync("/api/admin/employees");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("not_authorized", json.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Hr_admin_lists_people_without_birth_dates()
    {
        await using var db = Open();
        var (_, session) = await ReadyEmployee(db, EmployeeRole.HrAdmin);
        await using var api = await Start();
        SignIn(api.Client, session);

        var response = await api.Client.GetAsync("/api/admin/employees");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.DoesNotContain("bornOn", body, StringComparison.Ordinal);
        Assert.DoesNotContain("born_on", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        var sam = json.RootElement.EnumerateArray().Single(row => row.GetProperty("email").GetString() == "sam.reyes@example.com");
        Assert.Equal("Sam Reyes", sam.GetProperty("name").GetString());
        Assert.Equal("employee", sam.GetProperty("role").GetString());
        Assert.False(sam.GetProperty("hdhpEligible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sam.GetProperty("hsaCoverage").ValueKind);
        Assert.Equal("Elias Witt", sam.GetProperty("managerName").GetString());
        var elias = json.RootElement.EnumerateArray().Single(row => row.GetProperty("email").GetString() == "ew@eliaswitt.com");
        Assert.Equal("hr_admin", elias.GetProperty("role").GetString());
        Assert.True(elias.GetProperty("hdhpEligible").GetBoolean());
        Assert.Equal("self", elias.GetProperty("hsaCoverage").GetString());
    }

    [Fact]
    public async Task Team_calendar_hides_people_who_do_not_report_to_Elias()
    {
        await using var db = Open();
        var elias = await db.Employees.SingleAsync(row => row.Email == "ew@eliaswitt.com");
        var sam = await db.Employees.SingleAsync(row => row.Email == "sam.reyes@example.com");
        Assert.True(await db.ManagerLinks.AnyAsync(link =>
            link.EmployeeId == sam.Id && link.ManagerId == elias.Id && link.EndedOn == null));
        if (elias.RecoverySavedAt is null)
        {
            elias.RecoverySavedAt = DateTimeOffset.UtcNow;
        }

        var stranger = Guid.NewGuid();
        var strangerName = "Stranger " + stranger.ToString("N")[..8];
        db.Employees.Add(new Employee
        {
            Id = stranger,
            Email = $"stranger-{stranger:N}@example.com",
            Name = strangerName,
            Jurisdiction = "US-NC",
            HiredOn = new DateOnly(2024, 1, 15),
        });
        var session = Guid.NewGuid();
        db.AppSessions.Add(new AppSession
        {
            Id = session,
            EmployeeId = elias.Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
        });
        await db.SaveChangesAsync();

        var day = await FreeDay(db, elias.Id, sam.Id, stranger);
        var unpaid = await db.LeaveTypes.Where(row => row.Code == "unpaid").Select(row => row.Id).SingleAsync();
        await AddDay(db, elias.Id, unpaid, day);
        await AddDay(db, sam.Id, unpaid, day);
        await AddDay(db, stranger, unpaid, day);

        await using var api = await Start();
        SignIn(api.Client, session);
        var response = await api.Client.GetAsync($"/api/team/calendar?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var ids = json.RootElement.EnumerateArray().Select(row => row.GetProperty("employeeId").GetGuid()).ToHashSet();
        Assert.Contains(elias.Id, ids);
        Assert.Contains(sam.Id, ids);
        Assert.DoesNotContain(stranger, ids);
        Assert.Contains("Sam Reyes", body, StringComparison.Ordinal);
        Assert.DoesNotContain(strangerName, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_read_requires_a_consumed_step_up()
    {
        await using var db = Open();
        var (admin, session) = await ReadyEmployee(db, EmployeeRole.HrAdmin);
        await using var api = await Start();
        SignIn(api.Client, session);

        var blocked = await api.Client.GetAsync("/api/admin/audit");
        var blockedBody = await blocked.Content.ReadAsStringAsync();
        Assert.True(blocked.StatusCode == HttpStatusCode.Forbidden, blockedBody);
        using var blockedJson = JsonDocument.Parse(blockedBody);
        Assert.Equal("step_up_required", blockedJson.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());

        db.WebauthnChallenges.Add(new WebauthnChallenge
        {
            Id = Guid.NewGuid(),
            EmployeeId = admin,
            Kind = "step_up",
            Action = "read_audit",
            Challenge = [9, 9, 9, 9],
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(4),
            ConsumedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var allowed = await api.Client.GetAsync("/api/admin/audit");
        var body = await allowed.Content.ReadAsStringAsync();
        Assert.True(allowed.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var records = json.RootElement.GetProperty("records");
        var access = json.RootElement.GetProperty("accessEvents");
        Assert.Equal(JsonValueKind.Array, records.ValueKind);
        Assert.Equal(JsonValueKind.Array, access.ValueKind);
        Assert.True(records.GetArrayLength() <= 100);
        Assert.True(access.GetArrayLength() <= 100);
        Assert.Contains("read_audit", body, StringComparison.Ordinal);
    }

    private static async Task AddDay(HarborDbContext db, Guid employeeId, Guid leaveTypeId, DateOnly day)
    {
        var quoteId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        db.ActionQuotes.Add(new ActionQuote
        {
            Id = quoteId,
            EmployeeId = employeeId,
            Kind = QuoteKind.Leave,
            Payload = """{"action":"submit"}""",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        db.LeaveRequests.Add(new LeaveRequest
        {
            Id = requestId,
            EmployeeId = employeeId,
            LeaveTypeId = leaveTypeId,
            Status = LeaveStatus.Pending,
            IdempotencyKey = "cal-" + requestId.ToString("N"),
            QuoteId = quoteId,
        });
        await db.SaveChangesAsync();
        db.LeaveRequestDays.Add(new LeaveRequestDay
        {
            RequestId = requestId,
            OnDate = day,
            Hours = 8,
            EmployeeId = employeeId,
            Status = LeaveStatus.Pending,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<DateOnly> FreeDay(HarborDbContext db, params Guid[] employees)
    {
        var used = await db.LeaveRequestDays.AsNoTracking()
            .Where(day => employees.Contains(day.EmployeeId))
            .Select(day => new { day.EmployeeId, day.OnDate })
            .ToListAsync();
        var taken = used.Select(day => (day.EmployeeId, day.OnDate)).ToHashSet();
        for (var day = new DateOnly(2027, 2, 1); day < new DateOnly(2028, 1, 1); day = day.AddDays(1))
        {
            if (employees.All(id => !taken.Contains((id, day))))
            {
                return day;
            }
        }

        throw new InvalidOperationException("No free day.");
    }

    private static async Task<(Guid EmployeeId, Guid SessionId)> ReadyEmployee(HarborDbContext db, EmployeeRole role = EmployeeRole.Employee)
    {
        var id = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = id,
            Email = $"phase4-{id:N}@example.com",
            Name = "Phase Four",
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
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookies.Name}={sessionId:D}");
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<RunningApi> Start()
    {
        var connection = AppConnection();
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
                        options.UseNpgsql(connection, HarborDbContext.MapEnums);
                    });
                    services.AddScoped<LeaveWorkflow>();
                    services.AddScoped<DeductionWorkflow>();
                    services.AddScoped<HarborBusiness>();
                });
                web.Configure(app =>
                {
                    app.UseDeveloperExceptionPage();
                    app.UseRouting();
                    app.UseMiddleware<HarborSessionMiddleware>();
                    app.UseEndpoints(endpoints => endpoints.MapBusiness());
                });
            })
            .Build();
        await host.StartAsync();
        var client = host.GetTestClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        return new RunningApi(host, client);
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

    private static string AppConnection()
    {
        string? password = null;
        foreach (var line in File.ReadAllLines("/Users/ewitt/hr-app/.secrets/dev-db.env"))
        {
            var trimmed = line.Trim();
            var split = trimmed.IndexOf('=');
            if (split <= 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed[..split].Trim() == "HARBOR_APP_PASSWORD")
            {
                password = trimmed[(split + 1)..].Trim();
            }
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("HARBOR_APP_PASSWORD is missing.");
        }

        return $"Host=127.0.0.1;Port=5432;Database=harbor_test;Username=harbor_app;Password={password};Timeout=15";
    }
}
