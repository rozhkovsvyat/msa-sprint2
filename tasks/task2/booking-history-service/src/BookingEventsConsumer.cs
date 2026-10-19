using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Npgsql;

namespace Hotelio.BookingHistory;

/// <summary>
/// Читает booking-events своей группой. Смещение фиксируется только после записи в историю или в dead letter.
/// Неразбираемое сообщение и неподдерживаемая версия уходят в dead letter и не останавливают поток.
/// Временная ошибка БД повторяется; если повторы не помогли, смещение не фиксируется и сообщение читается снова.
/// </summary>
public sealed class BookingEventsConsumer(HistoryStore store, IConfiguration config, ILogger<BookingEventsConsumer> log) : BackgroundService
{
    private const int MaxAttempts = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => RunAsync(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken ct)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = config["KAFKA_BOOTSTRAP"], GroupId = config["KAFKA_GROUP"] ?? "booking-history",
            AutoOffsetReset = AutoOffsetReset.Earliest, EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(config["KAFKA_TOPIC"] ?? "booking-events");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, string>? record = null;
                try
                {
                    record = consumer.Consume(ct);
                    await ProcessAsync(record, ct);
                    consumer.Commit(record);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    log.LogWarning("History unavailable: {Error}; offset not committed, message will be read again", ex.GetType().Name);
                    if (record is not null) consumer.Seek(record.TopicPartitionOffset);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
        }
        finally { consumer.Close(); }
    }

    private async Task ProcessAsync(ConsumeResult<string, string> record, CancellationToken ct)
    {
        if (!TryParse(record.Message.Value, out var evt, out var reason))
        {
            var requestId = record.Message.Headers?.TryGetLastBytes("correlation-id", out var bytes) == true
                ? Encoding.UTF8.GetString(bytes) : null;
            await DeadLetterAsync(record, reason, requestId, ct);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var saved = await store.SaveAsync(evt, ct);
                log.LogInformation("{Result} eventId={EventId} bookingId={BookingId} {Partition}@{Offset} requestId={RequestId}",
                    saved ? "Saved" : "Duplicate skipped", evt.EventId, evt.Booking.Id, record.Partition.Value, record.Offset.Value, evt.CorrelationId);
                return;
            }
            catch (PostgresException ex) when (!ex.IsTransient)
            {
                await DeadLetterAsync(record, "PostgreSQL " + ex.SqlState, evt.CorrelationId, ct);
                return;
            }
            catch (NpgsqlException ex) when (ex.IsTransient && attempt < MaxAttempts)
            {
                log.LogWarning("History write retry {Attempt}: {Error} requestId={RequestId}", attempt, ex.GetType().Name, evt.CorrelationId);
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), ct);
            }
        }
    }

    private async Task DeadLetterAsync(ConsumeResult<string, string> record, string reason, string? requestId, CancellationToken ct)
    {
        await store.SaveDeadLetterAsync(record.Topic, record.Partition.Value, record.Offset.Value, record.Message.Value, reason, requestId, ct);
        log.LogError("Dead letter {Partition}@{Offset}: {Reason} requestId={RequestId}", record.Partition.Value, record.Offset.Value, reason, requestId);
    }

    /// <summary>Разбирает BookingCreated версии 1. Иначе возвращает причину для dead letter.</summary>
    public static bool TryParse(string? payload, [NotNullWhen(true)] out BookingCreated? evt, out string reason)
    {
        try
        {
            evt = payload is null ? null : JsonSerializer.Deserialize<BookingCreated>(payload, Json);
        }
        catch (JsonException ex)
        {
            (evt, reason) = (null, "invalid JSON or missing field: " + ex.Path);
            return false;
        }
        reason = evt switch
        {
            null => "empty message",
            { EventType: not "BookingCreated" } => "unsupported eventType " + evt.EventType,
            { SchemaVersion: not 1 } => "unsupported schemaVersion " + evt.SchemaVersion,
            _ when evt.EventId == Guid.Empty || evt.Booking.Id <= 0 => "empty eventId or booking.id",
            _ when string.IsNullOrWhiteSpace(evt.Booking.UserId) || string.IsNullOrWhiteSpace(evt.Booking.HotelId) => "empty userId or hotelId",
            _ => "",
        };
        if (evt is not null && reason.Length == 0) return true;
        evt = null;
        return false;
    }
}
