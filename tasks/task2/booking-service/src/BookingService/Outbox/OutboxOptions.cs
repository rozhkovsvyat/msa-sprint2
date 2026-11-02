namespace Hotelio.Bookings.Outbox;

/// <summary>Настройки публикации и очистки outbox из окружения.</summary>
public sealed class OutboxOptions
{
    [ConfigurationKeyName("KAFKA_BOOTSTRAP")]
    public string KafkaBootstrap { get; init; } = "";

    [ConfigurationKeyName("KAFKA_TOPIC")]
    public string Topic { get; init; } = "booking-events";

    // Дольше, чем Kafka хранит booking-events: после долгого простоя потребителя событие можно отправить заново.
    [ConfigurationKeyName("OUTBOX_RETENTION_DAYS")]
    public int RetentionDays { get; init; } = 14;
}
