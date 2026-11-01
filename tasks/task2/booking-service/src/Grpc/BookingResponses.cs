using System.Globalization;
using Booking;
using Hotelio.Bookings.Data;

namespace Hotelio.Bookings.Grpc;

/// <summary>Ответ контракта booking.proto: его разбирает готовый прокси монолита.</summary>
internal static class BookingResponses
{
    public static BookingResponse ToResponse(StoredBooking b) => new()
    {
        // Прокси монолита делает Long.parseLong(id) и Instant.parse(created_at): числовой id и дата ISO-8601 в UTC.
        Id = b.Id.ToString(CultureInfo.InvariantCulture),
        UserId = b.UserId,
        HotelId = b.HotelId,
        PromoCode = b.PromoCode ?? string.Empty,
        // Поле называется discount_percent, но монолит хранит в нём сумму скидки — отдаём так же.
        DiscountPercent = (double)b.DiscountAmount,
        Price = (double)b.Price,
        CreatedAt = b.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
    };
}
