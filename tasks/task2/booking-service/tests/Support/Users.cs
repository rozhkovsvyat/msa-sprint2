using Hotelio.Bookings.Domain;

namespace Hotelio.Bookings.Tests.Support;

internal static class Users
{
    public static readonly UserFacts Active = WithStatus("ACTIVE");
    public static readonly UserFacts Blacklisted = new(Exists: true, Active: true, Blacklisted: true, Status: "ACTIVE");

    public static UserFacts WithStatus(string status) => new(Exists: true, Active: true, Blacklisted: false, Status: status);
}
