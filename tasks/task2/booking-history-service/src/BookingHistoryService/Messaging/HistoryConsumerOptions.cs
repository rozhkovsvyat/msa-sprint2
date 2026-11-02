namespace Hotelio.BookingHistory.Messaging;

/// <summary>Настройки чтения booking-events из окружения.</summary>
public sealed class HistoryConsumerOptions
{
    [ConfigurationKeyName("KAFKA_BOOTSTRAP")]
    public string KafkaBootstrap { get; init; } = "";

    [ConfigurationKeyName("KAFKA_TOPIC")]
    public string Topic { get; init; } = "booking-events";

    [ConfigurationKeyName("KAFKA_GROUP")]
    public string Group { get; init; } = "booking-history";

    /// <summary>По умолчанию — имя топика с суффиксом .dlq.</summary>
    [ConfigurationKeyName("KAFKA_DLQ_TOPIC")]
    public string? DeadLetterTopic { get; init; }
}
