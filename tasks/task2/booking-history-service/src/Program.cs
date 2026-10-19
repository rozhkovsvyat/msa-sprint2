using Hotelio.BookingHistory;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

if (args is ["migrate"])
{
    if (string.IsNullOrWhiteSpace(config["HISTORY_DB"]) || string.IsNullOrWhiteSpace(config["ANALYST_PASSWORD"]))
        return Fail("migrate требует HISTORY_DB и ANALYST_PASSWORD");
    using var logging = LoggerFactory.Create(b => b.AddSimpleConsole());
    return SchemaMigrations.Apply(config["HISTORY_DB"]!, config["ANALYST_PASSWORD"]!, logging) ? 0 : 1;
}

var missing = new[] { "HISTORY_DB", "KAFKA_BOOTSTRAP" }.Where(name => string.IsNullOrWhiteSpace(config[name])).ToList();
if (missing.Count > 0) return Fail("не заданы " + string.Join(", ", missing));

builder.WebHost.UseUrls($"http://0.0.0.0:{config.GetValue("HTTP_PORT", 8080)}");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(config["HISTORY_DB"]!));
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddHostedService<BookingEventsConsumer>();

var app = builder.Build();
// Не читаем Kafka, пока схема не соответствует образу: иначе исправные события ушли бы в dead letter из-за схемы.
using (var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
{
    if (!await SchemaMigrations.IsCurrentAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), startup.Token))
        return Fail("БД недоступна или схема устарела, сначала выполните migrate");
}
app.MapGet("/ping", () => "pong");
app.MapGet("/ready", async (NpgsqlDataSource db, CancellationToken ct) => await SchemaMigrations.IsCurrentAsync(db, ct)
    ? Results.Text("ready")
    : Results.Text("database unavailable or schema outdated", statusCode: StatusCodes.Status503ServiceUnavailable));
app.Run();
return 0;

static int Fail(string reason)
{
    Console.Error.WriteLine($"booking-history-service не запущен: {reason}");
    return 1;
}
