using Hotelio.Bookings.Outbox;
using Hotelio.Bookings.Services;
using Npgsql;
using NpgsqlTypes;

namespace Hotelio.Bookings.Data;

public sealed record StoredBooking(
    long Id, string UserId, string HotelId, string? PromoCode,
    decimal DiscountAmount, decimal Price, DateTimeOffset CreatedAt, bool CreatedAtEstimated);

/// <summary>Хранилище бронирований. Бронирование и событие о нём пишутся одной транзакцией (outbox).</summary>
public sealed class BookingStore(NpgsqlDataSource db)
{
    private const string Columns =
        "id, user_id, hotel_id, promo_code, discount_amount, price, created_at, created_at_estimated";

    public async Task<StoredBooking> CreateAsync(string userId, string hotelId, string? promoCode, Price price, string requestId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        StoredBooking booking;
        await using (var insert = new NpgsqlCommand("""
            insert into booking (user_id, hotel_id, promo_code, discount_amount, price, created_at)
            values (@user, @hotel, @promo, @discount, @price, now())
            returning id, created_at
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("user", userId);
            insert.Parameters.AddWithValue("hotel", hotelId);
            insert.Parameters.Add(new NpgsqlParameter("promo", NpgsqlDbType.Text) { Value = (object?)promoCode ?? DBNull.Value });
            insert.Parameters.AddWithValue("discount", price.Discount);
            insert.Parameters.AddWithValue("price", price.FinalPrice);
            await using var reader = await insert.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            booking = new StoredBooking(reader.GetInt64(0), userId, hotelId, promoCode,
                price.Discount, price.FinalPrice, reader.GetFieldValue<DateTimeOffset>(1), false);
        }

        var evt = BookingCreatedEvent.From(booking, requestId);
        await using (var outbox = new NpgsqlCommand("""
            insert into outbox (event_id, aggregate_id, event_type, correlation_id, payload)
            values (@eventId, @bookingId, @eventType, @correlation, @payload)
            """, conn, tx))
        {
            outbox.Parameters.AddWithValue("eventId", evt.EventId);
            outbox.Parameters.AddWithValue("bookingId", booking.Id);
            outbox.Parameters.AddWithValue("eventType", evt.EventType);
            outbox.Parameters.AddWithValue("correlation", requestId);
            outbox.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = evt.ToJson() });
            await outbox.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return booking;
    }

    public async Task<IReadOnlyList<StoredBooking>> ListAsync(string userId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"select {Columns} from booking where user_id = @user order by id");
        cmd.Parameters.AddWithValue("user", userId);
        return await ReadAsync(cmd, ct);
    }

    /// <summary>Страница бронирований по возрастанию id после указанного — для служебного списка.</summary>
    public async Task<IReadOnlyList<StoredBooking>> ListPageAsync(long afterId, int limit, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"select {Columns} from booking where id > @after order by id limit @limit");
        cmd.Parameters.AddWithValue("after", afterId);
        cmd.Parameters.AddWithValue("limit", limit);
        return await ReadAsync(cmd, ct);
    }

    private static async Task<IReadOnlyList<StoredBooking>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var result = new List<StoredBooking>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new StoredBooking(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetDecimal(4), reader.GetDecimal(5),
                reader.GetFieldValue<DateTimeOffset>(6), reader.GetBoolean(7)));
        }
        return result;
    }
}
