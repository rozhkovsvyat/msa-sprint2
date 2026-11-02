using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hotelio.Bookings.Data;

namespace Hotelio.Bookings.Outbox;

/// <summary>Событие BookingCreated, версия схемы 1. Контракт: contracts/booking-events.asyncapi.yaml.</summary>
public sealed record BookingCreatedEvent(
    Guid EventId, string EventType, int SchemaVersion, DateTimeOffset OccurredAt,
    string Source, string CorrelationId, BookingPayload Booking)
{
    /// <summary>Источник событий сервиса; события джобы переноса помечены как migration.</summary>
    public const string ServiceSource = "booking-service";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static BookingCreatedEvent From(StoredBooking b, string correlationId) => new(
        IdFor(b.Id), "BookingCreated", 1, DateTimeOffset.UtcNow, ServiceSource, correlationId,
        new BookingPayload(b.Id, b.UserId, b.HotelId, b.PromoCode, b.DiscountAmount, b.Price, b.CreatedAt, b.CreatedAtEstimated));

    // eventId выводится из id бронирования: повторный перенос того же бронирования не создаёт нового события.
    // Та же формула в migration/migrate-bookings.sh: md5('hotelio:BookingCreated:v1:' || id)::uuid.
    public static Guid IdFor(long bookingId) => Guid.ParseExact(Convert.ToHexString(
        MD5.HashData(Encoding.UTF8.GetBytes($"hotelio:BookingCreated:v1:{bookingId}"))), "N");

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>Бронирование в событии. Сумма скидки — DiscountAmount: имя discountPercent монолита в контракт не переносим.</summary>
public sealed record BookingPayload(
    long Id, string UserId, string HotelId, string? PromoCode,
    decimal DiscountAmount, decimal Price, DateTimeOffset CreatedAt, bool CreatedAtEstimated);
