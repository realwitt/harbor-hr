using Harbor;

namespace Harbor.Tests;

public class DeductionEstimateTests
{
    [Fact]
    public void Hsa_at_22_percent_under_the_wage_base_splits_the_200_dollars()
    {
        var outcome = DeductionEstimate.Calculate(Base() with { FederalRateBpsOverride = 2200 });
        var estimate = Assert.IsType<DeductionOutcome.Estimated>(outcome).Estimate;

        Assert.Equal("estimate, not a pay stub", estimate.Label);
        Assert.True(estimate.IsEstimate);
        Assert.Equal(20_000, estimate.GrossWithheldCents);
        Assert.Equal(4_400, estimate.EstimatedFederalSavedCents);
        Assert.Equal(1_240, estimate.EstimatedSocialSecuritySavedCents);
        Assert.Equal(290, estimate.EstimatedMedicareSavedCents);
        Assert.Equal(1_530, estimate.EstimatedFicaSavedCents);
        Assert.Equal(0, estimate.EstimatedStateSavedCents);
        Assert.Equal(14_070, estimate.EstimatedTakeHomeReductionCents);
    }

    [Fact]
    public void Bracket_lookup_uses_12_percent_and_22_percent()
    {
        var low = DeductionEstimate.Calculate(Base() with
        {
            FederalRateBpsOverride = null,
            GrossPerPaycheckCents = 200_000,
            PayPeriodsInYear = 26,
            StandardDeductionCents = 1_610_000,
            Brackets = SingleBrackets(),
        });
        var high = DeductionEstimate.Calculate(Base() with
        {
            FederalRateBpsOverride = null,
            GrossPerPaycheckCents = 300_000,
            PayPeriodsInYear = 26,
            StandardDeductionCents = 1_610_000,
            Brackets = SingleBrackets(),
        });

        Assert.Equal(2_400, Assert.IsType<DeductionOutcome.Estimated>(low).Estimate.EstimatedFederalSavedCents);
        Assert.Equal(4_400, Assert.IsType<DeductionOutcome.Estimated>(high).Estimate.EstimatedFederalSavedCents);
    }

    [Fact]
    public void Above_the_social_security_wage_base_saves_medicare_only()
    {
        var outcome = DeductionEstimate.Calculate(Base() with
        {
            YtdWagesCents = 18_450_000,
            SsWageBaseCents = 18_450_000,
            FederalRateBpsOverride = 2200,
        });
        var estimate = Assert.IsType<DeductionOutcome.Estimated>(outcome).Estimate;

        Assert.Equal(0, estimate.EstimatedSocialSecuritySavedCents);
        Assert.Equal(290, estimate.EstimatedMedicareSavedCents);
        Assert.Equal(290, estimate.EstimatedFicaSavedCents);
        Assert.NotEqual(1_530, estimate.EstimatedFicaSavedCents);
    }

    [Fact]
    public void Cap_rejects_when_the_annualized_election_crosses_the_cap()
    {
        var outcome = DeductionEstimate.Calculate(Base() with
        {
            ProposedPerPaycheckCents = 20_000,
            PeriodsRemaining = 26,
            PayrollPostingCents = 0,
            CapCents = 100_000,
        });
        var failed = Assert.IsType<DeductionOutcome.Failed>(outcome);

        Assert.Equal(DeductionEstimate.CapExceeded, failed.Code);
    }

    [Fact]
    public void Retirement_kind_is_rejected_and_is_not_an_hsa_estimate()
    {
        var outcome = DeductionEstimate.Calculate(Base() with { Kind = "retirement" });
        var failed = Assert.IsType<DeductionOutcome.Failed>(outcome);

        Assert.Equal(DeductionEstimate.Unavailable, failed.Code);
        Assert.Equal("That deduction type is not available.", failed.Message);
    }

    [Fact]
    public void Hsa_is_rejected_when_hdhp_is_false()
    {
        var outcome = DeductionEstimate.Calculate(Base() with { HdhpEligible = false, CapCents = 10_000_000 });
        var failed = Assert.IsType<DeductionOutcome.Failed>(outcome);

        Assert.Equal(DeductionEstimate.HdhpRequired, failed.Code);
    }

