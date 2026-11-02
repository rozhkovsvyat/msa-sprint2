namespace Hotelio.Bookings.Legacy;

/// <summary>Передаёт монолиту x-request-id входящего запроса, чтобы один запрос был виден в логах обоих сервисов.</summary>
public sealed class RequestIdHandler(IHttpContextAccessor context) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.TryAddWithoutValidation("x-request-id", context.HttpContext?.TraceIdentifier ?? Guid.NewGuid().ToString());
        return base.SendAsync(request, ct);
    }
}
