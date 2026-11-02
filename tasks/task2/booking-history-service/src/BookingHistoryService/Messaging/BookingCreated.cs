namespace Hotelio.BookingHistory.Messaging;

/// <summary>Событие BookingCreated, версия схемы 1. Контракт: contracts/booking-events.asyncapi.yaml.</summary>
public sealed record BookingCreated(
    Guid EventId, string EventType, int SchemaVersion, DateTimeOffset OccurredAt,
    string Source, string CorrelationId, BookingPayload Booking);

// PromoCode последний и необязательный: в событии этого поля может не быть.
public sealed record BookingPayload(
    long Id, string UserId, string HotelId, decimal DiscountAmount, decimal Price,
    DateTimeOffset CreatedAt, bool CreatedAtEstimated, string? PromoCode = null);
