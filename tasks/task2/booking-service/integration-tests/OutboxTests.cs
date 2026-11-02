using System.Globalization;
using System.Text;
using Hotelio.BookingHistory;
using Hotelio.Bookings.Data;
using Hotelio.Bookings.Domain;
using Hotelio.Bookings.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        await using var stand = await DeliveryStand.CreateAsync(infra, "it");
        var booking = await new BookingStore(stand.Bookings).CreateAsync("user-2", "hotel-1", null, new Price(80m, 0m, 80m), "req-3", Ct);

        await stand.StartPublisherAsync();
        await stand.StartConsumerAsync();
        await Eventually(async () => await stand.HistoryRowsAsync(booking.Id) == 1);

        // Повторная публикация, как после сбоя между отправкой в Kafka и отметкой в outbox.
        await stand.Bookings.ExecuteAsync("update outbox set published_at = null");
        await Eventually(async () => await stand.Bookings.ScalarAsync<long>("select count(*) from outbox where published_at is null") == 0);

        // Ждём, пока группа подтвердит всё, что есть в топике, — сколько бы раз событие ни ушло в Kafka.
        var end = await infra.EndOffsetAsync(stand.Topic);
        end.ShouldBeGreaterThanOrEqualTo(2);
        await Eventually(async () => await infra.CommittedOffsetAsync(stand.Group, stand.Topic) == end);

        (await stand.History.ScalarAsync<long>("select count(*) from booking_history")).ShouldBe(1);
        // Повтор отброшен как дубль по event_id, а не отложен в DLQ как ошибка.
        (await infra.EndOffsetAsync(stand.Topic + ".dlq")).ShouldBe(0);
    }

    [Fact]
    public async Task Event_waits_in_outbox_while_kafka_is_down()
    {
        await using var stand = await DeliveryStand.CreateAsync(infra, "down");

        await infra.PauseKafkaAsync();
        try
        {
            await stand.StartPublisherAsync();
            var booking = await new BookingStore(stand.Bookings).CreateAsync("user-3", "hotel-1", null, new Price(70m, 0m, 70m), "req-4", Ct);

            // Kafka недоступна: бронирование сохранено, попытки отправки идут, но событие не отмечено отправленным.
            await Eventually(async () => await stand.Bookings.ScalarAsync<int>("select failure_count from outbox") > 0);
            (await stand.Bookings.ScalarAsync<long>("select count(*) from outbox where published_at is not null")).ShouldBe(0);

            await infra.ResumeKafkaAsync();
            await stand.StartConsumerAsync();
            await Eventually(async () => await stand.HistoryRowsAsync(booking.Id) == 1);
        }
        finally
        {
            await infra.ResumeKafkaAsync();
        }
    }

    [Fact]
    public async Task Offset_is_committed_only_after_history_is_written()
    {
        await using var stand = await DeliveryStand.CreateAsync(infra, "order");
        var booking = await new BookingStore(stand.Bookings).CreateAsync("user-4", "hotel-1", null, new Price(60m, 0m, 60m), "req-5", Ct);

        // Пока таблица истории заблокирована, вставка в неё ждёт.
        await using var locker = await stand.History.OpenConnectionAsync(Ct);
        await using var lockTx = await locker.BeginTransactionAsync(Ct);
        await using (var lockTable = new NpgsqlCommand("lock table booking_history in access exclusive mode", locker, lockTx))
            await lockTable.ExecuteNonQueryAsync(Ct);

        await stand.StartPublisherAsync();
        var first = await stand.StartConsumerAsync();
        // Первый потребитель прочитал событие и ждёт блокировку на вставке в историю — останавливаем его.
        await Eventually(async () => await stand.History.ScalarAsync<long>(
            "select count(*) from pg_locks where relation = 'booking_history'::regclass and not granted") == 1);
        await first.StopAsync(CancellationToken.None);
        (await infra.CommittedOffsetAsync(stand.Group, stand.Topic)).ShouldBe(0);

        // История снова доступна: второй потребитель той же группы читает событие заново.
        await lockTx.RollbackAsync(Ct);
        await stand.StartConsumerAsync();
        await Eventually(async () => await stand.HistoryRowsAsync(booking.Id) == 1);
    }

    [Theory]
    [InlineData("port_closed")]
    [InlineData("wrong_password")]
    public async Task Event_is_read_again_after_history_database_was_unavailable(string failure)
    {
        // Порт, где никто не слушает: подключение сразу отклоняется, повторы записи исчерпываются (около 15 с).
        // Неверный пароль: ошибка не временная и не повторяется, но и в DLQ событие не уходит — дело в БД, а не в сообщении.
        await using var unavailable = failure == "port_closed"
            ? NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=history;Username=history;Timeout=1")
            : infra.WithWrongPassword(failure + "_history");
        await using var stand = await DeliveryStand.CreateAsync(infra, failure);
        var booking = await new BookingStore(stand.Bookings).CreateAsync("user-5", "hotel-1", null, new Price(50m, 0m, 50m), "req-6", Ct);

        var firstLog = new RecordingLogger<BookingEventsConsumer>();
        await stand.StartPublisherAsync();
        var first = await stand.StartConsumerAsync(unavailable, firstLog);
        // Смещение не подтверждено, в DLQ ничего не ушло: событие будет прочитано снова.
        await Eventually(() => Task.FromResult(firstLog.Contains("Processing failed")));
        await first.StopAsync(CancellationToken.None);
        (await infra.CommittedOffsetAsync(stand.Group, stand.Topic)).ShouldBe(0);
        (await infra.EndOffsetAsync(stand.Topic + ".dlq")).ShouldBe(0);

        await stand.StartConsumerAsync();
        await Eventually(async () => await stand.HistoryRowsAsync(booking.Id) == 1);
    }

    [Fact]
    public async Task Message_that_cannot_be_processed_goes_to_dead_letter_topic_as_is_and_reading_continues()
    {
        await using var stand = await DeliveryStand.CreateAsync(infra, "dlq");
        var booking = await new BookingStore(stand.Bookings).CreateAsync("user-6", "hotel-1", null, new Price(40m, 0m, 40m), "req-7", Ct);

        // До события бронирования в топике два сообщения, которые не обработать: байты не в UTF-8 с ключом и заголовком
        // и событие с ценой, которая не помещается в numeric(10, 2) истории.
        byte[] garbage = [0x6E, 0x6F, 0xC0, 0xFF];
        var tooExpensive = await stand.Bookings.ScalarAsync<string>(
            "select jsonb_set(payload, '{booking,price}', '1e12')::text from outbox where aggregate_id = @id", ("id", booking.Id));
        await infra.ProduceAsync(stand.Topic, "key-1", garbage, ("correlation-id", "req-garbage"));
        await infra.ProduceAsync(stand.Topic, booking.Id.ToString(CultureInfo.InvariantCulture), Encoding.UTF8.GetBytes(tooExpensive));

        await stand.StartPublisherAsync();
        await stand.StartConsumerAsync();
        await Eventually(async () => await stand.HistoryRowsAsync(booking.Id) == 1);
        await Eventually(async () => await infra.CommittedOffsetAsync(stand.Group, stand.Topic) == 3);

        var deadLetters = infra.Read(stand.Topic + ".dlq", 2);
        var (key, value, headers) = deadLetters[0];
        key.ShouldBe("key-1");
        value.ShouldBe(garbage);
        headers["correlation-id"].ShouldBe("req-garbage");
        headers["dlq-reason"].ShouldStartWith("invalid JSON");
        headers["dlq-topic"].ShouldBe(stand.Topic);
        headers["dlq-partition"].ShouldBe("0");
        headers["dlq-offset"].ShouldBe("0");
        deadLetters[1].Headers["dlq-reason"].ShouldBe("PostgreSQL 22003");
        deadLetters[1].Headers["dlq-offset"].ShouldBe("1");
    }

    [Fact]
    public async Task Message_is_read_again_until_dead_letter_topic_accepts_it()
    {
        await using var stand = await DeliveryStand.CreateAsync(infra, "no-dlq", withDeadLetterTopic: false);
        await infra.ProduceAsync(stand.Topic, "key-1", "not a booking event"u8.ToArray());

        // DLQ-топика нет, Kafka сообщение не приняла: смещение не подтверждено.
        var firstLog = new RecordingLogger<BookingEventsConsumer>();
        var first = await stand.StartConsumerAsync(log: firstLog);
        await Eventually(() => Task.FromResult(firstLog.Contains("Processing failed")));
        await first.StopAsync(CancellationToken.None);
        (await infra.CommittedOffsetAsync(stand.Group, stand.Topic)).ShouldBe(0);

        // Топик создали: второй потребитель той же группы откладывает сообщение и идёт дальше.
        await infra.CreateTopicAsync(stand.Topic + ".dlq");
        await stand.StartConsumerAsync();
        await Eventually(async () => await infra.CommittedOffsetAsync(stand.Group, stand.Topic) == 1);
        infra.Read(stand.Topic + ".dlq", 1)[0].Value.ShouldBe("not a booking event"u8.ToArray());
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

        var cleanup = new OutboxCleanup(db, Options.Create(new OutboxOptions()), NullLogger<OutboxCleanup>.Instance);

        (await cleanup.DeleteExpiredAsync(Ct)).ShouldBe(1500);
        (await db.ScalarAsync<string>("select string_agg(correlation_id, ',' order by aggregate_id) from outbox"))
            .ShouldBe("recent,pending,quarantine");
    }

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
