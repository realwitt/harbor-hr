using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

[Collection("harbor_test")]
public sealed class McpPostgresTests(McpPostgresTests.McpApi fixture) : IClassFixture<McpPostgresTests.McpApi>
{
    private const string Resource = "http://localhost:5088/mcp";
    private const string Redirect = "http://127.0.0.1:9410/callback";

    [Fact]
    public async Task Register_public_client_returns_a_client_id_and_rejects_a_secret()
    {
        var client = fixture.CreateClient();
        var metadata = await client.GetAsync("/.well-known/oauth-protected-resource");
        var metadataBody = await metadata.Content.ReadAsStringAsync();
        Assert.True(metadata.StatusCode == HttpStatusCode.OK, metadataBody);
        using var metadataJson = JsonDocument.Parse(metadataBody);
        Assert.Equal(Resource, metadataJson.RootElement.GetProperty("resource").GetString());
        Assert.Equal("http://localhost:5088", metadataJson.RootElement.GetProperty("authorization_servers")[0].GetString());
        Assert.Contains("header", metadataJson.RootElement.GetProperty("bearer_methods_supported").EnumerateArray().Select(item => item.GetString()));

        var discovery = await client.GetAsync("/.well-known/oauth-authorization-server");
        var discoveryBody = await discovery.Content.ReadAsStringAsync();
        Assert.True(discovery.StatusCode == HttpStatusCode.OK, discoveryBody);
        using var discoveryJson = JsonDocument.Parse(discoveryBody);
        Assert.Contains("/connect/token", discoveryJson.RootElement.GetProperty("token_endpoint").GetString(), StringComparison.Ordinal);
        Assert.Contains("/connect/register", discoveryJson.RootElement.GetProperty("registration_endpoint").GetString(), StringComparison.Ordinal);
        Assert.Contains(
            "none",
            discoveryJson.RootElement.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(item => item.GetString()));

        var created = await client.PostAsync("/connect/register", Json(
            $$"""
            {
              "client_name": "Phase Five",
              "redirect_uris": ["{{Redirect}}"],
              "token_endpoint_auth_method": "none",
              "grant_types": ["authorization_code", "refresh_token"],
              "response_types": ["code"]
            }
            """));
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, createdBody);
        using var createdJson = JsonDocument.Parse(createdBody);
        var clientId = createdJson.RootElement.GetProperty("client_id").GetString();
        Assert.False(string.IsNullOrEmpty(clientId));
        Assert.False(createdJson.RootElement.TryGetProperty("client_secret", out _));

        await using var db = fixture.OpenApp();
        var links = await CountClients(db, clientId!);
        Assert.Equal(0, links);

