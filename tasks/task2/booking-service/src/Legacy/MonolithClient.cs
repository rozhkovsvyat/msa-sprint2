using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hotelio.Bookings.Domain;

namespace Hotelio.Bookings.Legacy;

/// <summary>
/// Anti-Corruption Layer к REST API монолита. Знает форматы и коды ответов монолита
/// и переводит их в модель бронирования. Бизнес-правил здесь нет — только трансляция.
/// </summary>
public sealed class MonolithClient(HttpClient http, ILogger<MonolithClient> log, IHttpContextAccessor? context = null)
{
    public async Task<UserFacts> GetUserAsync(string userId, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"api/users/{Uri.EscapeDataString(userId)}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return UserFacts.Unknown;
        EnsureOk(resp, "users");
        var dto = await ReadAsync<UserDto>(resp, "users", ct);
        if (dto.Active is null || dto.Blacklisted is null)
            throw Invalid("users", "missing required field");
        return new UserFacts(true, dto.Active.Value, dto.Blacklisted.Value, dto.Status);
    }

    public async Task<HotelFacts> GetHotelAsync(string hotelId, CancellationToken ct)
    {
        var hotelTask = SendAsync(HttpMethod.Get, $"api/hotels/{Uri.EscapeDataString(hotelId)}", ct);
        var trustedTask = SendAsync(HttpMethod.Get, $"api/reviews/hotel/{Uri.EscapeDataString(hotelId)}/trusted", ct);
        try { await Task.WhenAll(hotelTask, trustedTask); }
        catch
        {
            if (hotelTask.IsCompletedSuccessfully) hotelTask.Result.Dispose();
            if (trustedTask.IsCompletedSuccessfully) trustedTask.Result.Dispose();
            throw;
        }
        using var hotelResp = await hotelTask;
        using var trustedResp = await trustedTask;

        EnsureOk(trustedResp, "reviews");
        if (!bool.TryParse((await trustedResp.Content.ReadAsStringAsync(ct)).Trim(), out var trusted))
            throw Invalid("reviews", "expected boolean");

        if (hotelResp.StatusCode == HttpStatusCode.NotFound) return HotelFacts.Unknown(trusted);
        EnsureOk(hotelResp, "hotels");
        var dto = await ReadAsync<HotelDto>(hotelResp, "hotels", ct);
        if (dto.Operational is null || dto.FullyBooked is null) throw Invalid("hotels", "missing required field");
        return new HotelFacts(true, dto.Operational.Value, dto.FullyBooked.Value, trusted);
    }

    /// <returns>Скидка промокода или null, если промокод не подходит пользователю.</returns>
    public async Task<decimal?> ValidatePromoAsync(string code, string userId, CancellationToken ct)
    {
        var url = $"api/promos/validate?code={Uri.EscapeDataString(code)}&userId={Uri.EscapeDataString(userId)}";
        using var resp = await SendAsync(HttpMethod.Post, url, ct);
        if (resp.StatusCode == HttpStatusCode.BadRequest) return null; // монолит: «недействителен для пользователя»
        EnsureOk(resp, "promos");
        var dto = await ReadAsync<PromoDto>(resp, "promos", ct);
        if (dto.Discount is null) throw Invalid("promos", "missing discount");
        return dto.Discount;
    }

    private void EnsureOk(HttpResponseMessage resp, string area)
    {
        if (resp.IsSuccessStatusCode) return;
        log.LogWarning("Monolith {Area} answered {Status}", area, (int)resp.StatusCode);
        throw new LegacyUnavailableException($"{area}: HTTP {(int)resp.StatusCode}");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("x-request-id", context?.HttpContext?.TraceIdentifier ?? Guid.NewGuid().ToString());
        return await http.SendAsync(request, ct);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, string area, CancellationToken ct)
    {
        try { return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw Invalid(area, "empty body"); }
        catch (JsonException) { throw Invalid(area, "incompatible DTO"); }
    }

    private LegacyUnavailableException Invalid(string area, string reason)
    {
        log.LogError("Legacy translation failed {Area}: {Reason}", area, reason);
        return new LegacyUnavailableException(area + ": " + reason);
    }

    private sealed record UserDto(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("blacklisted")] bool? Blacklisted,
        [property: JsonPropertyName("active")] bool? Active);

    private sealed record HotelDto(
        [property: JsonPropertyName("operational")] bool? Operational,
        [property: JsonPropertyName("fullyBooked")] bool? FullyBooked);

    // В монолите поле называется discount, хотя architecture/readme.md описывает discountPercent.
    private sealed record PromoDto([property: JsonPropertyName("discount")] decimal? Discount);
}

public sealed class LegacyUnavailableException(string message) : Exception(message);
