using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hotelio.Bookings.Outbox;

/// <summary>
/// Публикует события outbox в Kafka, каждую строку в своей транзакции. Временные ошибки повторяются с растущей паузой,
/// неисправимые (слишком большое сообщение, нет прав на топик) откладывают строку в карантин — dead_lettered_at — для ручного разбора.
/// Доставка «хотя бы один раз»: дубли отсеивает история по eventId.
/// </summary>
public sealed class OutboxPublisher(NpgsqlDataSource db, IOptions<OutboxOptions> options, ILogger<OutboxPublisher> log) : BackgroundService
{
    private const int BatchSize = 100;

    private readonly OutboxOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // После фатальной ошибки идемпотентный продюсер librdkafka уже не отправляет — создаём новый.
            using var producer = new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = settings.KafkaBootstrap,
                Acks = Acks.All,
                EnableIdempotence = true,
                MessageTimeoutMs = 3000,
            }).Build();
            await PublishUntilFatalAsync(producer, ct);
            if (!ct.IsCancellationRequested) log.LogError("Kafka producer failed fatally and is recreated");
        }
    }

    /// <summary>Ошибки Kafka, которые повтором не исправить: сообщение слишком велико или нет доступа к топику.</summary>
    public static bool IsPermanent(ErrorCode code) => code is ErrorCode.MsgSizeTooLarge or ErrorCode.InvalidMsgSize
        or ErrorCode.TopicAuthorizationFailed or ErrorCode.TopicException;

    /// <summary>Пауза перед следующей попыткой: 1, 2, 4, 8, 16, затем 30 с.</summary>
    public static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt - 1, 5))));

    private async Task PublishUntilFatalAsync(IProducer<string, string> producer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (handled, fatal) = await PublishBatchAsync(producer, ct);
                if (fatal) return;
                if (handled == 0) await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogWarning("Outbox publishing failed: {Error}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    /// <summary>Публикует очередную пачку. Handled — сколько строк обработано: строки, взятые другим экземпляром, не считаются.</summary>
    private async Task<(int Handled, bool Fatal)> PublishBatchAsync(IProducer<string, string> producer, CancellationToken ct)
    {
        var handled = 0;
        foreach (var id in await DueIdsAsync(ct))
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var pending = await LockAsync(id, conn, tx, ct);
            if (pending is null) continue; // строку уже взял другой экземпляр

            var error = await ProduceAsync(pending, producer, ct);
            await MarkAsync(pending, error, conn, tx, ct);
            await tx.CommitAsync(ct);
            handled++;
            if (error is null)
                log.LogInformation("Published eventId={EventId} bookingId={BookingId} requestId={RequestId}",
                    pending.EventId, pending.BookingId, pending.RequestId);
            else if (error.IsFatal)
                return (handled, true);
        }
        return (handled, false);
    }

    private async Task<List<long>> DueIdsAsync(CancellationToken ct)
    {
        await using var select = db.CreateCommand("""
            select id from outbox where published_at is null and dead_lettered_at is null and next_attempt_at <= now()
            order by id limit @batch
            """);
        select.Parameters.AddWithValue("batch", BatchSize);
        var ids = new List<long>();
        await using var reader = await select.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static async Task<PendingEvent?> LockAsync(long id, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var select = new NpgsqlCommand("""
            select event_id, aggregate_id, event_type, payload::text, correlation_id, failure_count from outbox
            where id = @id and published_at is null and dead_lettered_at is null and next_attempt_at <= now()
            for update skip locked
            """, conn, tx);
        select.Parameters.AddWithValue("id", id);
        await using var r = await select.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new PendingEvent(id, r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5))
            : null;
    }

    private async Task<Error?> ProduceAsync(PendingEvent pending, IProducer<string, string> producer, CancellationToken ct)
    {
        try
        {
            await producer.ProduceAsync(settings.Topic, new Message<string, string>
            {
                Key = pending.BookingId.ToString(CultureInfo.InvariantCulture),
                Value = pending.Payload,
                Headers = new Headers
                {
                    { "event-id", Encoding.UTF8.GetBytes(pending.EventId.ToString()) },
                    { "event-type", Encoding.UTF8.GetBytes(pending.EventType) },
                    { "correlation-id", Encoding.UTF8.GetBytes(pending.RequestId) },
                },
            }, ct);
            return null;
        }
        catch (KafkaException ex)
        {
            return ex.Error;
        }
    }

    private async Task MarkAsync(PendingEvent pending, Error? error, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        if (error is null)
        {
            await using var published = new NpgsqlCommand("update outbox set published_at = now(), last_error = null where id = @id", conn, tx);
            published.Parameters.AddWithValue("id", pending.Id);
            await published.ExecuteNonQueryAsync(ct);
            return;
        }

        var attempt = pending.Failures + 1;
        var permanent = IsPermanent(error.Code);
        await using var failed = new NpgsqlCommand("""
            update outbox set failure_count = failure_count + 1, last_error = @error, next_attempt_at = now() + @delay,
                dead_lettered_at = case when @permanent then now() end
            where id = @id
            """, conn, tx);
        failed.Parameters.AddWithValue("id", pending.Id);
        failed.Parameters.AddWithValue("error", "Kafka: " + error.Code);
        failed.Parameters.AddWithValue("delay", RetryDelay(attempt));
        failed.Parameters.AddWithValue("permanent", permanent);
        await failed.ExecuteNonQueryAsync(ct);
        log.Log(permanent ? LogLevel.Error : LogLevel.Warning,
            "Outbox {Action} eventId={EventId} bookingId={BookingId} attempt={Attempt} error={Error} requestId={RequestId}",
            permanent ? "quarantine" : "retry", pending.EventId, pending.BookingId, attempt, error.Code, pending.RequestId);
    }

    /// <summary>Строка outbox, взятая на публикацию.</summary>
    private sealed record PendingEvent(long Id, Guid EventId, long BookingId, string EventType, string Payload, string RequestId, int Failures);
}
