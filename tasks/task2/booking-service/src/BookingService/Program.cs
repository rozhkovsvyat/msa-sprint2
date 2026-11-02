using Hotelio.Bookings.Data;
using Hotelio.Bookings.Legacy;
using Hotelio.Bookings.Middleware;
using Hotelio.Bookings.Outbox;
using Hotelio.Bookings.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Polly", LogLevel.Warning);
builder.Logging.AddFilter("Grpc", LogLevel.Warning);

if (args is ["migrate"])
{
    if (string.IsNullOrWhiteSpace(config["BOOKING_DB"])) return Fail("не задан BOOKING_DB");
    using var logging = LoggerFactory.Create(b => b.AddSimpleConsole());
    return SchemaMigrations.Apply(config["BOOKING_DB"]!, logging) ? 0 : 1;
}

// disabled — для окружения без Kafka: события копятся в outbox, пока публикацию не включат.
var publisher = config["OUTBOX_PUBLISHER"] ?? "enabled";
if (publisher is not ("enabled" or "disabled")) return Fail("OUTBOX_PUBLISHER: ожидается enabled или disabled");
var required = new List<string> { "BOOKING_DB", "MONOLITH_URL" };
if (publisher == "enabled") required.Add("KAFKA_BOOTSTRAP");
var missing = required.Where(name => string.IsNullOrWhiteSpace(config[name])).ToList();
if (missing.Count > 0) return Fail("не заданы " + string.Join(", ", missing));
if (!Uri.TryCreate(config["MONOLITH_URL"], UriKind.Absolute, out var monolithUri)) return Fail("MONOLITH_URL: ожидается абсолютный URL");
var bookingDb = config["BOOKING_DB"]!;

// gRPC слушает отдельный порт с HTTP/2 без TLS — так подключается прокси монолита; служебный HTTP — HTTP/1.1.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(config.GetValue("GRPC_PORT", 9090), o => o.Protocols = HttpProtocols.Http2);
    kestrel.ListenAnyIP(config.GetValue("HTTP_PORT", 8080), o => o.Protocols = HttpProtocols.Http1);
});

builder.Services.AddGrpc();
var reflection = config.GetValue("GRPC_REFLECTION", false); // для grpcurl на стенде
if (reflection) builder.Services.AddGrpcReflection();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(bookingDb));
builder.Services.AddSingleton<BookingStore>();
var adminToken = new AdminToken(config["BOOKING_ADMIN_TOKEN"]);
builder.Services.AddSingleton(adminToken);
builder.Services.AddHttpContextAccessor();
builder.Services.AddLegacyHttp(monolithUri);
if (publisher == "enabled")
{
    builder.Services.Configure<OutboxOptions>(config);
    builder.Services.AddHostedService<OutboxPublisher>();
    builder.Services.AddHostedService<OutboxCleanup>();
}

var app = builder.Build();
var version = config["SERVICE_VERSION"] ?? "v1";
var featureX = config.GetValue("ENABLE_FEATURE_X", false);
// Для проверки fallback в Istio: /version отвечает этим кодом (например, 503), а под остаётся живым.
var faultStatus = config.GetValue("FAULT_INJECTION_STATUS", 0);

app.UseRequestLogging(version);

app.MapGrpcService<BookingGrpcService>();
app.MapGrpcService<BookingAdminGrpcService>();
if (reflection) app.MapGrpcReflectionService();
app.MapGet("/ping", () => "pong");
app.MapGet("/ready", async (NpgsqlDataSource db, CancellationToken ct) => await SchemaMigrations.IsCurrentAsync(db, ct)
    ? Results.Text("ready")
    : Results.Text("database unavailable or schema outdated", statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/version", () => Results.Json(new { version, featureX }, statusCode: faultStatus == 0 ? 200 : faultStatus));
app.MapGet("/diagnostics/outbox", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    var stats = await OutboxStats.ReadAsync(db, ct);
    return Results.Json(new { stats.Pending, stats.Retrying, stats.DeadLetters, stats.OldestAgeSeconds, publisher });
});
if (featureX) app.MapGet("/feature", () => "Feature X is enabled!");

if (!adminToken.Configured) app.Logger.LogWarning("BOOKING_ADMIN_TOKEN is not set: the admin booking list rejects every call");
app.Logger.LogInformation("booking-service {Version} started, ENABLE_FEATURE_X={FeatureX}, OUTBOX_PUBLISHER={Publisher}", version, featureX, publisher);
app.Run();
return 0;

static int Fail(string reason)
{
    Console.Error.WriteLine($"booking-service не запущен: {reason}");
    return 1;
}
