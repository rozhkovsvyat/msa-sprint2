using Npgsql;

namespace Hotelio.Bookings.Outbox;

/// <summary>Состояние очереди outbox для GET /diagnostics/outbox. Карантин в возраст очереди не входит.</summary>
public sealed record OutboxStats(long Pending, long Retrying, long DeadLetters, double OldestAgeSeconds)
{
    public static async Task<OutboxStats> ReadAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            select count(*) filter (where published_at is null and dead_lettered_at is null),
                   count(*) filter (where published_at is null and dead_lettered_at is null and failure_count > 0),
                   count(*) filter (where dead_lettered_at is not null),
                   coalesce(extract(epoch from now() - min(created_at) filter (where published_at is null and dead_lettered_at is null)), 0)::double precision
            from outbox
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new OutboxStats(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3));
    }
}
