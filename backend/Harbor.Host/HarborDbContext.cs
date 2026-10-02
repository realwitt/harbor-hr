using System.Text;
using Harbor;
using Microsoft.EntityFrameworkCore;
using Npgsql.NameTranslation;

namespace Harbor.Host;

// SQL in db/ is the schema source. Do not call EnsureCreated or Migrate.
public sealed class HarborDbContext(DbContextOptions<HarborDbContext> options) : DbContext(options)
{
    private static readonly NpgsqlSnakeCaseNameTranslator EnumNames = new();

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<ManagerLink> ManagerLinks => Set<ManagerLink>();
    public DbSet<PayPeriod> PayPeriods => Set<PayPeriod>();
    public DbSet<CompanyHoliday> CompanyHolidays => Set<CompanyHoliday>();
    public DbSet<LeaveType> LeaveTypes => Set<LeaveType>();
    public DbSet<LeaveLedger> LeaveLedgers => Set<LeaveLedger>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<LeaveRequestDay> LeaveRequestDays => Set<LeaveRequestDay>();
    public DbSet<BlackoutDate> BlackoutDates => Set<BlackoutDate>();
    public DbSet<ActionQuote> ActionQuotes => Set<ActionQuote>();
    public DbSet<DeductionCap> DeductionCaps => Set<DeductionCap>();
    public DbSet<TaxYearParam> TaxYearParams => Set<TaxYearParam>();
    public DbSet<StateIncomeTax> StateIncomeTaxes => Set<StateIncomeTax>();
    public DbSet<DeductionElection> DeductionElections => Set<DeductionElection>();
    public DbSet<PayrollPosting> PayrollPostings => Set<PayrollPosting>();
    public DbSet<WithholdingProfile> WithholdingProfiles => Set<WithholdingProfile>();
    public DbSet<WebauthnCredential> WebauthnCredentials => Set<WebauthnCredential>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<JoinRequest> JoinRequests => Set<JoinRequest>();
    public DbSet<AppSession> AppSessions => Set<AppSession>();
    public DbSet<WebauthnChallenge> WebauthnChallenges => Set<WebauthnChallenge>();
    public DbSet<McpClient> McpClients => Set<McpClient>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresEnum<EmployeeRole>("public", "employee_role", EnumNames);
        modelBuilder.HasPostgresEnum<GrantModel>("public", "grant_model", EnumNames);
        modelBuilder.HasPostgresEnum<LeaveStatus>("public", "leave_status", EnumNames);
        modelBuilder.HasPostgresEnum<LedgerKind>("public", "ledger_kind", EnumNames);
        modelBuilder.HasPostgresEnum<DeductionKind>("public", "deduction_kind", EnumNames);
        modelBuilder.HasPostgresEnum<ElectionStatus>("public", "election_status", EnumNames);
        modelBuilder.HasPostgresEnum<QuoteKind>("public", "quote_kind", EnumNames);
        modelBuilder.HasPostgresEnum<HsaCoverage>("public", "hsa_coverage", EnumNames);

        modelBuilder.Entity<Employee>().HasKey(e => e.Id);
        modelBuilder.Entity<ManagerLink>().HasKey(e => new { e.EmployeeId, e.EffectiveOn });
        modelBuilder.Entity<PayPeriod>().HasKey(e => e.Id);
        modelBuilder.Entity<CompanyHoliday>().HasKey(e => e.OnDate);
        modelBuilder.Entity<LeaveType>().HasKey(e => e.Id);
        modelBuilder.Entity<LeaveLedger>().HasKey(e => e.Id);
        modelBuilder.Entity<LeaveRequest>().HasKey(e => e.Id);
        modelBuilder.Entity<LeaveRequestDay>().HasKey(e => new { e.RequestId, e.OnDate });
        modelBuilder.Entity<BlackoutDate>().HasKey(e => e.OnDate);
        modelBuilder.Entity<ActionQuote>().HasKey(e => e.Id);
        modelBuilder.Entity<DeductionCap>().HasKey(e => new { e.TaxYear, e.Kind, e.Coverage });
        modelBuilder.Entity<TaxYearParam>().HasKey(e => e.TaxYear);
        modelBuilder.Entity<StateIncomeTax>().HasKey(e => new { e.TaxYear, e.StateCode });
        modelBuilder.Entity<DeductionElection>().HasKey(e => e.Id);
        modelBuilder.Entity<PayrollPosting>().HasKey(e => e.Id);
        modelBuilder.Entity<WithholdingProfile>().HasKey(e => e.EmployeeId);
        modelBuilder.Entity<WebauthnCredential>().HasKey(e => e.Id);
        modelBuilder.Entity<RecoveryCode>().HasKey(e => e.Id);
        modelBuilder.Entity<Invite>().HasKey(e => e.Id);
        modelBuilder.Entity<JoinRequest>().HasKey(e => e.Id);
        modelBuilder.Entity<AppSession>().HasKey(e => e.Id);
        modelBuilder.Entity<WebauthnChallenge>().HasKey(e => e.Id);
        modelBuilder.Entity<McpClient>().HasKey(e => e.Id);

        modelBuilder.Entity<AppSession>().Property(e => e.Id).ValueGeneratedNever();

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            // OpenIddict maps its own table and column names. Do not snake-case them.
            if (entity.ClrType.Namespace?.StartsWith("OpenIddict", StringComparison.Ordinal) == true)
            {
                foreach (var property in entity.GetProperties())
                {
                    if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                    {
                        property.SetColumnType("timestamp with time zone");
                    }
                }

                continue;
            }

            entity.SetTableName(ToSnake(entity.ClrType.Name));
            entity.SetSchema("public");

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnake(property.Name));

                if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                {
                    property.SetPrecision(8);
                    property.SetScale(2);
                }

                if (property.Name is "Payload" or "FederalBrackets" or "W4")
                {
                    property.SetColumnType("jsonb");
                }

                if (property.Name == "Id"
                    && property.ClrType == typeof(Guid)
                    && entity.ClrType != typeof(AppSession))
                {
                    property.SetDefaultValueSql("gen_random_uuid()");
                    property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd;
                }

                if (property.Name is "CreatedAt" or "PostedAt" or "UpdatedAt")
                {
                    property.SetDefaultValueSql("now()");
                    property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd;
                }
            }
        }

        modelBuilder.Entity<LeaveRequest>()
            .HasOne<ActionQuote>()
            .WithMany()
            .HasForeignKey(row => row.QuoteId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LeaveRequestDay>()
            .HasOne<LeaveRequest>()
            .WithMany()
            .HasForeignKey(row => row.RequestId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LeaveLedger>()
            .HasOne<LeaveRequest>()
            .WithMany()
            .HasForeignKey(row => row.RequestId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<DeductionElection>()
            .HasOne<ActionQuote>()
            .WithMany()
            .HasForeignKey(row => row.QuoteId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public static void MapEnums(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder builder)
    {
        builder.MapEnum<EmployeeRole>("employee_role", "public", EnumNames);
        builder.MapEnum<GrantModel>("grant_model", "public", EnumNames);
        builder.MapEnum<LeaveStatus>("leave_status", "public", EnumNames);
        builder.MapEnum<LedgerKind>("ledger_kind", "public", EnumNames);
        builder.MapEnum<DeductionKind>("deduction_kind", "public", EnumNames);
        builder.MapEnum<ElectionStatus>("election_status", "public", EnumNames);
        builder.MapEnum<QuoteKind>("quote_kind", "public", EnumNames);
        builder.MapEnum<HsaCoverage>("hsa_coverage", "public", EnumNames);
    }

    private static string ToSnake(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
