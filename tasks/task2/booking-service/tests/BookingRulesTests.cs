using Hotelio.Bookings.Domain;
using Hotelio.Bookings.Tests.Support;
using Shouldly;
using Xunit;

namespace Hotelio.Bookings.Tests;

/// <summary>Ведёт себя как BookingService монолита: порядок проверок, тексты отказов, расчёт цены.</summary>
public class BookingRulesTests
{
    [Fact]
    public void Accepts_active_user_in_trusted_operational_hotel_with_free_rooms() =>
        BookingRules.Reject(Users.Active, Hotels.Good).ShouldBeNull();

    [Theory]
    [InlineData(false, false, "User is inactive")]
    [InlineData(true, true, "User is blacklisted")]
    public void Rejects_user(bool active, bool blacklisted, string reason) =>
        BookingRules.Reject(new UserFacts(Exists: true, Active: active, Blacklisted: blacklisted, Status: "ACTIVE"), Hotels.Good)
            .ShouldBe(reason);

    [Fact]
    public void Unknown_user_is_treated_as_inactive() =>
        BookingRules.Reject(UserFacts.Unknown, Hotels.Good).ShouldBe("User is inactive");

    [Fact]
    public void User_checks_come_before_hotel_checks() =>
        BookingRules.Reject(Users.Blacklisted, HotelFacts.Unknown(trusted: false)).ShouldBe("User is blacklisted");

    [Fact]
    public void Unknown_hotel_is_not_operational() =>
        BookingRules.Reject(Users.Active, HotelFacts.Unknown(trusted: true)).ShouldBe("Hotel is not operational");

    [Fact]
    public void Untrusted_hotel_is_rejected_before_fully_booked() =>
        BookingRules.Reject(Users.Active, new HotelFacts(Exists: true, Operational: true, FullyBooked: true, Trusted: false))
            .ShouldBe("Hotel is not trusted based on reviews");

    [Fact]
    public void Fully_booked_hotel_is_rejected() =>
        BookingRules.Reject(Users.Active, new HotelFacts(Exists: true, Operational: true, FullyBooked: true, Trusted: true))
            .ShouldBe("Hotel is fully booked");

    [Theory]
    [InlineData("ACTIVE", null, 100.0, 0.0)]
    [InlineData("ACTIVE", 10.0, 90.0, 10.0)]
    [InlineData("vip", null, 80.0, 0.0)]
    [InlineData("NEW_STATUS", null, 100.0, 0.0)]
    // Как в монолите (это дефект): скидка 20 вычитается как сумма (80 - 20 = 60), а не как 20% (64).
    [InlineData("VIP", 20.0, 60.0, 20.0)]
    public void Calculates_price_like_monolith(string status, double? promo, double expectedFinal, double expectedDiscount)
    {
        var price = BookingRules.Calculate(Users.WithStatus(status), (decimal?)promo);

        price.ShouldSatisfyAllConditions(
            () => price.FinalPrice.ShouldBe((decimal)expectedFinal),
            () => price.Discount.ShouldBe((decimal)expectedDiscount));
    }
}
