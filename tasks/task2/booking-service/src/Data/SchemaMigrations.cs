using DbUp;
using Npgsql;

namespace Hotelio.Bookings.Data;

/// <summary>
/// Версионные миграции схемы: скрипты db/migrations встроены в сборку, применённые DbUp отмечает в таблице
/// schemaversions. Применяет их отдельная команда migrate из того же образа до запуска новой версии сервиса.
/// </summary>
public static class SchemaMigrations
{
    // DbUp записывает в schemaversions имя встроенного ресурса скрипта.
    private static readonly string[] Expected = typeof(SchemaMigrations).Assembly.GetManifestResourceNames()
        .Where(name => name.Contains(".db.migrations.")).ToArray();

    public static bool Apply(string connectionString, ILoggerFactory logging) => DeployChanges.To
        .PostgresqlDatabase(connectionString)
        .WithScriptsEmbeddedInAssembly(typeof(SchemaMigrations).Assembly, name => name.Contains(".db.migrations."))
        .WithTransactionPerScript()
        .LogTo(logging)
        .Build()
        .PerformUpgrade()
        .Successful;

    /// <summary>БД доступна и все миграции образа применены.</summary>
    public static async Task<bool> IsCurrentAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        try
        {
            var applied = new HashSet<string>(StringComparer.Ordinal);
            await using var cmd = db.CreateCommand("select scriptname from schemaversions");
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) applied.Add(reader.GetString(0));
            return Expected.All(applied.Contains);
        }
        catch (Exception)
        {
            return false; // БД недоступна или журнала миграций ещё нет
        }
    }
}
