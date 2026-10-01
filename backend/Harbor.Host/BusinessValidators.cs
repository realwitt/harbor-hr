using FluentValidation;
using Harbor;

namespace Harbor.Host;

public sealed class LeavePreviewBody
{
    public Guid LeaveTypeId { get; set; }
    public string? Start { get; set; }
    public string? End { get; set; }
    public decimal? HoursPerDay { get; set; }
    public bool AdminOverride { get; set; }
}

public sealed class LeaveSubmitBody
{
    public Guid QuoteId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public string? Start { get; set; }
    public string? End { get; set; }
    public decimal? HoursPerDay { get; set; }
    public bool AdminOverride { get; set; }
}

public sealed class LeaveCancelPreviewBody
{
    public Guid RequestId { get; set; }
}

public sealed class LeaveCancelBody
{
    public Guid QuoteId { get; set; }
    public Guid RequestId { get; set; }
}

public sealed class DeductionPreviewBody
{
    public string? Kind { get; set; }
    public int? PerPaycheckCents { get; set; }
    public string? QualifyingEvent { get; set; }
}

public sealed class DeductionSubmitBody
{
    public Guid QuoteId { get; set; }
    public string? Kind { get; set; }
    public int? PerPaycheckCents { get; set; }
    public string? QualifyingEvent { get; set; }
}

public sealed class WithholdingBody
{
    public string? FilingStatus { get; set; }
    public int? OtherIncomeCents { get; set; }
    public int? DeductionsCents { get; set; }
    public int? ExtraWithholdingCents { get; set; }
    public int? DependentsCreditCents { get; set; }
    public int? GrossPerPeriodCents { get; set; }
    public int? YtdWagesCents { get; set; }
}

public sealed class LeaveTypeBody
{
    public string? Code { get; set; }
    public string? Model { get; set; }
    public decimal? HoursPerGrant { get; set; }
    public string? YearBoundary { get; set; }
    public decimal? CarryCapHours { get; set; }
    public decimal? MaxBalanceHours { get; set; }
    public int? WaitingDays { get; set; }
}

public sealed class BlackoutBody
{
    public string? On { get; set; }
    public string? Reason { get; set; }
}

public sealed class DeductionCapBody
{
    public int? TaxYear { get; set; }
    public string? Kind { get; set; }
    public string? Coverage { get; set; }
    public int? LimitCents { get; set; }
}

public sealed class HdhpBody
{
    public bool? HdhpEligible { get; set; }
    public string? HsaCoverage { get; set; }
}

public sealed class LedgerBody
{
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public decimal? Hours { get; set; }
    public string? EffectiveOn { get; set; }
    public string? Note { get; set; }
}

public sealed class PayPeriodBody
{
    public string? StartsOn { get; set; }
    public string? EndsOn { get; set; }
    public string? PayDate { get; set; }
}

public sealed class HolidayBody
{
    public string? On { get; set; }
    public string? Name { get; set; }
}

internal static class IsoDate
{
    public static bool IsValid(string? text) => LeaveRequestPolicy.TryParseIsoDate(text, out _);

    public static bool Try(string? text, out DateOnly date) => LeaveRequestPolicy.TryParseIsoDate(text, out date);
}

internal static class HourRules
{
    public static bool FitsDay(decimal hours) =>
        hours > 0 && hours <= 24 && hours == decimal.Round(hours, 2);

    public static bool FitsLedger(decimal hours) =>
        hours == decimal.Round(hours, 2) && hours is >= -999999.99m and <= 999999.99m;

    public static bool FitsGrant(decimal hours) =>
        hours > 0 && hours <= 999999.99m && hours == decimal.Round(hours, 2);

    public static bool FitsOptionalGrant(decimal? hours) =>
        hours is null || (hours.Value >= 0 && hours.Value <= 999999.99m && hours.Value == decimal.Round(hours.Value, 2));
}

internal static class DeductionKinds
{
    public static bool IsKnown(string? kind) =>
        kind is "hsa" or "health_fsa" or "dependent_care_fsa";
}

internal sealed class LeavePreviewValidator : AbstractValidator<LeavePreviewBody>
{
    public LeavePreviewValidator()
    {
        RuleFor(body => body.LeaveTypeId).NotEmpty().WithMessage("The leave type is required.");
        RuleFor(body => body.Start).Must(IsoDate.IsValid).WithMessage("The date is not an ISO date.");
        RuleFor(body => body.End).Must(IsoDate.IsValid).WithMessage("The date is not an ISO date.");
        RuleFor(body => body.HoursPerDay)
            .Must(hours => hours is null || HourRules.FitsDay(hours.Value))
            .WithMessage("Hours per day must be greater than 0 and at most 24.");
    }
}

