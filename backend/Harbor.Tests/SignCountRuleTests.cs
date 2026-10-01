using Harbor;

namespace Harbor.Tests;

public class SignCountRuleTests
{
    [Fact]
    public void A_count_of_zero_stays_zero()
    {
        var decision = SignCountRule.Decide(0, 0);

        Assert.True(decision.Accept);
        Assert.Equal(0, decision.Next);
    }

    [Fact]
    public void A_stored_count_of_five_rejects_the_same_count()
    {
        var decision = SignCountRule.Decide(5, 5);

        Assert.False(decision.Accept);
        Assert.Equal(5, decision.Next);
    }

    [Fact]
    public void A_new_count_of_zero_is_accepted()
    {
        var decision = SignCountRule.Decide(5, 0);

        Assert.True(decision.Accept);
        Assert.Equal(0, decision.Next);
    }

    [Fact]
    public void A_higher_count_is_accepted()
    {
        var decision = SignCountRule.Decide(5, 6);

        Assert.True(decision.Accept);
        Assert.Equal(6, decision.Next);
    }
}
