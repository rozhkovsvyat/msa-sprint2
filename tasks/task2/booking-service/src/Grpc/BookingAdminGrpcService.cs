using System.Globalization;
using Booking.Admin;
using Grpc.Core;
using Hotelio.Bookings.Data;

namespace Hotelio.Bookings.Grpc;

/// <summary>Служебный список всех бронирований: токен и постраничный вывод, чтобы не тянуть таблицу целиком.</summary>
public sealed class BookingAdminGrpcService(BookingStore store, AdminToken admin, ILogger<BookingAdminGrpcService> log)
    : BookingAdminService.BookingAdminServiceBase
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;

    public override async Task<ListAllBookingsResponse> ListAllBookings(ListAllBookingsRequest request, ServerCallContext context)
    {
        if (!admin.Accepts(context.RequestHeaders))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "admin token required"));
        if (request.PageSize is < 0 or > MaxPageSize)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"page_size is 1..{MaxPageSize}"));
        var pageSize = request.PageSize == 0 ? DefaultPageSize : request.PageSize;
        var afterId = DecodePageToken(request.PageToken);

        var page = await store.ListPageAsync(afterId, pageSize, context.CancellationToken);
        var response = new ListAllBookingsResponse();
        response.Bookings.AddRange(page.Select(BookingResponses.ToResponse));
        // Неполная страница — это конец списка, токена дальше нет.
        if (page.Count == pageSize) response.NextPageToken = EncodePageToken(page[^1].Id);
        log.LogInformation("Admin listing: {Count} bookings after {AfterId} requestId={RequestId}",
            page.Count, afterId, context.GetHttpContext().TraceIdentifier);
        return response;
    }

    // Токен страницы — id последней строки: строки не нумеруются заново, пока список читают.
    public static string EncodePageToken(long id) => id.ToString(CultureInfo.InvariantCulture);

    public static long DecodePageToken(string token) => token.Length == 0
        ? 0
        : long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0
            ? id
            : throw new RpcException(new Status(StatusCode.InvalidArgument, "page_token is next_page_token of the previous page"));
}
