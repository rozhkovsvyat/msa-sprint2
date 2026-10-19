using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Hotelio.Bookings.Domain;
using Hotelio.Bookings.Legacy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Timeout;
using Xunit;

namespace Hotelio.Bookings.Tests;

/// <summary>Anti-Corruption Layer: перевод ответов монолита в модель бронирования и политика обращений к нему.</summary>
public sealed class MonolithContractTests
{
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return run(request, ct);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static MonolithClient Client(Stub stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("http://legacy/") }, NullLogger<MonolithClient>.Instance);

    private static MonolithClient ResilientClient(Stub stub, IHttpContextAccessor? context = null)
    {
        var services = new ServiceCollection().AddLogging();
        if (context is not null) services.AddSingleton(context);
        services.AddLegacyHttp(new Uri("http://legacy/"));
        services.AddHttpClient<MonolithClient>().ConfigurePrimaryHttpMessageHandler(() => stub);
        return services.BuildServiceProvider().GetRequiredService<MonolithClient>();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Maps_monolith_fields_and_ignores_unknown_ones()
    {
        var client = Client(new Stub((r, _) => Task.FromResult(Json(r.RequestUri!.AbsolutePath switch
        {
            var p when p.Contains("users") => """{"id":"u1","status":"VIP","active":true,"blacklisted":false,"email":"ignored"}""",
            var p when p.Contains("trusted") => "true",
            _ => """{"operational":true,"fullyBooked":false,"city":"Seoul"}""",
        }))));
        var user = await client.GetUserAsync("u1", Ct);
        Assert.True(user.IsVip);
        Assert.True(user.Active);
        Assert.False(user.Blacklisted);
        var hotel = await client.GetHotelAsync("h1", Ct);
        Assert.True(hotel.Operational);
        Assert.False(hotel.FullyBooked);
        Assert.True(hotel.Trusted);
    }

    [Theory]
    [InlineData("""{"status":"ACTIVE","active":true}""")]
    [InlineData("""{"status":"ACTIVE","active":"yes","blacklisted":false}""")]
    [InlineData("null")]
    public async Task Incomplete_or_incompatible_user_is_a_dependency_failure(string body)
    {
        var client = Client(new Stub((_, _) => Task.FromResult(Json(body))));
        await Assert.ThrowsAsync<LegacyUnavailableException>(() => client.GetUserAsync("u", Ct));
    }

    [Theory]
    [InlineData("\"VIP\"", 80)]
    [InlineData("\"vip\"", 80)]
    [InlineData("\"ACTIVE\"", 100)]
    [InlineData("null", 100)]
    public async Task User_status_gives_the_same_base_price_as_monolith(string status, double expected)
    {
        var client = Client(new Stub((_, _) => Task.FromResult(Json($$"""{"status":{{status}},"active":true,"blacklisted":false}"""))));
        var user = await client.GetUserAsync("u", Ct);
        Assert.Equal((decimal)expected, BookingRules.Calculate(user, null).BasePrice);
    }

    [Theory]
    [InlineData("""{"operational":true}""", "true")]
    [InlineData("""{"operational":true,"fullyBooked":false}""", "unknown")]
    public async Task Incomplete_hotel_or_unknown_trust_is_not_read_as_false(string hotel, string trust)
    {
        var client = Client(new Stub((r, _) => Task.FromResult(Json(r.RequestUri!.AbsolutePath.Contains("trusted") ? trust : hotel))));
        await Assert.ThrowsAsync<LegacyUnavailableException>(() => client.GetHotelAsync("h", Ct));
    }

    [Fact]
    public async Task Promo_discount_is_read_from_the_monolith_discount_field()
    {
        Assert.Equal(15m, await Client(new Stub((_, _) => Task.FromResult(Json("""{"discount":15}""")))).ValidatePromoAsync("p", "u", Ct));
        var renamed = Client(new Stub((_, _) => Task.FromResult(Json("""{"discountPercent":15}"""))));
        await Assert.ThrowsAsync<LegacyUnavailableException>(() => renamed.ValidatePromoAsync("p", "u", Ct));
    }

    [Fact]
    public async Task Missing_user_and_rejected_promo_keep_domain_meaning()
    {
        var client = Client(new Stub((r, _) =>
            Task.FromResult(Json("", r.Method == HttpMethod.Post ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound))));
        Assert.False((await client.GetUserAsync("missing", Ct)).Exists);
        Assert.Null(await client.ValidatePromoAsync("expired", "u", Ct));
    }

    [Fact]
    public async Task Get_is_retried_once_post_is_not_and_request_id_is_forwarded()
    {
        var stub = new Stub((r, _) =>
        {
            Assert.Equal("client-request-1", r.Headers.GetValues("x-request-id").Single());
            return Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable));
        });
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext { TraceIdentifier = "client-request-1" } };
        var client = ResilientClient(stub, context);
        await Assert.ThrowsAsync<LegacyUnavailableException>(() => client.GetUserAsync("u", Ct));
        Assert.Equal(2, stub.Calls);
        await Assert.ThrowsAsync<LegacyUnavailableException>(() => client.ValidatePromoAsync("p", "u", Ct));
        Assert.Equal(3, stub.Calls);
    }

    [Fact]
    public async Task Slow_monolith_is_cut_off_by_attempt_timeout()
    {
        var observed = new ConcurrentBag<CancellationToken>();
        var stub = new Stub(async (_, ct) =>
        {
            observed.Add(ct);
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return Json("{}");
        });
        using var budget = new CancellationTokenSource(LegacyHttp.Budget);
        var error = await Record.ExceptionAsync(() => ResilientClient(stub).GetUserAsync("u", budget.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.True(error is TimeoutRejectedException or OperationCanceledException, error?.GetType().Name);
        Assert.InRange(stub.Calls, 1, 2);
        Assert.All(observed, token => Assert.True(token.IsCancellationRequested));
    }

    [Fact]
    public async Task Caller_cancellation_reaches_the_monolith_call_and_stops_retries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var stub = new Stub(async (_, ct) =>
        {
            observed = ct;
            started.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return Json("{}");
        });
        using var caller = new CancellationTokenSource();
        var pending = ResilientClient(stub).GetUserAsync("u", caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        caller.Cancel();
        var error = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.True(observed.IsCancellationRequested);
        Assert.Equal(1, stub.Calls);
    }
}
