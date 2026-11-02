using Hotelio.BookingHistory;
using Hotelio.Bookings.Outbox;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Hotelio.Bookings.IntegrationTests;

/// <summary>Свои топик, группа и две БД одного теста доставки. Запущенные publisher и потребители останавливаются при выходе из теста.</summary>
internal sealed class DeliveryStand : IAsyncDisposable
{
    private readonly Infrastructure infra;
    private readonly List<BackgroundService> started = [];

    private DeliveryStand(Infrastructure infra, string name, NpgsqlDataSource bookings, NpgsqlDataSource history)
    {
        this.infra = infra;
        Name = name;
        Bookings = bookings;
        History = history;
    }

    public string Name { get; }
    public string Topic => "booking-events-" + Name;
    public string Group => "booking-history-" + Name;
    public NpgsqlDataSource Bookings { get; }
    public NpgsqlDataSource History { get; }

    /// <param name="withDeadLetterTopic">false — топик DLQ не создаётся, как на кластере, где его забыли завести.</param>
    public static async Task<DeliveryStand> CreateAsync(Infrastructure infra, string name, bool withDeadLetterTopic = true)
    {
        if (withDeadLetterTopic) await infra.CreateTopicsAsync("booking-events-" + name);
        else await infra.CreateTopicAsync("booking-events-" + name);
        return new DeliveryStand(infra, name,
            await infra.BookingDatabaseAsync(name + "_bookings"), await infra.HistoryDatabaseAsync(name + "_history"));
    }

    public Task<OutboxPublisher> StartPublisherAsync() => StartAsync(new OutboxPublisher(Bookings,
        Options.Create(new OutboxOptions { KafkaBootstrap = infra.KafkaBootstrap, Topic = Topic }), NullLogger<OutboxPublisher>.Instance));

    /// <summary>Потребитель истории в группе стенда; history подменяет БД истории, log нужен, чтобы ждать строк лога.</summary>
    public Task<BookingEventsConsumer> StartConsumerAsync(NpgsqlDataSource? history = null, ILogger<BookingEventsConsumer>? log = null) =>
        StartAsync(new BookingEventsConsumer(new HistoryStore(history ?? History),
            Options.Create(new HistoryConsumerOptions { KafkaBootstrap = infra.KafkaBootstrap, Topic = Topic, Group = Group }),
            log ?? NullLogger<BookingEventsConsumer>.Instance));

    public Task<long> HistoryRowsAsync(long bookingId) =>
        History.ScalarAsync<long>("select count(*) from booking_history where booking_id = @id", ("id", bookingId));

    public async ValueTask DisposeAsync()
    {
        foreach (var service in Enumerable.Reverse(started))
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
        await Bookings.DisposeAsync();
        await History.DisposeAsync();
    }

    private async Task<T> StartAsync<T>(T service) where T : BackgroundService
    {
        started.Add(service);
        await service.StartAsync(TestContext.Current.CancellationToken);
        return service;
    }
}
