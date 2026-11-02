using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Xunit;
using BookingMigrations = Hotelio.Bookings.Data.SchemaMigrations;
using HistoryMigrations = Hotelio.BookingHistory.SchemaMigrations;

namespace Hotelio.Bookings.IntegrationTests;

/// <summary>PostgreSQL и Kafka в контейнерах, общие для всех тестов класса. У каждого теста своя БД, у тестов доставки — свои топики.</summary>
public sealed class Infrastructure : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:15").Build();
    private readonly KafkaContainer kafka = new KafkaBuilder("confluentinc/cp-kafka:7.2.1").Build();

    private bool kafkaPaused;

    public string KafkaBootstrap => kafka.GetBootstrapAddress();

    public async ValueTask InitializeAsync() => await Task.WhenAll(postgres.StartAsync(), kafka.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await postgres.DisposeAsync();
        await kafka.DisposeAsync();
    }

    public async Task<NpgsqlDataSource> BookingDatabaseAsync(string name)
    {
        var connectionString = await CreateDatabaseAsync(name);
        BookingMigrations.Apply(connectionString, NullLoggerFactory.Instance).ShouldBeTrue();
        return NpgsqlDataSource.Create(connectionString);
    }

    public async Task<NpgsqlDataSource> HistoryDatabaseAsync(string name)
    {
        var connectionString = await CreateDatabaseAsync(name);
        HistoryMigrations.Apply(connectionString, "analyst-password", NullLoggerFactory.Instance).ShouldBeTrue();
        return NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>Брокер «завис»: контейнер на паузе, соединения есть, ответов нет.</summary>
    public async Task PauseKafkaAsync()
    {
        await kafka.PauseAsync();
        kafkaPaused = true;
    }

    public async Task ResumeKafkaAsync()
    {
        if (!kafkaPaused) return;
        await kafka.UnpauseAsync();
        kafkaPaused = false;
    }

    public async Task CreateTopicAsync(string topic)
    {
        using var admin = Admin();
        // После долгой паузы (тест недоступности Kafka) брокер не сразу создаёт топики — до 30 попыток.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
                return;
            }
            catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
            {
                return;
            }
            catch (KafkaException) when (attempt < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    /// <summary>Топик событий и его DLQ с суффиксом .dlq, как на стенде.</summary>
    public async Task CreateTopicsAsync(string topic)
    {
        await CreateTopicAsync(topic);
        await CreateTopicAsync(topic + ".dlq");
    }

    public async Task ProduceAsync(string topic, string key, byte[] value, params (string Name, string Value)[] headers)
    {
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = KafkaBootstrap }).Build();
        var message = new Message<string, byte[]> { Key = key, Value = value, Headers = [] };
        foreach (var (name, headerValue) in headers) message.Headers.Add(name, Encoding.UTF8.GetBytes(headerValue));
        await producer.ProduceAsync(topic, message);
    }

    /// <summary>Первые сообщения партиции 0 как есть: ключ, байты значения, заголовки.</summary>
    public List<(string Key, byte[] Value, Dictionary<string, string> Headers)> Read(string topic, int count)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = KafkaBootstrap,
            GroupId = "test-reader-" + Guid.NewGuid(),
            EnableAutoCommit = false,
        }).Build();
        consumer.Assign(new TopicPartitionOffset(topic, 0, Offset.Beginning));
        var messages = Enumerable.Range(0, count)
            .Select(_ => consumer.Consume(TimeSpan.FromSeconds(30)).ShouldNotBeNull($"в {topic} меньше {count} сообщений").Message)
            .Select(m => (m.Key, m.Value, m.Headers.ToDictionary(h => h.Key, h => Encoding.UTF8.GetString(h.GetValueBytes()))))
            .ToList();
        consumer.Close();
        return messages;
    }

    /// <summary>Та же БД, но с неверным паролем: PostgreSQL отклоняет вход (28P01).</summary>
    public NpgsqlDataSource WithWrongPassword(string database) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString(database)) { Password = "wrong" }.ConnectionString);

    /// <summary>Сколько сообщений записано в топик (конец партиции 0).</summary>
    public async Task<long> EndOffsetAsync(string topic)
    {
        using var admin = Admin();
        var result = await admin.ListOffsetsAsync(
            [new TopicPartitionOffsetSpec { TopicPartition = new TopicPartition(topic, 0), OffsetSpec = OffsetSpec.Latest() }]);
        return result.ResultInfos.Single().TopicPartitionOffsetError.Offset.Value;
    }

    /// <summary>Сколько сообщений топика группа потребителя уже подтвердила.</summary>
    public async Task<long> CommittedOffsetAsync(string group, string topic)
    {
        using var admin = Admin();
        var groups = await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group, [new TopicPartition(topic, 0)])]);
        var offset = groups.Single().Partitions.Single().Offset;
        return offset.IsSpecial ? 0 : offset.Value;
    }

    private IAdminClient Admin() => new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaBootstrap }).Build();

    private async Task<string> CreateDatabaseAsync(string name)
    {
        await using var admin = new NpgsqlConnection(ConnectionString(database: null));
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"create database \"{name}\"", admin))
            await create.ExecuteNonQueryAsync();
        return ConnectionString(name);
    }

    // Как в строках подключения сервисов: Kerberos в контейнерах нет, GSS-шифрование не нужно.
    private string ConnectionString(string? database)
    {
        var builder = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { GssEncryptionMode = GssEncryptionMode.Disable };
        if (database is not null) builder.Database = database;
        return builder.ConnectionString;
    }
}
