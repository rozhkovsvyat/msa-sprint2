using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using BookingEventsConsumer = Hotelio.BookingHistory.BookingEventsConsumer;
using Hotelio.Bookings.Data;
using Hotelio.Bookings.Outbox;
using Xunit;

namespace Hotelio.Bookings.Tests;

/// <summary>Контракт события BookingCreated и классификация ошибок доставки.</summary>
public sealed class DeliveryTests
{
    private static readonly StoredBooking Booking = new(42, "test-user-2", "test-hotel-1", "TESTCODE1", 10m, 90m,
        DateTimeOffset.Parse("2026-10-19T08:21:34Z"), false);

    private static string Event() =>
        JsonSerializer.Serialize(BookingCreatedEvent.From(Booking, "booking-service", "client-request-1"), BookingStore.Json);

    private static string EventWith(string path, JsonNode? value)
    {
        var json = JsonNode.Parse(Event())!;
        var parts = path.Split('.');
        var parent = parts.SkipLast(1).Aggregate(json, (node, name) => node[name]!);
        if (value is null) parent.AsObject().Remove(parts[^1]);
        else parent[parts[^1]] = value;
        return json.ToJsonString();
    }

    [Fact]
    public void Event_id_follows_the_formula_of_the_migration_script()
    {
        // select md5('hotelio:BookingCreated:v1:42')::uuid
        Assert.Equal("17caf38f-7d1e-10d3-e3b7-bf33e8d71a97", BookingCreatedEvent.IdFor(42).ToString());
    }

    [Fact]
    public void Event_from_booking_service_is_read_by_history_service()
    {
        Assert.True(BookingEventsConsumer.TryParse(Event(), out var evt, out var reason), reason);
        Assert.Equal(BookingCreatedEvent.IdFor(42), evt.EventId);
        Assert.Equal("client-request-1", evt.CorrelationId);
        Assert.Equal(new Hotelio.BookingHistory.BookingPayload(42, "test-user-2", "test-hotel-1", 10m, 90m, Booking.CreatedAt, false, "TESTCODE1"), evt.Booking);
        Assert.Contains("\"discountAmount\":10", Event());
    }

    [Fact]
    public void Event_without_promo_code_is_valid()
    {
        Assert.True(BookingEventsConsumer.TryParse(EventWith("booking.promoCode", null), out var evt, out _));
        Assert.Null(evt.Booking.PromoCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Unreadable_message_goes_to_dead_letter(string payload)
    {
        Assert.False(BookingEventsConsumer.TryParse(payload, out _, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Unsupported_version_type_or_missing_fields_go_to_dead_letter()
    {
        foreach (var payload in new[]
        {
            EventWith("schemaVersion", 2),
            EventWith("eventType", "BookingCancelled"),
            EventWith("booking.userId", null),
            EventWith("booking.userId", ""),
            EventWith("booking.price", null),
            EventWith("correlationId", null),
        })
        {
            Assert.False(BookingEventsConsumer.TryParse(payload, out _, out var reason), payload);
            Assert.NotEmpty(reason);
        }
    }

    [Theory]
    [InlineData(ErrorCode.Local_MsgTimedOut, false)]
    [InlineData(ErrorCode.Local_Transport, false)]
    [InlineData(ErrorCode.LeaderNotAvailable, false)]
    [InlineData(ErrorCode.MsgSizeTooLarge, true)]
    [InlineData(ErrorCode.TopicAuthorizationFailed, true)]
    public void Only_unrecoverable_kafka_errors_quarantine_the_event(ErrorCode code, bool permanent) =>
        Assert.Equal(permanent, OutboxPublisher.IsPermanent(code));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(50, 30)]
    public void Retry_delay_grows_up_to_thirty_seconds(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), OutboxPublisher.RetryDelay(attempt));
}
