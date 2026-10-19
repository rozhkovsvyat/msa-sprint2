using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hotelio.Bookings.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Hotelio.Bookings.Data;

public sealed record StoredBooking(
    long Id, string UserId, string HotelId, string? PromoCode,
    decimal DiscountAmount, decimal Price, DateTimeOffset CreatedAt, bool CreatedAtEstimated);

/// <summary>Хранилище бронирований. Бронирование и событие о нём пишутся одной транзакцией (outbox).</summary>
public sealed class BookingStore(NpgsqlDataSource db)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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

        var evt = BookingCreatedEvent.From(booking, "booking-service", requestId);
        await using (var outbox = new NpgsqlCommand("""
            insert into outbox (event_id, aggregate_id, event_type, correlation_id, payload)
            values (@eventId, @bookingId, @eventType, @correlation, @payload)
            """, conn, tx))
        {
            outbox.Parameters.AddWithValue("eventId", evt.EventId);
            outbox.Parameters.AddWithValue("bookingId", booking.Id);
            outbox.Parameters.AddWithValue("eventType", evt.EventType);
            outbox.Parameters.AddWithValue("correlation", requestId);
            outbox.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(evt, Json) });
            await outbox.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return booking;
    }

    public async Task<IReadOnlyList<StoredBooking>> ListAsync(string? userId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            select id, user_id, hotel_id, promo_code, discount_amount, price, created_at, created_at_estimated
            from booking where @user::text is null or user_id = @user order by id
            """);
        cmd.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Text) { Value = (object?)userId ?? DBNull.Value });

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

/// <summary>Событие BookingCreated, версия схемы 1. Контракт: contracts/booking-events.asyncapi.yaml.</summary>
public sealed record BookingCreatedEvent(
    Guid EventId, string EventType, int SchemaVersion, DateTimeOffset OccurredAt,
    string Source, string CorrelationId, BookingPayload Booking)
{
    public static BookingCreatedEvent From(StoredBooking b, string source, string correlationId) => new(
        IdFor(b.Id), "BookingCreated", 1, DateTimeOffset.UtcNow, source, correlationId,
        new BookingPayload(b.Id, b.UserId, b.HotelId, b.PromoCode, b.DiscountAmount, b.Price, b.CreatedAt, b.CreatedAtEstimated));

    // eventId выводится из id бронирования: повторный перенос того же бронирования не создаёт нового события.
    // Та же формула в migration/migrate-bookings.sh: md5('hotelio:BookingCreated:v1:' || id)::uuid.
    public static Guid IdFor(long bookingId) => Guid.ParseExact(Convert.ToHexString(
        MD5.HashData(Encoding.UTF8.GetBytes($"hotelio:BookingCreated:v1:{bookingId}"))), "N");
}

public sealed record BookingPayload(
    long Id, string UserId, string HotelId, string? PromoCode,
    decimal DiscountAmount, decimal Price, DateTimeOffset CreatedAt, bool CreatedAtEstimated);
