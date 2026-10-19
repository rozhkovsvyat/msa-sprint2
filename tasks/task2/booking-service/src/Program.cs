using System.Diagnostics;
using Hotelio.Bookings;
using Hotelio.Bookings.Data;
using Hotelio.Bookings.Grpc;
using Hotelio.Bookings.Legacy;
using Hotelio.Bookings.Outbox;
using Microsoft.AspNetCore.Http.Features;
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

// disabled — для окружений без Kafka (Kubernetes заданий 4–5): события копятся в outbox.
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddLegacyHttp(monolithUri);
if (publisher == "enabled") builder.Services.AddHostedService<OutboxPublisher>();

var app = builder.Build();
var version = config["SERVICE_VERSION"] ?? "v1";
var featureX = config.GetValue("ENABLE_FEATURE_X", false);
// Для проверки fallback в Istio (задание 5): живой под отвечает на /version этим кодом.
var faultStatus = config.GetValue("FAULT_INJECTION_STATUS", 0);

app.Use(async (ctx, next) =>
{
    var requestId = RequestId.Normalize(ctx.Request.Headers["x-request-id"]);
    ctx.TraceIdentifier = requestId;
    ctx.Response.Headers["x-request-id"] = requestId;
    ctx.Response.Headers["X-Booking-Service-Version"] = version;
    var started = Stopwatch.GetTimestamp();
    await next();
    if (ctx.Request.Path == "/ping" || ctx.Request.Path == "/ready") return;
    // gRPC-статус приходит в трейлерах, а при ошибке без тела ответа — в заголовках.
    var grpcStatus = ctx.Response.Headers["grpc-status"].FirstOrDefault()
        ?? ctx.Features.Get<IHttpResponseTrailersFeature>()?.Trailers["grpc-status"].FirstOrDefault();
    app.Logger.LogInformation("{Method} {Path} {Status} grpc-status={GrpcStatus} {ElapsedMs:F0} ms requestId={RequestId}",
        ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode, grpcStatus ?? "-",
        Stopwatch.GetElapsedTime(started).TotalMilliseconds, requestId);
});

app.MapGrpcService<BookingGrpcService>();
if (reflection) app.MapGrpcReflectionService();
app.MapGet("/ping", () => "pong");
app.MapGet("/ready", async (NpgsqlDataSource db, CancellationToken ct) => await SchemaMigrations.IsCurrentAsync(db, ct)
    ? Results.Text("ready")
    : Results.Text("database unavailable or schema outdated", statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/version", () => Results.Json(new { version, featureX }, statusCode: faultStatus == 0 ? 200 : faultStatus));
app.MapGet("/diagnostics/outbox", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("""
        select count(*) filter (where published_at is null and dead_lettered_at is null),
               count(*) filter (where published_at is null and dead_lettered_at is null and failure_count > 0),
               count(*) filter (where dead_lettered_at is not null),
               coalesce(extract(epoch from now() - min(created_at) filter (where published_at is null)), 0)::double precision
        from outbox
        """);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    await r.ReadAsync(ct);
    return Results.Json(new { pending = r.GetInt64(0), retrying = r.GetInt64(1), deadLetters = r.GetInt64(2), oldestAgeSeconds = r.GetDouble(3), publisher });
});
if (featureX) app.MapGet("/feature", () => "Feature X is enabled!");

app.Logger.LogInformation("booking-service {Version} started, ENABLE_FEATURE_X={FeatureX}, OUTBOX_PUBLISHER={Publisher}", version, featureX, publisher);
app.Run();
return 0;

static int Fail(string reason)
{
    Console.Error.WriteLine($"booking-service не запущен: {reason}");
    return 1;
}
