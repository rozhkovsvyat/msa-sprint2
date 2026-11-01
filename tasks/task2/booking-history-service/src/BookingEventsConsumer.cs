using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Npgsql;

namespace Hotelio.BookingHistory;

/// <summary>
/// Читает booking-events и подтверждает смещение только после записи в историю или в DLQ-топик.
/// Временную ошибку БД повторяет; если повторы не помогли или недоступна сама БД, сообщение читается снова.
/// </summary>
public sealed class BookingEventsConsumer(HistoryStore store, IConfiguration config, ILogger<BookingEventsConsumer> log) : BackgroundService
{
    private const int MaxAttempts = 5;

    private string Topic => config["KAFKA_TOPIC"] ?? "booking-events";
    private string DeadLetterTopic => config["KAFKA_DLQ_TOPIC"] ?? Topic + ".dlq";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    // consumer.Consume блокирует поток, поэтому цикл чтения работает в отдельном потоке и не держит запуск хоста.
    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => RunAsync(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken ct)
    {
        // Ключ и тело читаются байтами, чтобы в DLQ сообщение ушло без перекодирования.
        using var consumer = new ConsumerBuilder<byte[]?, byte[]?>(new ConsumerConfig
        {
            BootstrapServers = config["KAFKA_BOOTSTRAP"], GroupId = config["KAFKA_GROUP"] ?? "booking-history",
            AutoOffsetReset = AutoOffsetReset.Earliest, EnableAutoCommit = false,
        }).Build();
        // DLQ-топик с хранением 30 дней создаёт инфраструктура; если его нет, запись падает, а не создаёт топик с настройками брокера.
        using var deadLetters = new ProducerBuilder<byte[]?, byte[]?>(new ProducerConfig
        {
            BootstrapServers = config["KAFKA_BOOTSTRAP"], Acks = Acks.All, EnableIdempotence = true,
            MessageTimeoutMs = 10_000, AllowAutoCreateTopics = false,
        }).Build();
        consumer.Subscribe(Topic);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<byte[]?, byte[]?>? record = null;
                try
                {
                    record = consumer.Consume(ct);
                    await ProcessAsync(record, deadLetters, ct);
                    consumer.Commit(record);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    var error = ex is ProduceException<byte[]?, byte[]?> ? $"DLQ {DeadLetterTopic}: {ex.Message}" : ex.Message;
                    log.LogWarning("Processing failed: {Error}; offset not committed, message will be read again", error);
                    if (record is not null) consumer.Seek(record.TopicPartitionOffset);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
        }
        finally { consumer.Close(); }
    }

    private async Task ProcessAsync(ConsumeResult<byte[]?, byte[]?> record, IProducer<byte[]?, byte[]?> deadLetters, CancellationToken ct)
    {
        if (!TryParse(record.Message.Value, out var evt, out var reason))
        {
            var requestId = record.Message.Headers?.TryGetLastBytes("correlation-id", out var bytes) == true
                ? Encoding.UTF8.GetString(bytes) : null;
            await DeadLetterAsync(deadLetters, record, reason, requestId, ct);
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
            catch (PostgresException ex) when (IsDataError(ex.SqlState))
            {
                await DeadLetterAsync(deadLetters, record, "PostgreSQL " + ex.SqlState, evt.CorrelationId, ct);
                return;
            }
            catch (NpgsqlException ex) when (ex.IsTransient && attempt < MaxAttempts)
            {
                log.LogWarning("History write retry {Attempt}: {Error} requestId={RequestId}", attempt, ex.GetType().Name, evt.CorrelationId);
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), ct);
            }
        }
    }

    /// <summary>
    /// PostgreSQL отверг данные сообщения (классы SQLSTATE 22 и 23): повтор не поможет, сообщение уходит в DLQ.
    /// Вход, права, схема, реплика только для чтения — ошибки самой БД: сообщение будет прочитано снова.
    /// </summary>
    public static bool IsDataError(string sqlState) =>
        sqlState.StartsWith("22", StringComparison.Ordinal) || sqlState.StartsWith("23", StringComparison.Ordinal);

    private async Task DeadLetterAsync(IProducer<byte[]?, byte[]?> deadLetters, ConsumeResult<byte[]?, byte[]?> record, string reason, string? requestId, CancellationToken ct)
    {
        var headers = new Headers();
        foreach (var header in record.Message.Headers ?? []) headers.Add(header.Key, header.GetValueBytes());
        headers.Add("dlq-reason", Encoding.UTF8.GetBytes(reason));
        headers.Add("dlq-topic", Encoding.UTF8.GetBytes(record.Topic));
        headers.Add("dlq-partition", Encoding.UTF8.GetBytes(record.Partition.Value.ToString(CultureInfo.InvariantCulture)));
        headers.Add("dlq-offset", Encoding.UTF8.GetBytes(record.Offset.Value.ToString(CultureInfo.InvariantCulture)));
        // Если Kafka не приняла сообщение, исключение не даст подтвердить смещение.
        await deadLetters.ProduceAsync(DeadLetterTopic, new Message<byte[]?, byte[]?> { Key = record.Message.Key, Value = record.Message.Value, Headers = headers }, ct);
        log.LogError("Dead letter {Partition}@{Offset}: {Reason} requestId={RequestId}", record.Partition.Value, record.Offset.Value, reason, requestId);
    }

    /// <summary>Разбирает BookingCreated версии 1. Иначе возвращает причину для DLQ.</summary>
    public static bool TryParse(byte[]? payload, [NotNullWhen(true)] out BookingCreated? evt, out string reason)
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
