using System.Diagnostics.CodeAnalysis;
using System.Text;
using Confluent.Kafka;
using Hotelio.Bookings.Data;
using Hotelio.Bookings.Outbox;
using Hotelio.Bookings.Tests.TestData;
using Shouldly;
using Xunit;
using BookingEventsConsumer = Hotelio.BookingHistory.BookingEventsConsumer;
using HistoryEvent = Hotelio.BookingHistory.BookingCreated;
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
        TryParse(SampleEvent.Json(), out var evt, out var reason).ShouldBeTrue(reason);

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
        TryParse(SampleEvent.With("booking.promoCode", null), out var evt, out var reason).ShouldBeTrue(reason);
        evt.Booking.PromoCode.ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Unreadable_message_goes_to_dead_letter(string payload)
    {
        TryParse(payload, out _, out var reason).ShouldBeFalse();
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
            TryParse(payload, out _, out var reason).ShouldBeFalse(payload);
            reason.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void Message_that_is_not_valid_utf8_goes_to_dead_letter()
    {
        var payload = Encoding.UTF8.GetBytes(SampleEvent.Json());
        payload[payload.AsSpan().IndexOf("test-user-2"u8) + "test-user-".Length] = 0xC0; // такого байта в UTF-8 не бывает

        BookingEventsConsumer.TryParse(payload, out _, out var reason).ShouldBeFalse();
        reason.ShouldStartWith("invalid JSON");
    }

    [Theory]
    [InlineData("22003", true)]  // число не помещается в numeric(10, 2)
    [InlineData("23502", true)]  // NOT NULL
    [InlineData("28P01", false)] // неверный пароль
    [InlineData("42501", false)] // нет прав на таблицу
    [InlineData("42P01", false)] // нет таблицы
    [InlineData("25006", false)] // реплика только для чтения
    public void Only_data_rejected_by_postgresql_sends_the_message_to_dead_letter(string sqlState, bool toDeadLetter) =>
        BookingEventsConsumer.IsDataError(sqlState).ShouldBe(toDeadLetter);

    [Theory]
    [InlineData(ErrorCode.Local_MsgTimedOut, false)]
    [InlineData(ErrorCode.Local_Transport, false)]
    [InlineData(ErrorCode.LeaderNotAvailable, false)]
    [InlineData(ErrorCode.MsgSizeTooLarge, true)]
    [InlineData(ErrorCode.TopicAuthorizationFailed, true)]
    public void Only_unrecoverable_kafka_errors_put_the_event_in_quarantine(ErrorCode code, bool permanent) =>
        OutboxPublisher.IsPermanent(code).ShouldBe(permanent);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(50, 30)]
    public void Retry_delay_grows_up_to_thirty_seconds(int attempt, int seconds) =>
        OutboxPublisher.RetryDelay(attempt).ShouldBe(TimeSpan.FromSeconds(seconds));

    private static bool TryParse(string payload, [NotNullWhen(true)] out HistoryEvent? evt, out string reason) =>
        BookingEventsConsumer.TryParse(Encoding.UTF8.GetBytes(payload), out evt, out reason);
}
