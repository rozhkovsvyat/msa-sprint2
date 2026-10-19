using Npgsql;
using NpgsqlTypes;

namespace Hotelio.BookingHistory;

/// <summary>Событие BookingCreated, версия схемы 1. Контракт: contracts/booking-events.asyncapi.yaml.</summary>
public sealed record BookingCreated(
    Guid EventId, string EventType, int SchemaVersion, DateTimeOffset OccurredAt,
    string Source, string CorrelationId, BookingPayload Booking);

public sealed record BookingPayload(
    long Id, string UserId, string HotelId, decimal DiscountAmount, decimal Price,
    DateTimeOffset CreatedAt, bool CreatedAtEstimated, string? PromoCode = null);

public sealed class HistoryStore(NpgsqlDataSource db)
{
    /// <returns>true — событие записано впервые; false — повторная доставка, история не изменилась.</returns>
    public async Task<bool> SaveAsync(BookingCreated e, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            insert into booking_history (event_id, event_type, booking_id, user_id, hotel_id, promo_code,
                discount_amount, price, booked_at, booked_at_estimated, source, correlation_id)
            values (@eventId, @eventType, @bookingId, @userId, @hotelId, @promo,
                @discount, @price, @bookedAt, @estimated, @source, @correlation)
            on conflict (event_id) do nothing
            """);
        var b = e.Booking;
        cmd.Parameters.AddWithValue("eventId", e.EventId);
        cmd.Parameters.AddWithValue("eventType", e.EventType);
        cmd.Parameters.AddWithValue("bookingId", b.Id);
        cmd.Parameters.AddWithValue("userId", b.UserId);
        cmd.Parameters.AddWithValue("hotelId", b.HotelId);
        cmd.Parameters.Add(new NpgsqlParameter("promo", NpgsqlDbType.Text) { Value = (object?)b.PromoCode ?? DBNull.Value });
        cmd.Parameters.AddWithValue("discount", b.DiscountAmount);
        cmd.Parameters.AddWithValue("price", b.Price);
        cmd.Parameters.AddWithValue("bookedAt", b.CreatedAt);
        cmd.Parameters.AddWithValue("estimated", b.CreatedAtEstimated);
        cmd.Parameters.AddWithValue("source", e.Source);
        cmd.Parameters.AddWithValue("correlation", e.CorrelationId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Сохраняет сообщение с причиной; повторное чтение той же позиции Kafka вторую запись не создаёт.</summary>
    public async Task SaveDeadLetterAsync(string topic, int partition, long offset, string? payload, string error, string? correlationId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            insert into dead_letter (topic, kafka_partition, kafka_offset, payload, error, correlation_id)
            values (@topic, @partition, @offset, @payload, @error, @correlation)
            on conflict (topic, kafka_partition, kafka_offset) do nothing
            """);
        cmd.Parameters.AddWithValue("topic", topic);
        cmd.Parameters.AddWithValue("partition", partition);
        cmd.Parameters.AddWithValue("offset", offset);
        cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Text) { Value = (object?)payload ?? DBNull.Value });
        cmd.Parameters.AddWithValue("error", error);
        cmd.Parameters.Add(new NpgsqlParameter("correlation", NpgsqlDbType.Text) { Value = (object?)correlationId ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
