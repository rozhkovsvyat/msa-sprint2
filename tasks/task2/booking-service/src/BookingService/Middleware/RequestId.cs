using System.Diagnostics.CodeAnalysis;

namespace Hotelio.Bookings.Middleware;

/// <summary>Сквозной x-request-id: допустимый от клиента принимаем, иначе создаём новый. Те же правила в подграфах GraphQL.</summary>
public static class RequestId
{
    public static string Normalize(string? value) => IsValid(value) ? value : Guid.NewGuid().ToString();

    // 1–128 символов: латинские буквы, цифры и -_.: — без пробелов и управляющих символов в логах.
    public static bool IsValid([NotNullWhen(true)] string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
