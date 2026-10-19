using System.Diagnostics.CodeAnalysis;

namespace Hotelio.Bookings;

/// <summary>
/// Сквозной идентификатор запроса: x-request-id клиента принимаем, если он допустим, иначе создаём новый.
/// Те же правила в подграфах GraphQL (tasks/task3/shared/request-context.js).
/// </summary>
public static class RequestId
{
    public static string Normalize(string? value) => IsValid(value) ? value : Guid.NewGuid().ToString();

    // 1–128 символов: латинские буквы, цифры и -_.: — без пробелов и управляющих символов в журналах.
    public static bool IsValid([NotNullWhen(true)] string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
