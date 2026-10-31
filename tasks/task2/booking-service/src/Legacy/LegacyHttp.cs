using Microsoft.Extensions.Http.Resilience;

namespace Hotelio.Bookings.Legacy;

public static class LegacyHttp
{
    /// <summary>Сколько всего времени даём обращениям к монолиту при создании одного бронирования.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    public static void AddLegacyHttp(this IServiceCollection services, Uri baseAddress) => services
        .AddHttpClient<MonolithClient>(http =>
        {
            http.BaseAddress = baseAddress;
            http.Timeout = Timeout.InfiniteTimeSpan; // время ограничивает resilience handler
        })
        .AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = Budget;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1);
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromMilliseconds(100);
            options.Retry.DisableForUnsafeHttpMethods(); // POST проверки промокода не повторяем
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
            options.CircuitBreaker.MinimumThroughput = 8;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(5);
            // Предел одновременных вызовов монолита с одного пода; остальные ждут в очереди, пока не выйдет это время.
            options.RateLimiter.DefaultRateLimiterOptions.PermitLimit = 100;
            options.RateLimiter.DefaultRateLimiterOptions.QueueLimit = 200;
        });
}
