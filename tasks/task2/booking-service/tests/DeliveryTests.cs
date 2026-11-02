using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using AutoFixture;
using Confluent.Kafka;
using Hotelio.Bookings.Data;
using Hotelio.Bookings.Outbox;
using Shouldly;
using Xunit;
using BookingEventsConsumer = Hotelio.BookingHistory.BookingEventsConsumer;
using HistoryEvent = Hotelio.BookingHistory.BookingCreated;
using HistoryPayload = Hotelio.BookingHistory.BookingPayload;

namespace Hotelio.Bookings.Tests;

/// <summary>Контракт события BookingCreated и классификация ошибок доставки.</summary>
public sealed class DeliveryTests
{
    // Бронирования со случайными значениями: история должна прочитать любые без потерь.
    private static readonly Fixture Any = new();

    [Fact]
    public void Event_id_follows_the_formula_of_the_migration_script() =>
        // select md5('hotelio:BookingCreated:v1:42')::uuid
        BookingCreatedEvent.IdFor(42).ToString().ShouldBe("17caf38f-7d1e-10d3-e3b7-bf33e8d71a97");

    [Fact]
    public void Event_from_booking_service_is_read_by_history_service()
    {
        var booking = Any.Create<StoredBooking>();
        var requestId = Any.Create<string>();
        var json = EventJson(booking, requestId);

        TryParse(json, out var evt, out var reason).ShouldBeTrue(reason);

        evt.ShouldSatisfyAllConditions(
            () => evt.EventId.ShouldBe(BookingCreatedEvent.IdFor(booking.Id)),
            () => evt.CorrelationId.ShouldBe(requestId),
            () => evt.Booking.ShouldBe(new HistoryPayload(booking.Id, booking.UserId, booking.HotelId, booking.DiscountAmount,
                booking.Price, booking.CreatedAt, booking.CreatedAtEstimated, booking.PromoCode)));
        // Сумма скидки называется discountAmount: имя discountPercent из монолита в событие не переносим.
        JsonNode.Parse(json)!["booking"]!["discountAmount"]!.GetValue<decimal>().ShouldBe(booking.DiscountAmount);
    }

    [Fact]
    public void Event_without_promo_code_is_valid()
    {
        var json = EventJson(Any.Create<StoredBooking>() with { PromoCode = null }, Any.Create<string>());

        TryParse(json, out var evt, out var reason).ShouldBeTrue(reason);
        evt.Booking.PromoCode.ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Unreadable_message_is_rejected_with_a_reason(string payload)
    {
        TryParse(payload, out _, out var reason).ShouldBeFalse();
        reason.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("eventType", "\"BookingCancelled\"")]
    [InlineData("booking.userId", null)]
    [InlineData("booking.userId", "\"\"")]
    [InlineData("booking.price", null)]
    [InlineData("correlationId", null)]
    public void Unsupported_version_type_or_missing_fields_are_rejected(string path, string? value)
    {
        var payload = Changed(EventJson(Any.Create<StoredBooking>(), Any.Create<string>()), path, value is null ? null : JsonNode.Parse(value));

        TryParse(payload, out _, out var reason).ShouldBeFalse(payload);
        reason.ShouldNotBeEmpty();
    }

    [Fact]
    public void Message_that_is_not_valid_utf8_is_rejected()
    {
        var booking = Any.Create<StoredBooking>();
        var payload = Encoding.UTF8.GetBytes(EventJson(booking, Any.Create<string>()));
        payload[payload.AsSpan().IndexOf(Encoding.UTF8.GetBytes(booking.UserId))] = 0xC0; // такого байта в UTF-8 не бывает

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

    private static string EventJson(StoredBooking booking, string requestId) =>
        BookingCreatedEvent.From(booking, requestId).ToJson();

    /// <summary>Событие с заменённым полем; null удаляет поле. Путь через точку: "booking.userId".</summary>
    private static string Changed(string json, string path, JsonNode? value)
    {
        var root = JsonNode.Parse(json)!;
        var parts = path.Split('.');
        var parent = parts.SkipLast(1).Aggregate(root, (node, name) => node[name]!);
        if (value is null) parent.AsObject().Remove(parts[^1]);
        else parent[parts[^1]] = value;
        return root.ToJsonString();
    }

    private static bool TryParse(string payload, [NotNullWhen(true)] out HistoryEvent? evt, out string reason) =>
        BookingEventsConsumer.TryParse(Encoding.UTF8.GetBytes(payload), out evt, out reason);
}
