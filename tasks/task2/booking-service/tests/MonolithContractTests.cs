using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Hotelio.Bookings.Domain;
using Hotelio.Bookings.Legacy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Shouldly;
using Xunit;

namespace Hotelio.Bookings.Tests;

/// <summary>Anti-Corruption Layer: перевод ответов монолита в модель бронирования и политика обращений к нему.</summary>
public sealed class MonolithContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Монолит, который отвечает заданной функцией: подменяем HttpMessageHandler клиента.</summary>
    private static Mock<HttpMessageHandler> Monolith(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(respond);
        return handler;
    }

    private static Mock<HttpMessageHandler> Monolith(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        Monolith((request, _) => Task.FromResult(respond(request)));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static void ShouldBeCalled(Mock<HttpMessageHandler> monolith, int times) =>
        monolith.Protected().Verify("SendAsync", Times.Exactly(times), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());

    private static MonolithClient Client(Mock<HttpMessageHandler> monolith) =>
        new(new HttpClient(monolith.Object) { BaseAddress = new Uri("http://legacy/") }, NullLogger<MonolithClient>.Instance);

    /// <summary>Клиент с таймаутами, повторами и circuit breaker, как в сервисе.</summary>
    private static MonolithClient ResilientClient(Mock<HttpMessageHandler> monolith, IHttpContextAccessor? context = null)
    {
        var services = new ServiceCollection().AddLogging();
        if (context is not null) services.AddSingleton(context);
        services.AddLegacyHttp(new Uri("http://legacy/"));
        services.AddHttpClient<MonolithClient>().ConfigurePrimaryHttpMessageHandler(() => monolith.Object);
        return services.BuildServiceProvider().GetRequiredService<MonolithClient>();
    }

    [Fact]
    public async Task Maps_monolith_fields_and_ignores_unknown_ones()
    {
        var client = Client(Monolith(r => Json(r.RequestUri!.AbsolutePath switch
        {
            var p when p.Contains("users") => """{"id":"u1","status":"VIP","active":true,"blacklisted":false,"email":"ignored"}""",
            var p when p.Contains("trusted") => "true",
            _ => """{"operational":true,"fullyBooked":false,"city":"Seoul"}""",
        })));

        var user = await client.GetUserAsync("u1", Ct);
        var hotel = await client.GetHotelAsync("h1", Ct);

        user.ShouldSatisfyAllConditions(
            () => user.IsVip.ShouldBeTrue(),
            () => user.Active.ShouldBeTrue(),
            () => user.Blacklisted.ShouldBeFalse());
        hotel.ShouldSatisfyAllConditions(
            () => hotel.Operational.ShouldBeTrue(),
            () => hotel.FullyBooked.ShouldBeFalse(),
            () => hotel.Trusted.ShouldBeTrue());
    }

    [Theory]
    [InlineData("""{"status":"ACTIVE","active":true}""")]
    [InlineData("""{"status":"ACTIVE","active":"yes","blacklisted":false}""")]
    [InlineData("null")]
    public async Task Incomplete_or_incompatible_user_is_a_dependency_failure(string body) =>
        await Should.ThrowAsync<LegacyUnavailableException>(() => Client(Monolith(_ => Json(body))).GetUserAsync("u", Ct));

    [Theory]
    [InlineData("\"VIP\"", 80)]
    [InlineData("\"vip\"", 80)]
    [InlineData("\"ACTIVE\"", 100)]
    [InlineData("null", 100)]
    public async Task User_status_gives_the_same_base_price_as_monolith(string status, double expected)
    {
        var client = Client(Monolith(_ => Json($$"""{"status":{{status}},"active":true,"blacklisted":false}""")));

        var user = await client.GetUserAsync("u", Ct);

        BookingRules.Calculate(user, null).BasePrice.ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("""{"operational":true}""", "true")]
    [InlineData("""{"operational":true,"fullyBooked":false}""", "unknown")]
    public async Task Incomplete_hotel_or_unknown_trust_is_not_read_as_false(string hotel, string trust)
    {
        var client = Client(Monolith(r => Json(r.RequestUri!.AbsolutePath.Contains("trusted") ? trust : hotel)));

        await Should.ThrowAsync<LegacyUnavailableException>(() => client.GetHotelAsync("h", Ct));
    }

    [Fact]
    public async Task Promo_discount_is_read_from_the_monolith_discount_field()
    {
        (await Client(Monolith(_ => Json("""{"discount":15}"""))).ValidatePromoAsync("p", "u", Ct)).ShouldBe(15m);

        var renamed = Client(Monolith(_ => Json("""{"discountPercent":15}""")));
        await Should.ThrowAsync<LegacyUnavailableException>(() => renamed.ValidatePromoAsync("p", "u", Ct));
    }

    [Fact]
    public async Task Missing_user_and_rejected_promo_keep_domain_meaning()
    {
        var client = Client(Monolith(r => Json("", r.Method == HttpMethod.Post ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound)));

        (await client.GetUserAsync("missing", Ct)).Exists.ShouldBeFalse();
        (await client.ValidatePromoAsync("expired", "u", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Get_is_retried_once_post_is_not_and_request_id_is_forwarded()
    {
        var monolith = Monolith(r =>
        {
            r.Headers.GetValues("x-request-id").Single().ShouldBe("client-request-1");
            return Json("{}", HttpStatusCode.ServiceUnavailable);
        });
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext { TraceIdentifier = "client-request-1" } };
        var client = ResilientClient(monolith, context);

        await Should.ThrowAsync<LegacyUnavailableException>(() => client.GetUserAsync("u", Ct));
        ShouldBeCalled(monolith, 2);

        await Should.ThrowAsync<LegacyUnavailableException>(() => client.ValidatePromoAsync("p", "u", Ct));
        ShouldBeCalled(monolith, 3);
    }

    [Fact]
    public async Task Slow_monolith_is_cut_off_by_attempt_timeout_and_retried_once()
    {
        var observed = new ConcurrentBag<CancellationToken>();
        var monolith = Monolith(async (_, ct) =>
        {
            observed.Add(ct);
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return Json("{}");
        });

        // Две попытки по 1 с укладываются в бюджет 3 с: ответ обрывает таймаут попытки, а не общий бюджет.
        await Should.ThrowAsync<TimeoutRejectedException>(() => ResilientClient(monolith).GetUserAsync("u", Ct)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct));

        ShouldBeCalled(monolith, 2);
        observed.ShouldAllBe(token => token.IsCancellationRequested);
    }

    [Fact]
    public async Task Circuit_opens_after_a_stream_of_errors_and_stops_calling_the_monolith()
    {
        var monolith = Monolith(_ => Json("{}", HttpStatusCode.ServiceUnavailable));
        var client = ResilientClient(monolith);

        // Четыре запроса по две попытки — восемь ошибок, это минимум для решения circuit breaker.
        for (var i = 0; i < 4; i++)
            await Should.ThrowAsync<LegacyUnavailableException>(() => client.GetUserAsync("u", Ct));
        ShouldBeCalled(monolith, 8);

        await Should.ThrowAsync<BrokenCircuitException>(() => client.GetUserAsync("u", Ct));
        ShouldBeCalled(monolith, 8);
    }

    [Fact]
    public async Task Caller_cancellation_reaches_the_monolith_call_and_stops_retries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var monolith = Monolith(async (_, ct) =>
        {
            observed = ct;
            started.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return Json("{}");
        });
        using var caller = new CancellationTokenSource();
        var pending = ResilientClient(monolith).GetUserAsync("u", caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        caller.Cancel();
        var error = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        error.ShouldBeAssignableTo<OperationCanceledException>();
        observed.IsCancellationRequested.ShouldBeTrue();
        ShouldBeCalled(monolith, 1);
    }
}
