using Npgsql;
using Shouldly;
using Xunit;

namespace Hotelio.Bookings.IntegrationTests;

/// <summary>Короткие запросы к БД из тестов.</summary>
internal static class Sql
{
    public static async Task<T> ScalarAsync<T>(this NpgsqlDataSource db, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = Command(db, sql, args);
        var value = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value.ShouldBeOfType<T>($"запрос не вернул {typeof(T).Name}: {sql}");
    }

    public static async Task ExecuteAsync(this NpgsqlDataSource db, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = Command(db, sql, args);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static NpgsqlCommand Command(NpgsqlDataSource db, string sql, (string Name, object Value)[] args)
    {
        var cmd = db.CreateCommand(sql);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
