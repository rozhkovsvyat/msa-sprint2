using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Npgsql;

namespace Hotelio.Bookings.Outbox;

/// <summary>
/// Публикует события outbox в Kafka. Каждая строка обрабатывается в своей транзакции, поэтому сбой одной не задерживает
/// остальные. Временные ошибки повторяются с растущей паузой, пока брокер не станет доступен; ошибки, которые повтором
/// не исправить, переводят строку в карантин (dead_lettered_at). Доставка «хотя бы один раз»: сбой после подтверждения
/// Kafka допускает повторную публикацию, booking-history-service отсеивает её по eventId.
/// </summary>
public sealed class OutboxPublisher(NpgsqlDataSource db, IConfiguration config, ILogger<OutboxPublisher> log) : BackgroundService
{
    private readonly string topic = config["KAFKA_TOPIC"] ?? "booking-events";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = config["KAFKA_BOOTSTRAP"], Acks = Acks.All, EnableIdempotence = true, MessageTimeoutMs = 3000,
        }).Build();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await PublishBatchAsync(producer, ct) == 0) await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning("Outbox database unavailable: {Error}", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    /// <summary>Ошибки Kafka, которые повтором не исправить: сообщение слишком велико или нет доступа к топику.</summary>
    public static bool IsPermanent(ErrorCode code) => code is ErrorCode.MsgSizeTooLarge or ErrorCode.InvalidMsgSize
        or ErrorCode.TopicAuthorizationFailed or ErrorCode.TopicException;

    /// <summary>Пауза перед следующей попыткой: 1, 2, 4, 8, 16, затем 30 с.</summary>
    public static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt - 1, 5))));

    private async Task<int> PublishBatchAsync(IProducer<string, string> producer, CancellationToken ct)
    {
        var ids = new List<long>();
        await using (var select = db.CreateCommand("""
            select id from outbox where published_at is null and dead_lettered_at is null and next_attempt_at <= now()
            order by id limit 100
            """))
        await using (var reader = await select.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt64(0));

        foreach (var id in ids) await PublishOneAsync(id, producer, ct);
        return ids.Count;
    }

    private async Task PublishOneAsync(long id, IProducer<string, string> producer, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid eventId; long bookingId; string eventType, payload, requestId; int failures;
        await using (var select = new NpgsqlCommand("""
            select event_id, aggregate_id, event_type, payload::text, correlation_id, failure_count from outbox
            where id = @id and published_at is null and dead_lettered_at is null and next_attempt_at <= now()
            for update skip locked
            """, conn, tx))
        {
            select.Parameters.AddWithValue("id", id);
            await using var r = await select.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return; // строку уже взял другой экземпляр
            (eventId, bookingId, eventType, payload, requestId, failures) =
                (r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5));
        }

        ErrorCode? error = null;
        try
        {
            await producer.ProduceAsync(topic, new Message<string, string>
            {
                Key = bookingId.ToString(CultureInfo.InvariantCulture),
                Value = payload,
                Headers = new Headers
                {
                    { "event-id", Encoding.UTF8.GetBytes(eventId.ToString()) },
                    { "event-type", Encoding.UTF8.GetBytes(eventType) },
                    { "correlation-id", Encoding.UTF8.GetBytes(requestId) },
                },
            }, ct);
        }
        catch (KafkaException ex)
        {
            error = ex.Error.Code;
        }

        await using var update = new NpgsqlCommand(error is null
            ? "update outbox set published_at = now(), last_error = null where id = @id"
            : """
              update outbox set failure_count = failure_count + 1, last_error = @error, next_attempt_at = now() + @delay,
                  dead_lettered_at = case when @permanent then now() end
              where id = @id
              """, conn, tx);
        update.Parameters.AddWithValue("id", id);
        if (error is { } code)
        {
            var permanent = IsPermanent(code);
            update.Parameters.AddWithValue("error", "Kafka: " + code);
            update.Parameters.AddWithValue("delay", RetryDelay(failures + 1));
            update.Parameters.AddWithValue("permanent", permanent);
            log.Log(permanent ? LogLevel.Error : LogLevel.Warning,
                "Outbox {Action} eventId={EventId} bookingId={BookingId} attempt={Attempt} error={Error} requestId={RequestId}",
                permanent ? "quarantine" : "retry", eventId, bookingId, failures + 1, code, requestId);
        }
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        if (error is null)
            log.LogInformation("Published eventId={EventId} bookingId={BookingId} requestId={RequestId}", eventId, bookingId, requestId);
    }
}
