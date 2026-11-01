using System.Net;
using System.Text;

namespace Hotelio.Bookings.Tests.Fakes;

/// <summary>Заглушка REST монолита: отвечает заданной функцией и считает обращения.</summary>
internal sealed class MonolithStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int calls;

    public int Calls => calls;

    public static MonolithStub Returning(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new((request, _) => Task.FromResult(respond(request)));

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref calls);
        return respond(request, ct);
    }
}
