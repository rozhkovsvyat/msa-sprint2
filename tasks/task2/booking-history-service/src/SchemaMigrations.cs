using DbUp;
using DbUp.Builder;
using DbUp.Engine;
using DbUp.Support;
using Npgsql;

namespace Hotelio.BookingHistory;

/// <summary>
/// Версионные миграции схемы (db/migrations, журнал DbUp — schemaversions) и роль аналитика (db/roles).
/// Применяет их отдельная команда migrate из того же образа до запуска новой версии сервиса.
/// </summary>
public static class SchemaMigrations
{
    // DbUp записывает в schemaversions имя встроенного ресурса скрипта.
    private static readonly string[] Expected = typeof(SchemaMigrations).Assembly.GetManifestResourceNames()
        .Where(name => name.Contains(".db.migrations.")).ToArray();

    public static bool Apply(string connectionString, string analystPassword, ILoggerFactory logging)
    {
        var result = Migrations(connectionString)
            // Роль и права выполняются при каждом migrate и после миграций: гранты ссылаются на их таблицы.
            .WithScriptsEmbeddedInAssembly(typeof(SchemaMigrations).Assembly, name => name.Contains(".db.roles."),
                new SqlScriptOptions { ScriptType = ScriptType.RunAlways, RunGroupOrder = DbUpDefaults.DefaultRunGroupOrder + 1 })
            .LogTo(logging)
            .Build()
            .PerformUpgrade();
        if (!result.Successful) return false;
        SetAnalystPassword(connectionString, analystPassword);
        return true;
    }

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

    private static UpgradeEngineBuilder Migrations(string connectionString) => DeployChanges.To
        .PostgresqlDatabase(connectionString)
        .WithScriptsEmbeddedInAssembly(typeof(SchemaMigrations).Assembly, name => name.Contains(".db.migrations."))
        .WithTransactionPerScript();

    // Пароль передаётся параметром, а не текстом SQL, поэтому не попадает в скрипты и журнал DbUp.
    private static void SetAnalystPassword(string connectionString, string password)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        using (var set = new NpgsqlCommand("select set_config('hotelio.analyst_password', @password, true)", conn, tx))
        {
            set.Parameters.AddWithValue("password", password);
            set.ExecuteNonQuery();
        }
        using (var alter = new NpgsqlCommand(
            "do $$ begin execute format('alter role analyst with login password %L', current_setting('hotelio.analyst_password')); end $$",
            conn, tx))
        {
            alter.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
