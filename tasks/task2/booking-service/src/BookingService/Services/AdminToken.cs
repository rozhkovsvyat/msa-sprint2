using System.Security.Cryptography;
using System.Text;
using Grpc.Core;

namespace Hotelio.Bookings.Services;

/// <summary>Доступ к служебным методам: метаданные authorization: Bearer &lt;BOOKING_ADMIN_TOKEN&gt;.</summary>
public sealed class AdminToken(string? token)
{
    private const string Scheme = "Bearer ";

    public bool Configured => !string.IsNullOrEmpty(token);

    public bool Accepts(Metadata? headers)
    {
        if (!Configured || headers is null) return false;
        var value = headers.GetValue("authorization");
        if (value is null || !value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;

        var presented = Encoding.UTF8.GetBytes(value[Scheme.Length..].Trim());
        return presented.Length != 0 && CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(token!));
    }
}