internal sealed class LeaveSubmitValidator : AbstractValidator<LeaveSubmitBody>
{
    public LeaveSubmitValidator()
    {
        RuleFor(body => body.QuoteId).NotEmpty().WithMessage("The quote is required.");
        RuleFor(body => body.LeaveTypeId).NotEmpty().WithMessage("The leave type is required.");
        RuleFor(body => body.Start).Must(IsoDate.IsValid).WithMessage("The date is not an ISO date.");
        RuleFor(body => body.End).Must(IsoDate.IsValid).WithMessage("The date is not an ISO date.");
        RuleFor(body => body.HoursPerDay)
            .Must(hours => hours is not null && HourRules.FitsDay(hours.Value))
            .WithMessage("Hours per day must be greater than 0 and at most 24.");
    }
}

internal sealed class LeaveCancelPreviewValidator : AbstractValidator<LeaveCancelPreviewBody>
{
    public LeaveCancelPreviewValidator()
    {
        RuleFor(body => body.RequestId).NotEmpty().WithMessage("The request is required.");
    }
}

internal sealed class LeaveCancelValidator : AbstractValidator<LeaveCancelBody>
{
    public LeaveCancelValidator()
    {
        RuleFor(body => body.QuoteId).NotEmpty().WithMessage("The quote is required.");
        RuleFor(body => body.RequestId).NotEmpty().WithMessage("The request is required.");
    }
}

internal sealed class DeductionPreviewValidator : AbstractValidator<DeductionPreviewBody>
{
    public DeductionPreviewValidator()
    {
        RuleFor(body => body.Kind).Must(DeductionKinds.IsKnown).WithMessage("The deduction type is not valid.");
        RuleFor(body => body.PerPaycheckCents)
            .Must(cents => cents is >= 0)
            .WithMessage("The amount must be zero or greater.");
        RuleFor(body => body.QualifyingEvent)
            .Must(text => text is null || text.Trim().Length <= 200)
            .WithMessage("The qualifying event is too long.");
    }
}

internal sealed class DeductionSubmitValidator : AbstractValidator<DeductionSubmitBody>
{
    public DeductionSubmitValidator()
    {
        RuleFor(body => body.QuoteId).NotEmpty().WithMessage("The quote is required.");
        RuleFor(body => body.Kind).Must(DeductionKinds.IsKnown).WithMessage("The deduction type is not valid.");
        RuleFor(body => body.PerPaycheckCents)
            .Must(cents => cents is >= 0)
            .WithMessage("The amount must be zero or greater.");
        RuleFor(body => body.QualifyingEvent)
            .Must(text => text is null || text.Trim().Length <= 200)
            .WithMessage("The qualifying event is too long.");
    }
}

internal sealed class WithholdingValidator : AbstractValidator<WithholdingBody>
{
    public WithholdingValidator()
    {
        RuleFor(body => body.FilingStatus)
            .Must(status => status is "single" or "married_joint" or "head")
            .WithMessage("The filing status is not valid.");
        RuleFor(body => body.OtherIncomeCents).Must(Cents).WithMessage("Other income must be zero or greater.");
        RuleFor(body => body.DeductionsCents).Must(Cents).WithMessage("Deductions must be zero or greater.");
        RuleFor(body => body.ExtraWithholdingCents).Must(Cents).WithMessage("Extra withholding must be zero or greater.");
        RuleFor(body => body.DependentsCreditCents).Must(Cents).WithMessage("The dependents credit must be zero or greater.");
        RuleFor(body => body.GrossPerPeriodCents).Must(Cents).WithMessage("Gross pay must be zero or greater.");
        RuleFor(body => body.YtdWagesCents).Must(Cents).WithMessage("Year-to-date wages must be zero or greater.");
    }

    private static bool Cents(int? value) => value is >= 0;
}

