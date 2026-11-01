using System.Globalization;
using System.Text;
using Hotelio.BookingHistory;
using Hotelio.Bookings.Data;
using Hotelio.Bookings.Domain;
using Hotelio.Bookings.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;
using HistoryPayload = Hotelio.BookingHistory.BookingPayload;

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
        await infra.CreateTopicsAsync(topic);
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
            // Повтор отброшен как дубль по event_id, а не отложен в DLQ как ошибка.
            (await infra.EndOffsetAsync(topic + ".dlq")).ShouldBe(0);
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
        await infra.CreateTopicsAsync(topic);
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
        await infra.CreateTopicsAsync(topic);
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

    [Theory]
    [InlineData("port_closed")]
    [InlineData("wrong_password")]
    public async Task Event_is_read_again_after_history_database_was_unavailable(string failure)
    {
        string topic = "booking-events-" + failure, group = "booking-history-" + failure;
        await infra.CreateTopicsAsync(topic);
        await using var bookings = await infra.BookingDatabaseAsync(failure + "_bookings");
        await using var history = await infra.HistoryDatabaseAsync(failure + "_history");
        // Порт, где никто не слушает: подключение сразу отклоняется, повторы записи исчерпываются (около 15 с).
        // Неверный пароль: ошибка не временная и не повторяется, но и в DLQ событие не уходит — дело в БД, а не в сообщении.
        await using var unavailable = failure == "port_closed"
            ? NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=history;Username=history;Timeout=1")
            : infra.WithWrongPassword(failure + "_history");
        var config = KafkaConfig(topic, group);
        var booking = await new BookingStore(bookings).CreateAsync("user-5", "hotel-1", null, new Price(50m, 0m, 50m), "req-6", Ct);

        var firstLog = new RecordingLogger<BookingEventsConsumer>();
        using var publisher = new OutboxPublisher(bookings, config, NullLogger<OutboxPublisher>.Instance);
        using var first = new BookingEventsConsumer(new HistoryStore(unavailable), config, firstLog);
        using var second = new BookingEventsConsumer(new HistoryStore(history), config, NullLogger<BookingEventsConsumer>.Instance);
        try
        {
            await publisher.StartAsync(Ct);
            await first.StartAsync(Ct);
            // Смещение не подтверждено, в DLQ ничего не ушло: событие будет прочитано снова.
            await Eventually(() => Task.FromResult(firstLog.Contains("Processing failed")));
            await first.StopAsync(CancellationToken.None);
            (await infra.CommittedOffsetAsync(group, topic)).ShouldBe(0);
            (await infra.EndOffsetAsync(topic + ".dlq")).ShouldBe(0);

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
    public async Task Message_that_cannot_be_processed_goes_to_dead_letter_topic_as_is_and_reading_continues()
    {
        const string topic = "booking-events-dlq", group = "booking-history-dlq";
        await infra.CreateTopicsAsync(topic);
        await using var bookings = await infra.BookingDatabaseAsync("dlq_bookings");
        await using var history = await infra.HistoryDatabaseAsync("dlq_history");
        var config = KafkaConfig(topic, group);
        var booking = await new BookingStore(bookings).CreateAsync("user-6", "hotel-1", null, new Price(40m, 0m, 40m), "req-7", Ct);

        // До события бронирования в топике два сообщения, которые не обработать: байты не в UTF-8 с ключом и заголовком
        // и событие с ценой, которая не помещается в numeric(10, 2) истории.
        byte[] garbage = [0x6E, 0x6F, 0xC0, 0xFF];
        var tooExpensive = await bookings.ScalarAsync<string>(
            "select jsonb_set(payload, '{booking,price}', '1e12')::text from outbox where aggregate_id = @id", ("id", booking.Id));
        await infra.ProduceAsync(topic, "key-1", garbage, ("correlation-id", "req-garbage"));
        await infra.ProduceAsync(topic, booking.Id.ToString(CultureInfo.InvariantCulture), Encoding.UTF8.GetBytes(tooExpensive));

        using var publisher = new OutboxPublisher(bookings, config, NullLogger<OutboxPublisher>.Instance);
        using var consumer = new BookingEventsConsumer(new HistoryStore(history), config, NullLogger<BookingEventsConsumer>.Instance);
        try
        {
            await publisher.StartAsync(Ct);
            await consumer.StartAsync(Ct);
            await Eventually(async () =>
                await history.ScalarAsync<long>("select count(*) from booking_history where booking_id = @id", ("id", booking.Id)) == 1);
            await Eventually(async () => await infra.CommittedOffsetAsync(group, topic) == 3);

            var deadLetters = infra.Read(topic + ".dlq", 2);
            var (key, value, headers) = deadLetters[0];
            key.ShouldBe("key-1");
            value.ShouldBe(garbage);
            headers["correlation-id"].ShouldBe("req-garbage");
            headers["dlq-reason"].ShouldStartWith("invalid JSON");
            headers["dlq-topic"].ShouldBe(topic);
            headers["dlq-partition"].ShouldBe("0");
            headers["dlq-offset"].ShouldBe("0");
            deadLetters[1].Headers["dlq-reason"].ShouldBe("PostgreSQL 22003");
            deadLetters[1].Headers["dlq-offset"].ShouldBe("1");
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Message_is_read_again_until_dead_letter_topic_accepts_it()
    {
        const string topic = "booking-events-no-dlq", group = "booking-history-no-dlq";
        await infra.CreateTopicAsync(topic);
        await using var history = await infra.HistoryDatabaseAsync("no_dlq_history");
        await infra.ProduceAsync(topic, "key-1", "not a booking event"u8.ToArray());
        var config = KafkaConfig(topic, group);

        var firstLog = new RecordingLogger<BookingEventsConsumer>();
        using var first = new BookingEventsConsumer(new HistoryStore(history), config, firstLog);
        using var second = new BookingEventsConsumer(new HistoryStore(history), config, NullLogger<BookingEventsConsumer>.Instance);
        try
        {
            // DLQ-топика нет, Kafka сообщение не приняла: смещение не подтверждено.
            await first.StartAsync(Ct);
            await Eventually(() => Task.FromResult(firstLog.Contains("Processing failed")));
            await first.StopAsync(CancellationToken.None);
            (await infra.CommittedOffsetAsync(group, topic)).ShouldBe(0);

            // Топик создали: второй потребитель той же группы откладывает сообщение и идёт дальше.
            await infra.CreateTopicAsync(topic + ".dlq");
            await second.StartAsync(Ct);
            await Eventually(async () => await infra.CommittedOffsetAsync(group, topic) == 1);
            infra.Read(topic + ".dlq", 1)[0].Value.ShouldBe("not a booking event"u8.ToArray());
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
            await second.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Booking_time_with_offset_is_saved_as_the_same_moment()
    {
        await using var history = await infra.HistoryDatabaseAsync("offset_history");
        // Перенос на сервере с московским временем отдаёт createdAt со смещением +03:00.
        var createdAt = new DateTimeOffset(2026, 10, 19, 17, 39, 58, TimeSpan.FromHours(3));
        var evt = new BookingCreated(BookingCreatedEvent.IdFor(9), "BookingCreated", 1, createdAt, "migration", "req-8",
            new HistoryPayload(9, "user-9", "hotel-1", 0m, 30m, createdAt, CreatedAtEstimated: false));

        (await new HistoryStore(history).SaveAsync(evt, Ct)).ShouldBeTrue();
        (await history.ScalarAsync<DateTime>("select booked_at from booking_history")).ShouldBe(createdAt.UtcDateTime);
    }

    [Fact]
    public async Task Cleanup_deletes_only_published_events_older_than_retention()
    {
        await using var db = await infra.BookingDatabaseAsync("outbox_cleanup");
        // 1500 событий опубликованы 15 дней назад — больше одного пакета удаления.
        await db.ExecuteAsync("""
            insert into outbox (event_id, aggregate_id, event_type, correlation_id, payload, created_at, published_at)
            select gen_random_uuid(), n, 'BookingCreated', 'old', '{}', now() - interval '16 days', now() - interval '15 days'
            from generate_series(1, 1500) n
            """);
        // Остаются: опубликованное вчера, неотправленное и в карантине — все созданы месяц назад.
        await db.ExecuteAsync("""
            insert into outbox (event_id, aggregate_id, event_type, correlation_id, payload, created_at, published_at, dead_lettered_at)
            values (gen_random_uuid(), 2001, 'BookingCreated', 'recent', '{}', now() - interval '30 days', now() - interval '1 day', null),
                   (gen_random_uuid(), 2002, 'BookingCreated', 'pending', '{}', now() - interval '30 days', null, null),
                   (gen_random_uuid(), 2003, 'BookingCreated', 'quarantine', '{}', now() - interval '30 days', null, now() - interval '30 days')
            """);

        var cleanup = new OutboxCleanup(db, new ConfigurationBuilder().Build(), NullLogger<OutboxCleanup>.Instance);

        (await cleanup.DeleteExpiredAsync(Ct)).ShouldBe(1500);
        (await db.ScalarAsync<string>("select string_agg(correlation_id, ',' order by aggregate_id) from outbox"))
            .ShouldBe("recent,pending,quarantine");
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
