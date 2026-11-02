using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

namespace Hotelio.Bookings;

public static class RequestLogging
{
    /// <summary>x-request-id и версия сервиса в каждом ответе, строка лога на каждый запрос, кроме проб.</summary>
    public static void UseRequestLogging(this WebApplication app, string version) => app.Use(async (ctx, next) =>
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
}
