using System.Globalization;
using Booking;
using Grpc.Core;
using Hotelio.Bookings.Data;
using Hotelio.Bookings.Domain;
using Hotelio.Bookings.Legacy;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Hotelio.Bookings.Grpc;

/// <summary>Реализация контракта booking.proto. Монолит вызывает её через готовый gRPC-прокси.</summary>
public sealed class BookingGrpcService(MonolithClient legacy, BookingStore store, ILogger<BookingGrpcService> log)
    : BookingService.BookingServiceBase
{
    public override async Task<BookingResponse> CreateBooking(BookingRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.HotelId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "user_id and hotel_id are required"));
        var requestId = context.GetHttpContext().TraceIdentifier;
        // Прокси монолита передаёт отсутствующий промокод пустой строкой.
        var promoCode = string.IsNullOrEmpty(request.PromoCode) ? null : request.PromoCode;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        budget.CancelAfter(LegacyHttp.Budget);
        try
        {
            var userTask = legacy.GetUserAsync(request.UserId, budget.Token);
            var hotelTask = legacy.GetHotelAsync(request.HotelId, budget.Token);
            await Task.WhenAll(userTask, hotelTask);
            var user = await userTask;

            var rejection = BookingRules.Reject(user, await hotelTask);
            if (rejection is not null)
            {
                log.LogWarning("Booking rejected: {Reason} userId={UserId} hotelId={HotelId} requestId={RequestId}",
                    rejection, request.UserId, request.HotelId, requestId);
                throw new RpcException(new Status(StatusCode.FailedPrecondition, rejection));
            }

            var promoDiscount = promoCode is null ? null : await legacy.ValidatePromoAsync(promoCode, request.UserId, budget.Token);
            var price = BookingRules.Calculate(user, promoDiscount);
            var booking = await store.CreateAsync(request.UserId, request.HotelId, promoCode, price, requestId, context.CancellationToken);
            log.LogInformation("Booking {BookingId} created: base={Base} discount={Discount} final={Final} requestId={RequestId}",
                booking.Id, price.BasePrice, price.Discount, price.FinalPrice, requestId);
            return ToResponse(booking);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            log.LogWarning("Monolith budget {Budget} exceeded requestId={RequestId}", LegacyHttp.Budget, requestId);
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "monolith budget exceeded"));
        }
        catch (Exception ex) when (ex is LegacyUnavailableException or HttpRequestException
                                       or TimeoutRejectedException or BrokenCircuitException or RateLimiterRejectedException)
        {
            log.LogError("Monolith dependency failed: {Error} requestId={RequestId}", ex.Message, requestId);
            throw new RpcException(new Status(StatusCode.Unavailable, "monolith dependency unavailable"));
        }
    }

    public override async Task<BookingListResponse> ListBookings(BookingListRequest request, ServerCallContext context)
    {
        // Пустой user_id — общий список (прокси монолита этот режим не использует, см. ADR-001, Н10).
        var bookings = await store.ListAsync(request.UserId.Length == 0 ? null : request.UserId, context.CancellationToken);
        var response = new BookingListResponse();
        response.Bookings.AddRange(bookings.Select(ToResponse));
        return response;
    }

    private static BookingResponse ToResponse(StoredBooking b) => new()
    {
        // Прокси монолита делает Long.parseLong(id) и Instant.parse(created_at): числовой id и дата ISO-8601 в UTC.
        Id = b.Id.ToString(CultureInfo.InvariantCulture),
        UserId = b.UserId,
        HotelId = b.HotelId,
        PromoCode = b.PromoCode ?? string.Empty,
        // В контракте прокси поле называется discount_percent, хотя монолит хранит в нём сумму скидки (ADR-001, Н2).
        DiscountPercent = (double)b.DiscountAmount,
        Price = (double)b.Price,
        CreatedAt = b.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
    };
}
