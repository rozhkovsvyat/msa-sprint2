using System.Text.Json;
using System.Text.Json.Nodes;
using Hotelio.Bookings.Data;

namespace Hotelio.Bookings.Tests.Support;

/// <summary>Событие BookingCreated, которое booking-service пишет в outbox для бронирования 42.</summary>
internal static class SampleEvent
{
    public static readonly StoredBooking Booking = new(42, "test-user-2", "test-hotel-1", "TESTCODE1", 10m, 90m,
        DateTimeOffset.Parse("2026-10-19T08:21:34Z"), CreatedAtEstimated: false);

    public static string Json() =>
        JsonSerializer.Serialize(BookingCreatedEvent.From(Booking, "booking-service", "client-request-1"), BookingStore.Json);

    /// <summary>То же событие с заменённым полем; null удаляет поле. Путь через точку: "booking.userId".</summary>
    public static string With(string path, JsonNode? value)
    {
        var json = JsonNode.Parse(Json())!;
        var parts = path.Split('.');
        var parent = parts.SkipLast(1).Aggregate(json, (node, name) => node[name]!);
        if (value is null) parent.AsObject().Remove(parts[^1]);
        else parent[parts[^1]] = value;
        return json.ToJsonString();
    }
}
