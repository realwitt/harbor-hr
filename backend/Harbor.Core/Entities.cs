namespace Harbor;

public sealed class Employee
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string Name { get; set; }
    public EmployeeRole Role { get; set; } = EmployeeRole.Employee;
    public string Timezone { get; set; } = "America/New_York";
    public required string Jurisdiction { get; set; }
    public DateOnly HiredOn { get; set; }
    public DateOnly? TerminatedOn { get; set; }
    public DateOnly? BornOn { get; set; }
    public bool HdhpEligible { get; set; }
    public HsaCoverage? HsaCoverage { get; set; }
    public DateTimeOffset? RecoverySavedAt { get; set; }
    public DateTimeOffset? McpEnabledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ManagerLink
{
    public Guid EmployeeId { get; set; }
    public Guid ManagerId { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public DateOnly? EndedOn { get; set; }
}

public sealed class PayPeriod
{
    public Guid Id { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public DateOnly PayDate { get; set; }
}

public sealed class CompanyHoliday
{
    public DateOnly OnDate { get; set; }
    public required string Name { get; set; }
}

public sealed class LeaveType
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public GrantModel Model { get; set; }
    public decimal? HoursPerGrant { get; set; }
    public string YearBoundary { get; set; } = "calendar";
    public decimal? CarryCapHours { get; set; }
    public decimal? MaxBalanceHours { get; set; }
    public int WaitingDays { get; set; }
}

public sealed class LeaveLedger
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public LedgerKind Kind { get; set; }
    public decimal Hours { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public Guid? RequestId { get; set; }
    public required string Source { get; set; }
    public string? ExternalRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LeaveRequest
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public LeaveStatus Status { get; set; } = LeaveStatus.Pending;
    public required string IdempotencyKey { get; set; }
    public Guid QuoteId { get; set; }
    public bool AdminOverride { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
}

public sealed class LeaveRequestDay
{
    public Guid RequestId { get; set; }
    public DateOnly OnDate { get; set; }
    public decimal Hours { get; set; }
    public Guid EmployeeId { get; set; }
    public LeaveStatus Status { get; set; }
}

public sealed class BlackoutDate
{
    public DateOnly OnDate { get; set; }
    public required string Reason { get; set; }
}

public sealed class ActionQuote
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public QuoteKind Kind { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DeductionCap
{
    public int TaxYear { get; set; }
    public DeductionKind Kind { get; set; }
    public required string Coverage { get; set; }
    public int LimitCents { get; set; }
}

public sealed class TaxYearParam
{
    public int TaxYear { get; set; }
    public int SsWageBaseCents { get; set; }
    public int SsRateBps { get; set; }
    public int MedicareRateBps { get; set; }
    public required string FederalBrackets { get; set; }
}

public sealed class StateIncomeTax
{
    public int TaxYear { get; set; }
    public required string StateCode { get; set; }
    public int RateBps { get; set; }
    public bool ConformsCafeteria { get; set; }
}

public sealed class DeductionElection
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public DeductionKind Kind { get; set; }
    public int PerPaycheckCents { get; set; }
    public ElectionStatus Status { get; set; } = ElectionStatus.PendingConfirm;
    public DateOnly EffectiveOn { get; set; }
    public DateOnly? EndedOn { get; set; }
    public string? QualifyingEvent { get; set; }
    public Guid QuoteId { get; set; }
    public required string IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PayrollPosting
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid PayPeriodId { get; set; }
    public DeductionKind Kind { get; set; }
    public int AmountCents { get; set; }
    public required string ExternalRef { get; set; }
    public DateTimeOffset PostedAt { get; set; }
}

public sealed class WithholdingProfile
{
    public Guid EmployeeId { get; set; }
    public required string FilingStatus { get; set; }
    public required string W4 { get; set; }
    public required string StateCode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WebauthnCredential
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public byte[] CredentialId { get; set; } = [];
    public byte[] PublicKey { get; set; } = [];
    public long SignCount { get; set; }
    public Guid? Aaguid { get; set; }
    public string[] Transports { get; set; } = [];
    public string? Nickname { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RecoveryCode
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public required string CodeHash { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class JoinRequest
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string Name { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public Guid? InviteId { get; set; }
}

public sealed class Invite
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string Name { get; set; }
    public EmployeeRole Role { get; set; } = EmployeeRole.Employee;
    public Guid? ManagerId { get; set; }
    public DateOnly HiredOn { get; set; }
    public required string Jurisdiction { get; set; }
    public string Timezone { get; set; } = "America/New_York";
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AppSession
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class WebauthnChallenge
{
    public Guid Id { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? InviteId { get; set; }
    public required string Kind { get; set; }
    public string? Action { get; set; }
    public Guid? QuoteId { get; set; }
    public byte[] Challenge { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class McpClient
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public required string ClientName { get; set; }
    public string? OauthApplicationId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