        var secret = await client.PostAsync("/connect/register", Json(
            $$"""
            {
              "client_secret": "nope",
              "redirect_uris": ["{{Redirect}}"]
            }
            """));
        var secretBody = await secret.Content.ReadAsStringAsync();
        Assert.True(secret.StatusCode == HttpStatusCode.BadRequest, secretBody);
        Assert.Contains("invalid_client_metadata", secretBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authorize_while_mcp_is_off_returns_mcp_disabled()
    {
        var client = fixture.CreateClient();
        var clientId = await Register(client, "Phase Five Off");
        await using var db = fixture.OpenApp();
        var (_, session) = await ReadyEmployee(db);
        SignIn(client, session);
        var (verifier, challenge) = Pkce();
        var response = await client.GetAsync(Authorize(clientId, verifier, challenge));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("mcp_disabled", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Authorize_html_without_a_session_redirects_to_sign_in()
    {
        var client = fixture.CreateClient();
        var clientId = await Register(client, "Phase Five Sign In");
        var (_, challenge) = Pkce();
        using var request = new HttpRequestMessage(HttpMethod.Get, Authorize(clientId, "", challenge));
        request.Headers.Accept.ParseAdd("text/html");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? "";
        Assert.StartsWith("/sign-in?next=", location, StringComparison.Ordinal);
        var next = Uri.UnescapeDataString(location["/sign-in?next=".Length..]);
        Assert.StartsWith("/connect/authorize?", next, StringComparison.Ordinal);
        Assert.Contains("client_id=" + Uri.EscapeDataString(clientId), next, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Code_flow_access_token_audience_is_the_mcp_resource()
    {
        var client = fixture.CreateClient();
        var clientId = await Register(client, "Phase Five");
        await using var db = fixture.OpenApp();
        var (employeeId, session) = await ReadyEmployee(db);
        SignIn(client, session);
        await StepUp(db, employeeId, "mcp_on");
        var enabled = await client.PostAsync("/api/auth/mcp/enable", new StringContent("", Encoding.UTF8, "application/json"));
        var enabledBody = await enabled.Content.ReadAsStringAsync();
        Assert.True(enabled.StatusCode == HttpStatusCode.OK, enabledBody);

        var (verifier, challenge) = Pkce();
        var page = await client.GetAsync(Authorize(clientId, verifier, challenge));
        var html = await page.Content.ReadAsStringAsync();
        Assert.True(page.StatusCode == HttpStatusCode.OK, html);
        Assert.Contains("Phase Five", html, StringComparison.Ordinal);
        Assert.Contains("This client can act as you.", html, StringComparison.Ordinal);
        Assert.Contains("Allow access", html, StringComparison.Ordinal);
        Assert.Contains("class=\"mark\"", html, StringComparison.Ordinal);

        var accept = await client.PostAsync("/connect/authorize", Form(AcceptForm(clientId, challenge)));
        var acceptBody = await accept.Content.ReadAsStringAsync();
        Assert.True(accept.StatusCode == HttpStatusCode.Redirect, acceptBody);
        var location = accept.Headers.Location?.ToString() ?? "";
        var code = Query(location, "code");
        Assert.False(string.IsNullOrEmpty(code));

        var token = await Token(client, clientId, code!, verifier);
        var audiences = Audiences(token);
        Assert.Equal([Resource], audiences);
    }

    [Fact]
    public async Task Submit_leave_request_with_confirm_is_idempotent()
    {
        var client = fixture.CreateClient();
        var issued = await Issue(client);
        await using var db = fixture.OpenApp();
        var unpaid = await db.LeaveTypes.Where(row => row.Code == "unpaid").Select(row => row.Id).SingleAsync();
        var preview = await Call(client, issued.AccessToken, "preview_leave_request", new
        {
            leaveTypeId = unpaid,
            start = "2027-06-15",
            end = "2027-06-15",
            hoursPerDay = 8,
        });
        Assert.True(preview.IsError != true, preview.Text);
        using var previewJson = JsonDocument.Parse(preview.Text);
        var quoteId = previewJson.RootElement.GetProperty("quoteId").GetGuid();
        var key = "mcp-" + Guid.NewGuid().ToString("N");
        var first = await Call(client, issued.AccessToken, "submit_leave_request", new
        {
            quoteId,
            leaveTypeId = unpaid,
            start = "2027-06-15",
            end = "2027-06-15",
            hoursPerDay = 8,
            confirm = true,
            idempotencyKey = key,
        });
        Assert.True(first.IsError != true, first.Text);
        using var firstJson = JsonDocument.Parse(first.Text);
        var requestId = firstJson.RootElement.GetProperty("requestId").GetGuid();
        Assert.Equal("pending", firstJson.RootElement.GetProperty("status").GetString());

        var second = await Call(client, issued.AccessToken, "submit_leave_request", new
        {
            quoteId,
            leaveTypeId = unpaid,
            start = "2027-06-15",
            end = "2027-06-15",
            hoursPerDay = 8,
            confirm = true,
            idempotencyKey = key,
        });
        Assert.True(second.IsError != true, second.Text);
        using var secondJson = JsonDocument.Parse(second.Text);
        Assert.Equal(requestId, secondJson.RootElement.GetProperty("requestId").GetGuid());
        Assert.True(secondJson.RootElement.GetProperty("replay").GetBoolean());

        await using var check = fixture.OpenApp();
        var count = await check.LeaveRequests.CountAsync(row => row.EmployeeId == issued.EmployeeId && row.IdempotencyKey == key);
        Assert.Equal(1, count);
        var status = await check.LeaveRequests.Where(row => row.Id == requestId).Select(row => row.Status).SingleAsync();
        Assert.Equal(LeaveStatus.Pending, status);
        var confirmed = await check.ActionQuotes.Where(row => row.Id == quoteId).Select(row => row.ConfirmedAt).SingleAsync();
        Assert.NotNull(confirmed);
        var access = await CountAccess(check, issued.McpClientId, "submit_leave_request", "ok");
        Assert.InRange(access, 1, int.MaxValue);
    }

    [Fact]
    public async Task Next_Tuesday_is_rejected()
    {
        var client = fixture.CreateClient();
        var issued = await Issue(client);
        var projected = await Call(client, issued.AccessToken, "project_leave_balance", new { on = "next Tuesday" });
        Assert.True(projected.IsError == true, projected.Text);
        Assert.Contains("ISO", projected.Text, StringComparison.Ordinal);

        await using var db = fixture.OpenApp();
        var unpaid = await db.LeaveTypes.Where(row => row.Code == "unpaid").Select(row => row.Id).SingleAsync();
        var preview = await Call(client, issued.AccessToken, "preview_leave_request", new
        {
            leaveTypeId = unpaid,
            start = "next Tuesday",
            end = "2027-06-16",
            hoursPerDay = 8,
        });
        Assert.True(preview.IsError == true, preview.Text);
        Assert.Contains("ISO", preview.Text, StringComparison.Ordinal);
        var quotes = await db.ActionQuotes.CountAsync(row => row.EmployeeId == issued.EmployeeId);
        Assert.Equal(0, quotes);
    }

    [Fact]
    public async Task Revoked_client_token_fails()
    {
        var client = fixture.CreateClient();
        var issued = await Issue(client);
        var before = await Call(client, issued.AccessToken, "whoami", new { });
        Assert.True(before.IsError != true, before.Text);

        await using var db = fixture.OpenApp();
        await StepUp(db, issued.EmployeeId, "revoke_client");
        SignIn(client, issued.SessionId);
        var revoke = await client.PostAsync($"/api/auth/mcp/clients/{issued.McpClientId}/revoke", new StringContent("", Encoding.UTF8, "application/json"));
        var revokeBody = await revoke.Content.ReadAsStringAsync();
        Assert.True(revoke.StatusCode == HttpStatusCode.OK, revokeBody);

        var denied = await Send(client, "/mcp", Rpc("tools/call", new { name = "whoami", arguments = new { } }), issued.AccessToken);
        var deniedBody = await denied.Content.ReadAsStringAsync();
        Assert.True(denied.StatusCode == HttpStatusCode.Unauthorized, deniedBody);

        var refresh = await client.PostAsync("/connect/token", Form(
            "grant_type=refresh_token"
            + "&refresh_token=" + Uri.EscapeDataString(issued.RefreshToken)
            + "&client_id=" + Uri.EscapeDataString(issued.OAuthClientId)
            + "&resource=" + Uri.EscapeDataString(Resource)));
        var refreshBody = await refresh.Content.ReadAsStringAsync();
        Assert.True(refresh.StatusCode == HttpStatusCode.BadRequest, refreshBody);
    }

    [Fact]
    public async Task Tool_list_has_no_approve_tool()
    {
        var client = fixture.CreateClient();
        var issued = await Issue(client);
        var listed = await Send(client, "/mcp", Rpc("tools/list", new { }), issued.AccessToken);
        var body = await listed.Content.ReadAsStringAsync();
        Assert.True(listed.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(JsonPayload(body));
        var names = json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString() ?? "")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "cancel_leave_request",
                "get_leave_balance",
                "list_deduction_elections",
                "list_my_requests",
                "preview_deduction",
                "preview_leave_request",
                "project_leave_balance",
                "submit_deduction",
                "submit_leave_request",
                "whoami",
            ],
            names);
        Assert.DoesNotContain(names, name => name is "approve" or "deny" or "approve_leave_request" or "deny_leave_request");
    }

    private async Task<Issued> Issue(HttpClient client)
    {
        var oauth = await Register(client, "Phase Five");
        await using var db = fixture.OpenApp();
        var (employeeId, session) = await ReadyEmployee(db);
        SignIn(client, session);
        await StepUp(db, employeeId, "mcp_on");
        var enabled = await client.PostAsync("/api/auth/mcp/enable", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.True(enabled.IsSuccessStatusCode, await enabled.Content.ReadAsStringAsync());
        var (verifier, challenge) = Pkce();
        var accept = await client.PostAsync("/connect/authorize", Form(AcceptForm(oauth, challenge)));
        var acceptBody = await accept.Content.ReadAsStringAsync();
        Assert.True(accept.StatusCode == HttpStatusCode.Redirect, acceptBody);
        var code = Query(accept.Headers.Location?.ToString() ?? "", "code");
        Assert.False(string.IsNullOrEmpty(code));
        var tokenResponse = await client.PostAsync("/connect/token", Form(
            "grant_type=authorization_code"
            + "&code=" + Uri.EscapeDataString(code!)
            + "&redirect_uri=" + Uri.EscapeDataString(Redirect)
            + "&client_id=" + Uri.EscapeDataString(oauth)
            + "&code_verifier=" + Uri.EscapeDataString(verifier)
            + "&resource=" + Uri.EscapeDataString(Resource)));
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
        Assert.True(tokenResponse.StatusCode == HttpStatusCode.OK, tokenBody);
        using var json = JsonDocument.Parse(tokenBody);
        var access = json.RootElement.GetProperty("access_token").GetString()!;
        var refresh = json.RootElement.GetProperty("refresh_token").GetString()!;
        var mcpClientId = await db.McpClients.Where(row => row.EmployeeId == employeeId && row.RevokedAt == null)
            .Select(row => row.Id)
            .SingleAsync();
        return new Issued(employeeId, session, mcpClientId, oauth, access, refresh);
    }

    private static async Task<string> Register(HttpClient client, string name)
    {
        var response = await client.PostAsync("/connect/register", Json(
            $$"""
            {
              "client_name": "{{name}}",
              "redirect_uris": ["{{Redirect}}"],
              "grant_types": ["authorization_code", "refresh_token"],
              "response_types": ["code"],
              "token_endpoint_auth_method": "none"
            }
            """));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("client_id").GetString()!;
    }

    private static async Task<string> Token(HttpClient client, string clientId, string code, string verifier)
    {
        var response = await client.PostAsync("/connect/token", Form(
            "grant_type=authorization_code"
            + "&code=" + Uri.EscapeDataString(code)
            + "&redirect_uri=" + Uri.EscapeDataString(Redirect)
            + "&client_id=" + Uri.EscapeDataString(clientId)
            + "&code_verifier=" + Uri.EscapeDataString(verifier)
            + "&resource=" + Uri.EscapeDataString(Resource)));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<ToolBody> Call(HttpClient client, string accessToken, string name, object arguments)
    {
        var response = await Send(client, "/mcp", Rpc("tools/call", new { name, arguments }), accessToken);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(JsonPayload(body));
        var result = json.RootElement.GetProperty("result");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
        var isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new ToolBody(text, isError);
    }

    private static string AcceptForm(string clientId, string challenge) =>
        "decision=accept&response_type=code"
        + "&client_id=" + Uri.EscapeDataString(clientId)
        + "&redirect_uri=" + Uri.EscapeDataString(Redirect)
        + "&scope=offline_access"
        + "&state=phase5"
        + "&code_challenge=" + Uri.EscapeDataString(challenge)
        + "&code_challenge_method=S256"
        + "&resource=" + Uri.EscapeDataString(Resource);

    private static string Authorize(string clientId, string verifier, string challenge)
    {
        _ = verifier;
        return "/connect/authorize?response_type=code"
            + "&client_id=" + Uri.EscapeDataString(clientId)
            + "&redirect_uri=" + Uri.EscapeDataString(Redirect)
            + "&scope=offline_access"
            + "&state=phase5"
            + "&code_challenge=" + Uri.EscapeDataString(challenge)
            + "&code_challenge_method=S256"
            + "&resource=" + Uri.EscapeDataString(Resource);
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string[] Audiences(string jwt)
    {
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        var aud = json.RootElement.GetProperty("aud");
        if (aud.ValueKind == JsonValueKind.String)
        {
            return [aud.GetString()!];
        }

        return aud.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static string Query(string location, string name)
    {
        var query = location.Contains('?', StringComparison.Ordinal) ? location[(location.IndexOf('?') + 1)..] : location;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            if (split.Length == 2 && split[0] == name)
            {
                return Uri.UnescapeDataString(split[1]);
            }
        }

        return "";
    }

    private static async Task<(Guid EmployeeId, Guid SessionId)> ReadyEmployee(HarborDbContext db)
    {
        var id = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = id,
            Email = $"phase5-{id:N}@example.com",
            Name = "Phase Five",
            Role = EmployeeRole.Employee,
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

    private static async Task StepUp(HarborDbContext db, Guid employeeId, string action)
    {
        db.WebauthnChallenges.Add(new WebauthnChallenge
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            Kind = "step_up",
            Action = action,
            Challenge = [5, 5, 5, 5],
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(4),
            ConsumedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static void SignIn(HttpClient client, Guid sessionId)
    {
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookies.Name}={sessionId:D}");
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static StringContent Form(string body) => new(body, Encoding.UTF8, "application/x-www-form-urlencoded");

    private static StringContent Rpc(string method, object parameters) => Json(
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params = parameters,
        }));

    private static Task<HttpResponseMessage> Send(HttpClient client, string path, HttpContent content, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client.SendAsync(request);
    }

    private static string JsonPayload(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                return trimmed[5..].Trim();
            }
        }

        return body;
    }

    private static async Task<int> CountClients(HarborDbContext db, string clientId)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int
            from public.mcp_client as client
            join public."OpenIddictApplications" as app on app."Id" = client.oauth_application_id
            where app."ClientId" = @id
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = clientId;
        command.Parameters.Add(parameter);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<int> CountAccess(HarborDbContext db, Guid clientId, string tool, string outcome)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int
            from audit.access_event
            where action = 'mcp_tool'
              and tool_name = @tool
              and outcome = @outcome
              and client_id = @client
            """;
        var toolParameter = command.CreateParameter();
        toolParameter.ParameterName = "tool";
        toolParameter.Value = tool;
        command.Parameters.Add(toolParameter);
        var outcomeParameter = command.CreateParameter();
        outcomeParameter.ParameterName = "outcome";
        outcomeParameter.Value = outcome;
        command.Parameters.Add(outcomeParameter);
        var clientParameter = command.CreateParameter();
        clientParameter.ParameterName = "client";
        clientParameter.Value = clientId;
        command.Parameters.Add(clientParameter);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private sealed record Issued(
        Guid EmployeeId,
        Guid SessionId,
        Guid McpClientId,
        string OAuthClientId,
        string AccessToken,
        string RefreshToken);

    private sealed record ToolBody(string Text, bool? IsError);

    public sealed class McpApi : IAsyncLifetime
    {
        private IHost? _host;

        public async Task InitializeAsync()
        {
            Apply();
            var connection = AppConnection();
            var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .UseEnvironment("Testing")
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddRateLimiter(limiter =>
                        {
                            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                            limiter.AddPolicy("auth", http =>
                                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                                    {
                                        PermitLimit = 30,
                                        Window = TimeSpan.FromMinutes(1),
                                        QueueLimit = 0,
                                    }));
                        });
                        services.AddDbContext<HarborDbContext>(options =>
                        {
                            options.UseNpgsql(connection, HarborDbContext.MapEnums);
                            options.UseOpenIddict();
                        });
                        services.AddScoped<LeaveWorkflow>();
                        services.AddScoped<DeductionWorkflow>();
                        services.AddScoped<HarborBusiness>();
                        HarborOpenId.Add(services, new HarborAuthOptions());
                    });
                    web.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseRateLimiter();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseMiddleware<HarborSessionMiddleware>();
                        app.UseEndpoints(endpoints =>
                        {
                            var auth = endpoints.MapGroup("/api/auth").RequireRateLimiting("auth");
                            McpSwitch.Map(auth);
                            endpoints.MapOAuth();
                            endpoints.MapMcp("/mcp").RequireAuthorization(HarborOpenId.Policy);
                        });
                    });
                })
                .Build();
            await host.StartAsync();
            _host = host;
        }

        public HttpClient CreateClient()
        {
            var client = _host!.GetTestClient();
            client.BaseAddress = new Uri("http://localhost:5088");
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        public HarborDbContext OpenApp()
        {
            var options = new DbContextOptionsBuilder<HarborDbContext>()
                .UseNpgsql(AppConnection(), HarborDbContext.MapEnums)
                .Options;
            return new HarborDbContext(options);
        }

        public async Task DisposeAsync()
        {
            if (_host is null)
            {
                return;
            }

            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                _host.Dispose();
            }
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

            throw new InvalidOperationException(key + " is missing.");
        }
    }
}
