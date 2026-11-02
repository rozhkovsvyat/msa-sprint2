using Microsoft.Extensions.Options;
using Npgsql;

namespace Hotelio.Bookings.Outbox;

/// <summary>
/// Раз в час удаляет события, опубликованные больше OUTBOX_RETENTION_DAYS дней назад (по умолчанию 14).
/// Неотправленные события и карантин не трогает.
/// </summary>
public sealed class OutboxCleanup(NpgsqlDataSource db, IOptions<OutboxOptions> options, ILogger<OutboxCleanup> log) : BackgroundService
{
    private const int BatchSize = 1000;

    private readonly int retentionDays = options.Value.RetentionDays;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var deleted = await DeleteExpiredAsync(ct);
                if (deleted > 0) log.LogInformation("Outbox cleanup: deleted {Deleted} events published more than {Days} days ago", deleted, retentionDays);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Outbox cleanup failed: {Error}", ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>
    /// Удаляет пакетами по 1000 строк, чтобы транзакции были короткими. FOR UPDATE SKIP LOCKED: строку, которую в это время
    /// снова ставят в очередь (published_at = null), не удалим.
    /// </summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken ct)
    {
        var total = 0;
        int deleted;
        do
        {
            await using var cmd = db.CreateCommand("""
                delete from outbox where id in (
                    select id from outbox where published_at < now() - @retention
                    order by id limit @batch for update skip locked)
                """);
            cmd.Parameters.AddWithValue("retention", TimeSpan.FromDays(retentionDays));
            cmd.Parameters.AddWithValue("batch", BatchSize);
            deleted = await cmd.ExecuteNonQueryAsync(ct);
            total += deleted;
        }
        while (deleted == BatchSize);
        return total;
    }
}