internal sealed class LeaveTypeValidator : AbstractValidator<LeaveTypeBody>
{
    public LeaveTypeValidator()
    {
        RuleFor(body => body.Code)
            .Must(code => !string.IsNullOrWhiteSpace(code) && code.Trim().Length <= 40)
            .WithMessage("The code is required.");
        RuleFor(body => body.Model)
            .Must(model => model is "accrued" or "instant" or "unpaid")
            .WithMessage("The grant model is not valid.");
        RuleFor(body => body.YearBoundary)
            .Must(boundary => boundary is null or "" or "calendar" or "anniversary")
            .WithMessage("The year boundary is not valid.");
        RuleFor(body => body.WaitingDays)
            .Must(days => days is null or >= 0)
            .WithMessage("Waiting days must be zero or greater.");
        RuleFor(body => body.CarryCapHours)
            .Must(HourRules.FitsOptionalGrant)
            .WithMessage("The carry cap is not valid.");
        RuleFor(body => body.MaxBalanceHours)
            .Must(HourRules.FitsOptionalGrant)
            .WithMessage("The max balance is not valid.");
        When(body => body.Model is "accrued" or "instant", () =>
        {
            RuleFor(body => body.HoursPerGrant)
                .Must(hours => hours is not null && HourRules.FitsGrant(hours.Value))
                .WithMessage("Hours per grant must be greater than 0.");
        });
        When(body => body.Model == "unpaid", () =>
        {
            RuleFor(body => body.HoursPerGrant)
                .Null()
                .WithMessage("Unpaid leave has no grant hours.");
        });
    }
}

internal sealed class BlackoutValidator : AbstractValidator<BlackoutBody>
{
    public BlackoutValidator()
    {
        RuleFor(body => body.On).Must(IsoDate.IsValid).WithMessage("The date is not an ISO date.");
        RuleFor(body => body.Reason)
            .Must(reason => !string.IsNullOrWhiteSpace(reason) && reason.Trim().Length <= 200)
            .WithMessage("The reason is required.");
    }
}

internal sealed class DeductionCapValidator : AbstractValidator<DeductionCapBody>
{
    public DeductionCapValidator()
    {
        RuleFor(body => body.TaxYear)
            .Must(year => year is >= 2000 and <= 2100)
            .WithMessage("The tax year is not valid.");
        RuleFor(body => body.Kind).Must(DeductionKinds.IsKnown).WithMessage("The deduction type is not valid.");
        RuleFor(body => body.Coverage)
            .Must(coverage => !string.IsNullOrWhiteSpace(coverage) && coverage.Trim().Length <= 40)
            .WithMessage("The coverage is required.");
        RuleFor(body => body.LimitCents)
            .Must(cents => cents is > 0)
            .WithMessage("The limit must be greater than 0.");
    }
}

internal sealed class HdhpValidator : AbstractValidator<HdhpBody>
{
    public HdhpValidator()
    {
        RuleFor(body => body.HdhpEligible).NotNull().WithMessage("HDHP eligibility is required.");
        RuleFor(body => body.HsaCoverage)
            .Must(coverage => coverage is null or "" or "self" or "family")
            .WithMessage("The HSA coverage is not valid.");
    }
}

internal sealed class LedgerValidator : AbstractValidator<LedgerBody>
{
    public LedgerValidator()
    {
        RuleFor(body => body.EmployeeId).NotEmpty().WithMessage("The employee is required.");
        RuleFor(body => body.LeaveTypeId).NotEmpty().WithMessage("The leave type is required.");
        RuleFor(body => body.Hours)
            .Must(hours => hours is not null && HourRules.FitsLedger(hours.Value))
            .WithMessage("The hours are not valid.");
        RuleFor(body => body.EffectiveOn).Must(IsoDate.IsValid).WithMessage("The date is not an ISO date.");
        RuleFor(body => body.Note)
            .Must(note => !string.IsNullOrWhiteSpace(note) && note.Trim().Length <= 500)
            .WithMessage("The note is required.");
    }
}

internal sealed class PayPeriodValidator : AbstractValidator<PayPeriodBody>
{
    public PayPeriodValidator()
    {
        RuleFor(body => body.StartsOn).Must(IsoDate.IsValid).WithMessage("The date is not an ISO date.");
        RuleFor(body => body.EndsOn).Must(IsoDate.IsValid).WithMessage("The date is not an ISO date.");
        RuleFor(body => body.PayDate).Must(IsoDate.IsValid).WithMessage("The date is not an ISO date.");
        RuleFor(body => body)
            .Must(Ordered)
            .WithMessage("The end date is before the start date.");
    }

    private static bool Ordered(PayPeriodBody body)
    {
        if (!IsoDate.Try(body.StartsOn, out var start) || !IsoDate.Try(body.EndsOn, out var end))
        {
            return true;
        }

        return start <= end;
    }
}

internal sealed class HolidayValidator : AbstractValidator<HolidayBody>
{
    public HolidayValidator()
    {
        RuleFor(body => body.On).Must(IsoDate.IsValid).WithMessage("The date is not an ISO date.");
        RuleFor(body => body.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 200)
            .WithMessage("The name is required.");
    }
}
