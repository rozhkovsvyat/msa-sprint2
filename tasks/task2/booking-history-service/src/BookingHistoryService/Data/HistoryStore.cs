using Hotelio.BookingHistory.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Hotelio.BookingHistory.Data;

/// <summary>Таблица booking_history: событие записывается один раз, повтор с тем же event_id не меняет историю.</summary>
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
        // В timestamptz Npgsql пишет только время в UTC, а дата в событии может прийти с другим смещением.
        cmd.Parameters.AddWithValue("bookedAt", b.CreatedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("estimated", b.CreatedAtEstimated);
        cmd.Parameters.AddWithValue("source", e.Source);
        cmd.Parameters.AddWithValue("correlation", e.CorrelationId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
