using Hotelio.BookingHistory;
using Hotelio.Bookings.Data;
using Hotelio.Bookings.Domain;
using Hotelio.Bookings.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Hotelio.Bookings.IntegrationTests;

/// <summary>Outbox и доставка BookingCreated до истории на настоящих PostgreSQL и Kafka.</summary>
public sealed class OutboxTests(Infrastructure infra) : IClassFixture<Infrastructure>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Booking_and_its_event_are_saved_together()
    {
        await using var db = await infra.BookingDatabaseAsync("outbox_saved");

        var booking = await new BookingStore(db).CreateAsync("user-1", "hotel-1", "PROMO10", new Price(100m, 10m, 90m), "req-1", Ct);

        (await db.ScalarAsync<long>("select count(*) from booking")).ShouldBe(1);
        (await db.ScalarAsync<Guid>("select event_id from outbox where aggregate_id = @id", ("id", booking.Id)))
            .ShouldBe(BookingCreatedEvent.IdFor(booking.Id));
        (await db.ScalarAsync<string>("select correlation_id from outbox")).ShouldBe("req-1");
    }

    [Fact]
    public async Task Booking_is_rolled_back_when_its_event_cannot_be_saved()
    {
        await using var db = await infra.BookingDatabaseAsync("outbox_rollback");
        // Первое бронирование в новой БД получит id 1. Занимаем event_id его события заранее — вставка в outbox упадёт.
        await db.ExecuteAsync("""
            insert into outbox (event_id, aggregate_id, event_type, correlation_id, payload)
            values (@eventId, 1, 'BookingCreated', 'test', '{}')
            """, ("eventId", BookingCreatedEvent.IdFor(1)));

        var error = await Should.ThrowAsync<PostgresException>(() =>
            new BookingStore(db).CreateAsync("user-1", "hotel-1", null, new Price(100m, 0m, 100m), "req-2", Ct));

        error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        error.ConstraintName.ShouldBe("outbox_event_id_key");
        (await db.ScalarAsync<long>("select count(*) from booking")).ShouldBe(0);
    }

    [Fact]
    public async Task Event_reaches_history_once_even_if_published_twice()
    {
        const string topic = "booking-events-it", group = "booking-history-it";
        await infra.CreateTopicAsync(topic);
        await using var bookings = await infra.BookingDatabaseAsync("delivery_bookings");
        await using var history = await infra.HistoryDatabaseAsync("delivery_history");
        var config = KafkaConfig(topic, group);
        var booking = await new BookingStore(bookings).CreateAsync("user-2", "hotel-1", null, new Price(80m, 0m, 80m), "req-3", Ct);

        using var publisher = new OutboxPublisher(bookings, config, NullLogger<OutboxPublisher>.Instance);
        using var consumer = new BookingEventsConsumer(new HistoryStore(history), config, NullLogger<BookingEventsConsumer>.Instance);
        try
        {
            await publisher.StartAsync(Ct);
            await consumer.StartAsync(Ct);
            await Eventually(async () =>
                await history.ScalarAsync<long>("select count(*) from booking_history where booking_id = @id", ("id", booking.Id)) == 1);

            // Повторная публикация, как после сбоя между отправкой в Kafka и отметкой в outbox.
            await bookings.ExecuteAsync("update outbox set published_at = null");
            await Eventually(async () => await bookings.ScalarAsync<long>("select count(*) from outbox where published_at is null") == 0);

            // Ждём, пока группа подтвердит всё, что есть в топике, — сколько бы раз событие ни ушло в Kafka.
            var end = await infra.EndOffsetAsync(topic);
            end.ShouldBeGreaterThanOrEqualTo(2);
            await Eventually(async () => await infra.CommittedOffsetAsync(group, topic) == end);

            (await history.ScalarAsync<long>("select count(*) from booking_history")).ShouldBe(1);
            // Повтор отброшен как дубль по event_id, а не отложен в dead letter как ошибка.
            (await history.ScalarAsync<long>("select count(*) from dead_letter")).ShouldBe(0);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Event_waits_in_outbox_while_kafka_is_down()
    {
        const string topic = "booking-events-down", group = "booking-history-down";
        await infra.CreateTopicAsync(topic);
        await using var bookings = await infra.BookingDatabaseAsync("kafka_down_bookings");
        await using var history = await infra.HistoryDatabaseAsync("kafka_down_history");
        var config = KafkaConfig(topic, group);

        using var publisher = new OutboxPublisher(bookings, config, NullLogger<OutboxPublisher>.Instance);
        using var consumer = new BookingEventsConsumer(new HistoryStore(history), config, NullLogger<BookingEventsConsumer>.Instance);
        await infra.PauseKafkaAsync();
        try
        {
            await publisher.StartAsync(Ct);
            var booking = await new BookingStore(bookings).CreateAsync("user-3", "hotel-1", null, new Price(70m, 0m, 70m), "req-4", Ct);

            // Kafka недоступна: бронирование сохранено, попытки отправки идут, но событие не отмечено отправленным.
            await Eventually(async () => await bookings.ScalarAsync<int>("select failure_count from outbox") > 0);
            (await bookings.ScalarAsync<long>("select count(*) from outbox where published_at is not null")).ShouldBe(0);

            await infra.ResumeKafkaAsync();
            await consumer.StartAsync(Ct);
            await Eventually(async () =>
                await history.ScalarAsync<long>("select count(*) from booking_history where booking_id = @id", ("id", booking.Id)) == 1);
        }
        finally
        {
            await infra.ResumeKafkaAsync();
            await consumer.StopAsync(CancellationToken.None);
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Offset_is_committed_only_after_history_is_written()
    {
        const string topic = "booking-events-order", group = "booking-history-order";
        await infra.CreateTopicAsync(topic);
        await using var bookings = await infra.BookingDatabaseAsync("order_bookings");
        await using var history = await infra.HistoryDatabaseAsync("order_history");
        var config = KafkaConfig(topic, group);
        var booking = await new BookingStore(bookings).CreateAsync("user-4", "hotel-1", null, new Price(60m, 0m, 60m), "req-5", Ct);

        using var publisher = new OutboxPublisher(bookings, config, NullLogger<OutboxPublisher>.Instance);
        using var first = new BookingEventsConsumer(new HistoryStore(history), config, NullLogger<BookingEventsConsumer>.Instance);
        using var second = new BookingEventsConsumer(new HistoryStore(history), config, NullLogger<BookingEventsConsumer>.Instance);

        // Пока таблица истории заблокирована, вставка в неё ждёт.
        await using var locker = await history.OpenConnectionAsync(Ct);
        await using var lockTx = await locker.BeginTransactionAsync(Ct);
        await using (var lockTable = new NpgsqlCommand("lock table booking_history in access exclusive mode", locker, lockTx))
            await lockTable.ExecuteNonQueryAsync(Ct);
        try
        {
            await publisher.StartAsync(Ct);
            await first.StartAsync(Ct);
            // Первый потребитель прочитал событие и ждёт блокировку на вставке в историю — останавливаем его.
            await Eventually(async () => await history.ScalarAsync<long>(
                "select count(*) from pg_locks where relation = 'booking_history'::regclass and not granted") == 1);
            await first.StopAsync(CancellationToken.None);
            (await infra.CommittedOffsetAsync(group, topic)).ShouldBe(0);

            // История снова доступна: второй потребитель той же группы читает событие заново.
            await lockTx.RollbackAsync(Ct);
            await second.StartAsync(Ct);
            await Eventually(async () =>
                await history.ScalarAsync<long>("select count(*) from booking_history where booking_id = @id", ("id", booking.Id)) == 1);
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
            await second.StopAsync(CancellationToken.None);
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Event_is_read_again_after_history_database_was_down()
    {
        const string topic = "booking-events-db-down", group = "booking-history-db-down";
        await infra.CreateTopicAsync(topic);
        await using var bookings = await infra.BookingDatabaseAsync("db_down_bookings");
        await using var history = await infra.HistoryDatabaseAsync("db_down_history");
        // На этом порту никто не слушает: каждое подключение к БД истории сразу отклоняется.
        await using var unreachable = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=history;Username=history;Timeout=1");
        var config = KafkaConfig(topic, group);
        var booking = await new BookingStore(bookings).CreateAsync("user-5", "hotel-1", null, new Price(50m, 0m, 50m), "req-6", Ct);

        var firstLog = new RecordingLogger<BookingEventsConsumer>();
        using var publisher = new OutboxPublisher(bookings, config, NullLogger<OutboxPublisher>.Instance);
        using var first = new BookingEventsConsumer(new HistoryStore(unreachable), config, firstLog);
        using var second = new BookingEventsConsumer(new HistoryStore(history), config, NullLogger<BookingEventsConsumer>.Instance);
        try
        {
            await publisher.StartAsync(Ct);
            await first.StartAsync(Ct);
            // Повторы записи исчерпаны (около 15 с): смещение не подтверждено, событие будет прочитано снова.
            await Eventually(() => Task.FromResult(firstLog.Contains("History unavailable")));
            await first.StopAsync(CancellationToken.None);
            (await infra.CommittedOffsetAsync(group, topic)).ShouldBe(0);

            await second.StartAsync(Ct);
            await Eventually(async () =>
                await history.ScalarAsync<long>("select count(*) from booking_history where booking_id = @id", ("id", booking.Id)) == 1);
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
            await second.StopAsync(CancellationToken.None);
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    private IConfiguration KafkaConfig(string topic, string group) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KAFKA_BOOTSTRAP"] = infra.KafkaBootstrap, ["KAFKA_TOPIC"] = topic, ["KAFKA_GROUP"] = group,
        })
        .Build();

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!await condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "условие не выполнилось за минуту");
            await Task.Delay(500, Ct);
        }
    }
}