    [Fact]
    public void Social_security_scales_when_the_paycheck_crosses_the_wage_base()
    {
        var outcome = DeductionEstimate.Calculate(Base() with
        {
            YtdWagesCents = 60_000,
            GrossPerPaycheckCents = 50_000,
            SsWageBaseCents = 100_000,
            FederalRateBpsOverride = 0,
        });
        var estimate = Assert.IsType<DeductionOutcome.Estimated>(outcome).Estimate;

        Assert.Equal(992, estimate.EstimatedSocialSecuritySavedCents);
        Assert.Equal(290, estimate.EstimatedMedicareSavedCents);
    }

    [Fact]
    public void Hsa_catch_up_applies_at_age_55_on_31_december()
    {
        var older = DeductionEstimate.Calculate(Base() with
        {
            BornOn = new DateOnly(1971, 12, 31),
            TaxYear = 2026,
            CapCents = 440_000,
            CatchUpCapCents = 100_000,
            ProposedPerPaycheckCents = 20_000,
            PeriodsRemaining = 26,
            FederalRateBpsOverride = 0,
        });
        var younger = DeductionEstimate.Calculate(Base() with
        {
            BornOn = new DateOnly(1972, 1, 1),
            TaxYear = 2026,
            CapCents = 440_000,
            CatchUpCapCents = 100_000,
            ProposedPerPaycheckCents = 20_000,
            PeriodsRemaining = 26,
        });

        Assert.IsType<DeductionOutcome.Estimated>(older);
        Assert.Equal(DeductionEstimate.CapExceeded, Assert.IsType<DeductionOutcome.Failed>(younger).Code);
    }

    [Fact]
    public void Mid_year_fsa_requires_a_qualifying_event_and_hsa_does_not()
    {
        var fsa = DeductionEstimate.Calculate(Base() with
        {
            Kind = "health_fsa",
            HdhpEligible = false,
            ActiveElectionExists = true,
            EffectiveOn = new DateOnly(2026, 6, 15),
            QualifyingEvent = null,
            ProposedPerPaycheckCents = 100,
            PeriodsRemaining = 1,
        });
        var hsa = DeductionEstimate.Calculate(Base() with
        {
            ActiveElectionExists = true,
            EffectiveOn = new DateOnly(2026, 6, 15),
            QualifyingEvent = null,
        });
        var newYear = DeductionEstimate.Calculate(Base() with
        {
            Kind = "dependent_care_fsa",
            ActiveElectionExists = true,
            EffectiveOn = new DateOnly(2026, 1, 1),
            QualifyingEvent = " ",
        });

        Assert.Equal(
            DeductionEstimate.QualifyingEventRequired,
            Assert.IsType<DeductionOutcome.Failed>(fsa).Code);
        Assert.IsType<DeductionOutcome.Estimated>(hsa);
        Assert.IsType<DeductionOutcome.Estimated>(newYear);
    }

    private static DeductionEstimateInput Base() => new()
    {
        Kind = "hsa",
        GrossPerPaycheckCents = 400_000,
        YtdWagesCents = 0,
        PayPeriodsInYear = 26,
        FederalRateBpsOverride = 2200,
        StandardDeductionCents = 1_610_000,
        Brackets = [],
        StateRateBps = 399,
        ConformsCafeteria = false,
        SsWageBaseCents = 18_450_000,
        SsRateBps = 620,
        MedicareRateBps = 145,
        ProposedPerPaycheckCents = 20_000,
        PeriodsRemaining = 10,
        PayrollPostingCents = 0,
        CapCents = 10_000_000,
        CatchUpCapCents = 0,
        HdhpEligible = true,
        TaxYear = 2026,
    };

    private static IReadOnlyList<TaxBracket> SingleBrackets() =>
    [
        new(1_240_000, 1000),
        new(5_040_000, 1200),
        new(10_570_000, 2200),
        new(20_177_500, 2400),
        new(25_622_500, 3200),
        new(64_060_000, 3500),
        new(null, 3700),
    ];
}
