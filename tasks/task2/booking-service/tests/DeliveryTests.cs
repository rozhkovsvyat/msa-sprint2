using Confluent.Kafka;
using Hotelio.Bookings.Data;
using Hotelio.Bookings.Outbox;
using Hotelio.Bookings.Tests.Support;
using Shouldly;
using Xunit;
using BookingEventsConsumer = Hotelio.BookingHistory.BookingEventsConsumer;
using HistoryPayload = Hotelio.BookingHistory.BookingPayload;

namespace Hotelio.Bookings.Tests;

/// <summary>Контракт события BookingCreated и классификация ошибок доставки.</summary>
public sealed class DeliveryTests
{
    [Fact]
    public void Event_id_follows_the_formula_of_the_migration_script() =>
        // select md5('hotelio:BookingCreated:v1:42')::uuid
        BookingCreatedEvent.IdFor(42).ToString().ShouldBe("17caf38f-7d1e-10d3-e3b7-bf33e8d71a97");

    [Fact]
    public void Event_from_booking_service_is_read_by_history_service()
    {
        BookingEventsConsumer.TryParse(SampleEvent.Json(), out var evt, out var reason).ShouldBeTrue(reason);

        evt.ShouldSatisfyAllConditions(
            () => evt.EventId.ShouldBe(BookingCreatedEvent.IdFor(42)),
            () => evt.CorrelationId.ShouldBe("client-request-1"),
            () => evt.Booking.ShouldBe(new HistoryPayload(42, "test-user-2", "test-hotel-1", 10m, 90m,
                SampleEvent.Booking.CreatedAt, CreatedAtEstimated: false, PromoCode: "TESTCODE1")));
        SampleEvent.Json().ShouldContain("\"discountAmount\":10");
    }

    [Fact]
    public void Event_without_promo_code_is_valid()
    {
        BookingEventsConsumer.TryParse(SampleEvent.With("booking.promoCode", null), out var evt, out var reason).ShouldBeTrue(reason);
        evt.Booking.PromoCode.ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Unreadable_message_goes_to_dead_letter(string payload)
    {
        BookingEventsConsumer.TryParse(payload, out _, out var reason).ShouldBeFalse();
        reason.ShouldNotBeEmpty();
    }

    [Fact]
    public void Unsupported_version_type_or_missing_fields_go_to_dead_letter()
    {
        string[] payloads =
        [
            SampleEvent.With("schemaVersion", 2),
            SampleEvent.With("eventType", "BookingCancelled"),
            SampleEvent.With("booking.userId", null),
            SampleEvent.With("booking.userId", ""),
            SampleEvent.With("booking.price", null),
            SampleEvent.With("correlationId", null),
        ];

        foreach (var payload in payloads)
        {
            BookingEventsConsumer.TryParse(payload, out _, out var reason).ShouldBeFalse(payload);
            reason.ShouldNotBeEmpty();
        }
    }

    [Theory]
    [InlineData(ErrorCode.Local_MsgTimedOut, false)]
    [InlineData(ErrorCode.Local_Transport, false)]
    [InlineData(ErrorCode.LeaderNotAvailable, false)]
    [InlineData(ErrorCode.MsgSizeTooLarge, true)]
    [InlineData(ErrorCode.TopicAuthorizationFailed, true)]
    public void Only_unrecoverable_kafka_errors_send_the_event_to_dead_letter(ErrorCode code, bool permanent) =>
        OutboxPublisher.IsPermanent(code).ShouldBe(permanent);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(50, 30)]
    public void Retry_delay_grows_up_to_thirty_seconds(int attempt, int seconds) =>
        OutboxPublisher.RetryDelay(attempt).ShouldBe(TimeSpan.FromSeconds(seconds));
}
