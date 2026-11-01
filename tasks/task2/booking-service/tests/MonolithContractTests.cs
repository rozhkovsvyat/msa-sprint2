using System.Collections.Concurrent;
using System.Net;
using Hotelio.Bookings.Domain;
using Hotelio.Bookings.Legacy;
using Hotelio.Bookings.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Timeout;
using Shouldly;
using Xunit;
using static Hotelio.Bookings.Tests.Fakes.MonolithStub;

namespace Hotelio.Bookings.Tests;

/// <summary>Anti-Corruption Layer: перевод ответов монолита в модель бронирования и политика обращений к нему.</summary>
public sealed class MonolithContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MonolithClient Client(MonolithStub stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("http://legacy/") }, NullLogger<MonolithClient>.Instance);

    /// <summary>Клиент с таймаутами, повторами и circuit breaker, как в сервисе.</summary>
    private static MonolithClient ResilientClient(MonolithStub stub, IHttpContextAccessor? context = null)
    {
        var services = new ServiceCollection().AddLogging();
        if (context is not null) services.AddSingleton(context);
        services.AddLegacyHttp(new Uri("http://legacy/"));
        services.AddHttpClient<MonolithClient>().ConfigurePrimaryHttpMessageHandler(() => stub);
        return services.BuildServiceProvider().GetRequiredService<MonolithClient>();
    }

    [Fact]
    public async Task Maps_monolith_fields_and_ignores_unknown_ones()
    {
        var client = Client(Returning(r => Json(r.RequestUri!.AbsolutePath switch
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
        await Should.ThrowAsync<LegacyUnavailableException>(() => Client(Returning(_ => Json(body))).GetUserAsync("u", Ct));

    [Theory]
    [InlineData("\"VIP\"", 80)]
    [InlineData("\"vip\"", 80)]
    [InlineData("\"ACTIVE\"", 100)]
    [InlineData("null", 100)]
    public async Task User_status_gives_the_same_base_price_as_monolith(string status, double expected)
    {
        var client = Client(Returning(_ => Json($$"""{"status":{{status}},"active":true,"blacklisted":false}""")));

        var user = await client.GetUserAsync("u", Ct);

        BookingRules.Calculate(user, null).BasePrice.ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("""{"operational":true}""", "true")]
    [InlineData("""{"operational":true,"fullyBooked":false}""", "unknown")]
    public async Task Incomplete_hotel_or_unknown_trust_is_not_read_as_false(string hotel, string trust)
    {
        var client = Client(Returning(r => Json(r.RequestUri!.AbsolutePath.Contains("trusted") ? trust : hotel)));

        await Should.ThrowAsync<LegacyUnavailableException>(() => client.GetHotelAsync("h", Ct));
    }

    [Fact]
    public async Task Promo_discount_is_read_from_the_monolith_discount_field()
    {
        (await Client(Returning(_ => Json("""{"discount":15}"""))).ValidatePromoAsync("p", "u", Ct)).ShouldBe(15m);

        var renamed = Client(Returning(_ => Json("""{"discountPercent":15}""")));
        await Should.ThrowAsync<LegacyUnavailableException>(() => renamed.ValidatePromoAsync("p", "u", Ct));
    }

    [Fact]
    public async Task Missing_user_and_rejected_promo_keep_domain_meaning()
    {
        var client = Client(Returning(r => Json("", r.Method == HttpMethod.Post ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound)));

        (await client.GetUserAsync("missing", Ct)).Exists.ShouldBeFalse();
        (await client.ValidatePromoAsync("expired", "u", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Get_is_retried_once_post_is_not_and_request_id_is_forwarded()
    {
        var stub = Returning(r =>
        {
            r.Headers.GetValues("x-request-id").Single().ShouldBe("client-request-1");
            return Json("{}", HttpStatusCode.ServiceUnavailable);
        });
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext { TraceIdentifier = "client-request-1" } };
        var client = ResilientClient(stub, context);

        await Should.ThrowAsync<LegacyUnavailableException>(() => client.GetUserAsync("u", Ct));
        stub.Calls.ShouldBe(2);

        await Should.ThrowAsync<LegacyUnavailableException>(() => client.ValidatePromoAsync("p", "u", Ct));
        stub.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task Slow_monolith_is_cut_off_by_attempt_timeout()
    {
        var observed = new ConcurrentBag<CancellationToken>();
        var stub = new MonolithStub(async (_, ct) =>
        {
            observed.Add(ct);
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return Json("{}");
        });
        using var budget = new CancellationTokenSource(LegacyHttp.Budget);

        var error = await Record.ExceptionAsync(() => ResilientClient(stub).GetUserAsync("u", budget.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct));

        error.ShouldNotBeNull();
        (error is TimeoutRejectedException or OperationCanceledException).ShouldBeTrue(error.GetType().Name);
        stub.Calls.ShouldBeInRange(1, 2);
        observed.ShouldAllBe(token => token.IsCancellationRequested);
    }

    [Fact]
    public async Task Caller_cancellation_reaches_the_monolith_call_and_stops_retries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var stub = new MonolithStub(async (_, ct) =>
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

        error.ShouldBeAssignableTo<OperationCanceledException>();
        observed.IsCancellationRequested.ShouldBeTrue();
        stub.Calls.ShouldBe(1);
    }
}
