namespace Harbor;

// A new count of 0 is valid. iCloud and Windows Hello often report 0.
// The stored count stays 0 in that case.
// A stored count above 0 must increase. A repeat of that count is a failed sign-in.
public readonly record struct SignCountDecision(bool Accept, long Next);

public static class SignCountRule
{
    public static SignCountDecision Decide(long stored, long reported)
    {
        if (reported == 0)
        {
            return new SignCountDecision(true, 0);
        }

        if (reported < 0 || stored < 0 || (stored > 0 && reported <= stored))
        {
            return new SignCountDecision(false, stored < 0 ? 0 : stored);
        }

        return new SignCountDecision(true, reported);
    }
}
