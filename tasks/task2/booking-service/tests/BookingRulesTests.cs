using Hotelio.Bookings.Domain;
using Xunit;

namespace Hotelio.Bookings.Tests;

/// <summary>Паритет с BookingService монолита: порядок проверок, тексты отказов, расчёт цены.</summary>
public class BookingRulesTests
{
    private static readonly UserFacts ActiveUser = new(true, true, false, "ACTIVE");
    private static readonly UserFacts VipUser = new(true, true, false, "VIP");
    private static readonly HotelFacts GoodHotel = new(true, true, false, true);

    [Fact]
    public void Accepts_active_user_in_trusted_operational_hotel_with_free_rooms() =>
        Assert.Null(BookingRules.Reject(ActiveUser, GoodHotel));

    [Theory]
    [InlineData(false, false, "User is inactive")]
    [InlineData(true, true, "User is blacklisted")]
    public void Rejects_user(bool active, bool blacklisted, string reason) =>
        Assert.Equal(reason, BookingRules.Reject(new UserFacts(true, active, blacklisted, "ACTIVE"), GoodHotel));

    [Fact]
    public void Unknown_user_is_treated_as_inactive() =>
        Assert.Equal("User is inactive", BookingRules.Reject(UserFacts.Unknown, GoodHotel));

    [Fact]
    public void User_checks_come_before_hotel_checks() =>
        Assert.Equal("User is blacklisted",
            BookingRules.Reject(new UserFacts(true, true, true, "ACTIVE"), HotelFacts.Unknown(trusted: false)));

    [Fact]
    public void Unknown_hotel_is_not_operational() =>
        Assert.Equal("Hotel is not operational", BookingRules.Reject(ActiveUser, HotelFacts.Unknown(trusted: true)));

    [Fact]
    public void Untrusted_hotel_is_rejected_before_fully_booked() =>
        Assert.Equal("Hotel is not trusted based on reviews",
            BookingRules.Reject(ActiveUser, new HotelFacts(true, true, FullyBooked: true, Trusted: false)));

    [Fact]
    public void Fully_booked_hotel_is_rejected() =>
        Assert.Equal("Hotel is fully booked",
            BookingRules.Reject(ActiveUser, new HotelFacts(true, true, FullyBooked: true, Trusted: true)));

    [Theory]
    [InlineData("ACTIVE", null, 100.0, 0.0)]
    [InlineData("ACTIVE", 10.0, 90.0, 10.0)]
    [InlineData("vip", null, 80.0, 0.0)]
    [InlineData("NEW_STATUS", null, 100.0, 0.0)]
    // Паритет с дефектом монолита: скидка 20 вычитается как сумма (80 - 20 = 60), а не как 20% (64).
    [InlineData("VIP", 20.0, 60.0, 20.0)]
    public void Calculates_price_like_monolith(string status, double? promo, double expectedFinal, double expectedDiscount)
    {
        var price = BookingRules.Calculate(new UserFacts(true, true, false, status), (decimal?)promo);
        Assert.Equal((decimal)expectedFinal, price.FinalPrice);
        Assert.Equal((decimal)expectedDiscount, price.Discount);
    }
}
