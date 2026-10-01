using Harbor;
using Harbor.Host;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Tests;

public class AuthPostgresTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly AsOf = new(2026, 8, 1);

    [Fact]
    public async Task One_passkey_cannot_submit_until_recovery_is_saved()
    {
        await using var db = Open();
        var employee = await CreateEmployee(db, recoverySavedAt: null);
        await AddPasskey(db, employee);
        var unpaid = await UnpaidType(db);
        var workflow = new LeaveWorkflow(db);
        var preview = await workflow.PreviewAsync(Leave(employee, unpaid), CancellationToken.None);
        Assert.True(preview.Succeeded);
        var previewValue = preview.Value ?? throw new InvalidOperationException("The preview is missing.");
        var quote = await db.ActionQuotes.SingleAsync(row => row.Id == previewValue.QuoteId);
        quote.ConfirmedAt = Now;
        await db.SaveChangesAsync();

        var blocked = await workflow.SubmitAsync(Submit(employee, unpaid, previewValue.QuoteId, "blocked"), CancellationToken.None);

        Assert.False(blocked.Succeeded);
        Assert.Contains(blocked.Errors, error => error.Code == "account_not_ready");
        await using var before = Open();
        Assert.Equal(0, await before.LeaveRequests.CountAsync(row => row.EmployeeId == employee));

        var tracked = await db.Employees.SingleAsync(row => row.Id == employee);
        tracked.RecoverySavedAt = Now;
        await db.SaveChangesAsync();
        var saved = await workflow.SubmitAsync(Submit(employee, unpaid, previewValue.QuoteId, "ready"), CancellationToken.None);

        Assert.True(saved.Succeeded);
        await using var after = Open();
        Assert.Equal(1, await after.LeaveRequests.CountAsync(row => row.EmployeeId == employee));
    }

    [Fact]
    public async Task The_only_passkey_cannot_be_removed()
    {
        await using var db = Open();
        var alone = await CreateEmployee(db, recoverySavedAt: null);
        var aloneKey = await AddPasskey(db, alone);
        var withCodes = await CreateEmployee(db, recoverySavedAt: null);
        var withCodesKey = await AddPasskey(db, withCodes);
        db.RecoveryCodes.Add(new RecoveryCode
        {
            Id = Guid.NewGuid(),
            EmployeeId = withCodes,
            CodeHash = RecoverySignIn.Hash("alone-code"),
        });
        await db.SaveChangesAsync();

        var blocked = await PasskeyRemoval.RemoveAsync(db, alone, aloneKey);
        var blockedWithCodes = await PasskeyRemoval.RemoveAsync(db, withCodes, withCodesKey);

        Assert.False(blocked.Succeeded);
        Assert.Equal("last_passkey", blocked.Errors[0].Code);
        Assert.False(blockedWithCodes.Succeeded);
        Assert.Equal("last_passkey", blockedWithCodes.Errors[0].Code);
        await using var check = Open();
        Assert.Equal(1, await check.WebauthnCredentials.CountAsync(row => row.EmployeeId == alone));
        Assert.Equal(1, await check.WebauthnCredentials.CountAsync(row => row.EmployeeId == withCodes));
    }

    [Fact]
    public async Task A_recovery_code_works_once()
    {
        await using var db = Open();
        var employee = await CreateEmployee(db, recoverySavedAt: null);
        var email = await db.Employees.Where(row => row.Id == employee).Select(row => row.Email).SingleAsync();
        const string code = "AB12-CD34";
        db.RecoveryCodes.Add(new RecoveryCode
        {
            Id = Guid.NewGuid(),
            EmployeeId = employee,
            CodeHash = RecoverySignIn.Hash(code),
        });
        await db.SaveChangesAsync();

        var first = await RecoverySignIn.AssertAsync(db, email, code);
        var second = await RecoverySignIn.AssertAsync(db, email, code);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        await using var check = Open();
        var stored = await check.RecoveryCodes.SingleAsync(row => row.EmployeeId == employee);
        Assert.NotNull(stored.UsedAt);
        Assert.Equal(1, await check.AppSessions.CountAsync(row => row.EmployeeId == employee));
    }

    private static LeavePreviewRequest Leave(Guid employee, Guid leaveType) => new()
    {
        EmployeeId = employee,
        ActorId = employee,
        LeaveTypeId = leaveType,
        Start = "2026-08-12",
        End = "2026-08-12",
        HoursPerDay = 8,
        Channel = "web",
        AsOf = AsOf,
        Now = Now,
        RequestId = "phase3",
    };

    private static LeaveSubmitRequest Submit(Guid employee, Guid leaveType, Guid quoteId, string key) => new()
    {
        EmployeeId = employee,
        ActorId = employee,
        QuoteId = quoteId,
        IdempotencyKey = key + "-" + Guid.NewGuid().ToString("N"),
        LeaveTypeId = leaveType,
        Start = "2026-08-12",
        End = "2026-08-12",
        HoursPerDay = 8,
        Channel = "web",
        Confirm = true,
        AsOf = AsOf,
        Now = Now,
        RequestId = "phase3",
    };

    private static async Task<Guid> CreateEmployee(HarborDbContext db, DateTimeOffset? recoverySavedAt)
    {
        var id = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = id,
            Email = $"phase3-{id:N}@example.com",
            Name = "Phase Three",
            Jurisdiction = "US-NC",
            HiredOn = new DateOnly(2024, 1, 15),
            RecoverySavedAt = recoverySavedAt,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> AddPasskey(HarborDbContext db, Guid employeeId)
    {
        var id = Guid.NewGuid();
        db.WebauthnCredentials.Add(new WebauthnCredential
        {
            Id = id,
            EmployeeId = employeeId,
            CredentialId = Guid.NewGuid().ToByteArray(),
            PublicKey = [1, 2, 3, 4],
            SignCount = 0,
            Transports = [],
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> UnpaidType(HarborDbContext db)
    {
        return await db.LeaveTypes.Where(row => row.Code == "unpaid").Select(row => row.Id).SingleAsync();
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
