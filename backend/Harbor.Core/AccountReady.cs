namespace Harbor;

public static class AccountReady
{
    public static bool IsReady(DateTimeOffset? recoverySavedAt, int passkeyCount) =>
        recoverySavedAt is not null || passkeyCount >= 2;
}
