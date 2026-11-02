namespace Hotelio.Bookings.Services;

/// <summary>Сведения о пользователе, которые нужны бронированию (переведены из модели монолита).</summary>
public sealed record UserFacts(bool Exists, bool Active, bool Blacklisted, string? Status)
{
    public static readonly UserFacts Unknown = new(false, false, false, null);
    // Как в монолите: VIP без учёта регистра, любой другой или пустой статус — обычная цена.
    public bool IsVip => string.Equals(Status, "VIP", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Сведения об отеле и его репутации.</summary>
public sealed record HotelFacts(bool Exists, bool Operational, bool FullyBooked, bool Trusted)
{
    public static HotelFacts Unknown(bool trusted) => new(false, false, true, trusted);
}

public sealed record Price(decimal BasePrice, decimal Discount, decimal FinalPrice);

/// <summary>Правила BookingService монолита без изменения поведения: тот же порядок проверок и тексты отказов.</summary>
public static class BookingRules
{
    public const decimal RegularBasePrice = 100m;
    public const decimal VipBasePrice = 80m;

    /// <returns>Причина отказа или null, если бронирование допустимо.</returns>
    public static string? Reject(UserFacts user, HotelFacts hotel)
    {
        if (!user.Active) return "User is inactive";          // неизвестный пользователь тоже «неактивен»
        if (user.Blacklisted) return "User is blacklisted";
        if (!hotel.Operational) return "Hotel is not operational"; // неизвестный отель — «не работает»
        if (!hotel.Trusted) return "Hotel is not trusted based on reviews";
        if (hotel.FullyBooked) return "Hotel is fully booked";
        return null;
    }

    /// <param name="promoDiscount">Скидка подтверждённого промокода или null.</param>
    public static Price Calculate(UserFacts user, decimal? promoDiscount)
    {
        var basePrice = user.IsVip ? VipBasePrice : RegularBasePrice;
        var discount = promoDiscount ?? 0m;
        // Как в монолите: скидка промокода вычитается как сумма, а не как процент.
        return new Price(basePrice, discount, basePrice - discount);
    }
}
