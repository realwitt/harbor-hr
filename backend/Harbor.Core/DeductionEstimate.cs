using System.Globalization;
using System.Text.Json;

namespace Harbor;

public sealed record TaxBracket(long? UpToCents, int RateBps);

public sealed record DeductionEstimateInput
{
    public int GrossPerPaycheckCents { get; init; }
    public int YtdWagesCents { get; init; }
    public int PayPeriodsInYear { get; init; }
    public int? FederalRateBpsOverride { get; init; }
    public int StandardDeductionCents { get; init; }
    public IReadOnlyList<TaxBracket> Brackets { get; init; } = [];
    public int StateRateBps { get; init; }
    public bool ConformsCafeteria { get; init; }
    public int SsWageBaseCents { get; init; }
    public int SsRateBps { get; init; } = 620;
    public int MedicareRateBps { get; init; } = 145;
    public required string Kind { get; init; }
    public int ProposedPerPaycheckCents { get; init; }
    public int PeriodsRemaining { get; init; }
    public int PayrollPostingCents { get; init; }
    public int CapCents { get; init; }
    public int CatchUpCapCents { get; init; }
    public bool HdhpEligible { get; init; }
    public DateOnly? BornOn { get; init; }
    public int TaxYear { get; init; }
    public bool ActiveElectionExists { get; init; }
    public DateOnly? EffectiveOn { get; init; }
    public string? QualifyingEvent { get; init; }
}

public sealed record PaycheckEstimate
{
    public string Label { get; init; } = "estimate, not a pay stub";
    public bool IsEstimate { get; init; } = true;
    public int GrossWithheldCents { get; init; }
    public int EstimatedFederalSavedCents { get; init; }
    public int EstimatedFicaSavedCents { get; init; }
    public int EstimatedSocialSecuritySavedCents { get; init; }
    public int EstimatedMedicareSavedCents { get; init; }
    public int EstimatedStateSavedCents { get; init; }
    public int EstimatedTakeHomeReductionCents { get; init; }
}

public abstract record DeductionOutcome
{
    public sealed record Estimated(PaycheckEstimate Estimate) : DeductionOutcome;
    public sealed record Failed(string Code, string Message) : DeductionOutcome;
}

public static class DeductionEstimate
{
    public const string Unavailable = "unavailable";
    public const string HdhpRequired = "hdhp_required";
    public const string CapExceeded = "cap_exceeded";
    public const string QualifyingEventRequired = "qualifying_event_required";

    public static DeductionOutcome Calculate(DeductionEstimateInput input)
    {
        if (input.ProposedPerPaycheckCents < 0)
        {
            return new DeductionOutcome.Failed("invalid_amount", "The amount must be zero or greater.");
        }

        var kind = input.Kind.Trim().ToLowerInvariant();
        if (kind is not ("hsa" or "health_fsa" or "dependent_care_fsa"))
        {
            return new DeductionOutcome.Failed(Unavailable, "That deduction type is not available.");
        }

        if (kind == "hsa" && !input.HdhpEligible)
        {
            return new DeductionOutcome.Failed(HdhpRequired, "HSA requires HDHP eligibility.");
        }

        if (kind is "health_fsa" or "dependent_care_fsa"
            && input.ActiveElectionExists
            && input.EffectiveOn is DateOnly effective
            && effective != new DateOnly(effective.Year, 1, 1)
            && string.IsNullOrWhiteSpace(input.QualifyingEvent))
        {
            return new DeductionOutcome.Failed(
                QualifyingEventRequired,
                "A mid-year FSA change requires a qualifying event.");
        }

        var cap = (long)input.CapCents;
        if (kind == "hsa" && input.BornOn is DateOnly born && input.TaxYear - born.Year >= 55)
        {
            cap += input.CatchUpCapCents;
        }

        var projected = (long)input.PayrollPostingCents
            + (long)input.ProposedPerPaycheckCents * input.PeriodsRemaining;
        if (projected > cap)
        {
            return new DeductionOutcome.Failed(CapExceeded, "The election crosses the annual cap.");
        }

        var federalRate = input.FederalRateBpsOverride ?? MarginalRate(input);
        var proposed = input.ProposedPerPaycheckCents;
        var federal = RoundCents(proposed * federalRate / 10000m);
        var state = input.ConformsCafeteria
            ? RoundCents(proposed * input.StateRateBps / 10000m)
            : 0;
        var socialSecurity = SocialSecuritySaved(input, proposed);
        var medicare = RoundCents(proposed * input.MedicareRateBps / 10000m);
        var fica = socialSecurity + medicare;
        return new DeductionOutcome.Estimated(new PaycheckEstimate
        {
            GrossWithheldCents = proposed,
            EstimatedFederalSavedCents = federal,
            EstimatedFicaSavedCents = fica,
            EstimatedSocialSecuritySavedCents = socialSecurity,
            EstimatedMedicareSavedCents = medicare,
            EstimatedStateSavedCents = state,
            EstimatedTakeHomeReductionCents = proposed - federal - fica - state,
        });
    }

