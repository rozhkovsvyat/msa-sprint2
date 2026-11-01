using Hotelio.Bookings.Domain;

namespace Hotelio.Bookings.Tests.TestData;

internal static class Hotels
{
    /// <summary>Отель, который проходит все правила бронирования.</summary>
    public static readonly HotelFacts Good = new(Exists: true, Operational: true, FullyBooked: false, Trusted: true);
}
