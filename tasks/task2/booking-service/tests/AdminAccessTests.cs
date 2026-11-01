using Grpc.Core;
using Hotelio.Bookings.Grpc;
using Shouldly;
using Xunit;

namespace Hotelio.Bookings.Tests;

/// <summary>Служебный список бронирований: доступ по токену и разбор токена страницы.</summary>
public sealed class AdminAccessTests
{
    private const string Token = "stand-admin-token";

    private static Metadata? WithAuthorization(string? value)
    {
        if (value is null) return null;
        var headers = new Metadata();
        headers.Add("authorization", value);
        return headers;
    }

    [Theory]
    [InlineData("Bearer stand-admin-token")]
    [InlineData("bearer stand-admin-token")]   // схема без учёта регистра
    [InlineData("Bearer  stand-admin-token ")] // лишние пробелы вокруг значения
    public void The_configured_token_opens_the_admin_list(string header) =>
        new AdminToken(Token).Accepts(WithAuthorization(header)).ShouldBeTrue();

    [Theory]
    [InlineData("Bearer wrong-token")]
    [InlineData("stand-admin-token")]  // без схемы Bearer
    [InlineData("Basic stand-admin-token")]
    [InlineData("Bearer ")]
    [InlineData("")]
    public void Anything_else_does_not(string header) =>
        new AdminToken(Token).Accepts(WithAuthorization(header)).ShouldBeFalse();

    [Fact]
    public void Without_a_configured_token_or_a_header_the_admin_list_is_closed()
    {
        new AdminToken(Token).Accepts(new Metadata()).ShouldBeFalse();
        new AdminToken(Token).Accepts(null).ShouldBeFalse();
        // Токен не задан — служебный метод не открывается никому, в том числе с пустым значением.
        new AdminToken(null).Accepts(WithAuthorization($"Bearer {Token}")).ShouldBeFalse();
        new AdminToken("").Accepts(WithAuthorization("Bearer ")).ShouldBeFalse();
    }

    [Fact]
    public void Page_token_is_the_id_of_the_last_row()
    {
        BookingAdminGrpcService.EncodePageToken(42).ShouldBe("42");
        BookingAdminGrpcService.DecodePageToken("").ShouldBe(0);
        BookingAdminGrpcService.DecodePageToken("42").ShouldBe(42);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData(" 12")]
    [InlineData("99999999999999999999")]
    public void A_page_token_from_elsewhere_is_rejected(string token) =>
        Should.Throw<RpcException>(() => BookingAdminGrpcService.DecodePageToken(token))
            .StatusCode.ShouldBe(StatusCode.InvalidArgument);
}