    private static int MarginalFederalRateBps(long taxableCents, IReadOnlyList<TaxBracket> brackets)
    {
        if (taxableCents < 0)
        {
            return 0;
        }

        foreach (var bracket in brackets)
        {
            if (bracket.UpToCents is null || taxableCents <= bracket.UpToCents.Value)
            {
                return bracket.RateBps;
            }
        }

        return brackets.Count == 0 ? 0 : brackets[^1].RateBps;
    }

    private static int MarginalRate(DeductionEstimateInput input)
    {
        var annualized = (long)input.GrossPerPaycheckCents * input.PayPeriodsInYear;
        var taxable = annualized - input.StandardDeductionCents;
        return MarginalFederalRateBps(taxable, input.Brackets);
    }

    private static int SocialSecuritySaved(DeductionEstimateInput input, int proposed)
    {
        if (proposed == 0 || input.GrossPerPaycheckCents <= 0 || input.YtdWagesCents >= input.SsWageBaseCents)
        {
            return 0;
        }

        var room = (long)input.SsWageBaseCents - input.YtdWagesCents;
        var under = Math.Min(room, input.GrossPerPaycheckCents);
        var scaled = proposed * (under / (decimal)input.GrossPerPaycheckCents);
        return RoundCents(scaled * input.SsRateBps / 10000m);
    }

    private static int RoundCents(decimal value)
    {
        return (int)Math.Round(value, 0, MidpointRounding.AwayFromZero);
    }
}

public static class FederalTaxTable
{
    public static bool TryRead(
        string json,
        string filingStatus,
        out int standardDeductionCents,
        out IReadOnlyList<TaxBracket> brackets,
        out string? error)
    {
        standardDeductionCents = 0;
        brackets = [];
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("standard_deduction_cents", out var standard)
                || !standard.TryGetProperty(filingStatus, out var deduction))
            {
                error = "The filing status has no standard deduction.";
                return false;
            }

            standardDeductionCents = deduction.GetInt32();
            if (!root.TryGetProperty(filingStatus, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                error = "The filing status has no brackets.";
                return false;
            }

            var parsed = new List<TaxBracket>();
            foreach (var item in list.EnumerateArray())
            {
                var rate = item.GetProperty("rate_bps").GetInt32();
                long? upTo = null;
                if (item.TryGetProperty("up_to_cents", out var up) && up.ValueKind != JsonValueKind.Null)
                {
                    upTo = up.GetInt64();
                }

                parsed.Add(new TaxBracket(upTo, rate));
            }

            brackets = parsed;
            return parsed.Count > 0;
        }
        catch (JsonException)
        {
            error = "The tax table is not valid.";
            return false;
        }
        catch (FormatException)
        {
            error = "The tax table is not valid.";
            return false;
        }
    }

    public static bool TryReadW4(string json, out int grossPerPaycheckCents, out int ytdWagesCents)
    {
        grossPerPaycheckCents = 0;
        ytdWagesCents = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            grossPerPaycheckCents = doc.RootElement.GetProperty("gross_per_period_cents").GetInt32();
            ytdWagesCents = doc.RootElement.GetProperty("ytd_wages_cents").GetInt32();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    public static string NormalizeKind(string kind)
    {
        return kind.Trim().ToLower(CultureInfo.InvariantCulture);
    }
}
