using Microsoft.EntityFrameworkCore;

namespace Harbor.Host;

public sealed record AuditStamp(
    Guid? ActorId,
    string Channel,
    Guid? ClientId,
    string AuthFactor,
    string? RequestId,
    Guid? QuoteId,
    string? IdempotencyKey)
{
    public static AuditStamp ForJob() => new(
        ActorId: null,
        Channel: "job",
        ClientId: null,
        AuthFactor: "system",
        RequestId: "accrual",
        QuoteId: null,
        IdempotencyKey: null);

    public AuditStamp With(Guid? quoteId, string? idempotencyKey) =>
        this with { QuoteId = quoteId, IdempotencyKey = idempotencyKey };
}

public static class AuditGuc
{
    public static async Task Apply(DbContext db, AuditStamp stamp, CancellationToken ct)
    {
        await Set(db, "audit.actor_id", stamp.ActorId?.ToString() ?? "", ct);
        await Set(db, "audit.channel", stamp.Channel, ct);
        await Set(db, "audit.client_id", stamp.ClientId?.ToString() ?? "", ct);
        await Set(db, "audit.auth_factor", stamp.AuthFactor, ct);
        await Set(db, "audit.request_id", stamp.RequestId ?? "", ct);
        await Set(db, "audit.quote_id", stamp.QuoteId?.ToString() ?? "", ct);
        await Set(db, "audit.idempotency_key", stamp.IdempotencyKey ?? "", ct);
    }

    private static Task Set(DbContext db, string name, string value, CancellationToken ct)
    {
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"select set_config({name}, {value}, true)",
            ct);
    }
}
