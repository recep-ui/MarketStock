using DbUp;
using DbUp.Engine;
using DbUp.Support;
using Microsoft.Extensions.Logging;

namespace PazaryeriHub.Infrastructure.Database;

/// <summary>
/// Şemayı DbUp ile günceller (ADR-004).
/// <list type="number">
/// <item><c>Database/Migrations/*.sql</c>: sürümlü, bir kez çalışır, <c>dbo.SchemaVersions</c> tablosuna yazılır.</item>
/// <item><c>Database/Procedures/*.sql</c>: her seferinde çalışır (CREATE OR ALTER); yordamların güncel hali kaynak koddadır.</item>
/// </list>
/// Her script kendi transaction'ında çalışır; hata olursa o script geri alınır ve güncelleme durur.
/// </summary>
public sealed class DatabaseMigrator(ILogger<DatabaseMigrator> logger)
{
    private const string MigrationsPrefix = "PazaryeriHub.Infrastructure.Database.Migrations.";
    private const string ProceduresPrefix = "PazaryeriHub.Infrastructure.Database.Procedures.";

    /// <summary>Şemayı günceller. Başarısızsa <see cref="InvalidOperationException"/> fırlatır.</summary>
    public IReadOnlyList<string> Migrate(string connectionString)
    {
        var assembly = typeof(DatabaseMigrator).Assembly;

        var upgrader = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(assembly,
                name => name.StartsWith(MigrationsPrefix, StringComparison.Ordinal),
                new SqlScriptOptions { ScriptType = ScriptType.RunOnce, RunGroupOrder = 1 })
            .WithScriptsEmbeddedInAssembly(assembly,
                name => name.StartsWith(ProceduresPrefix, StringComparison.Ordinal),
                new SqlScriptOptions { ScriptType = ScriptType.RunAlways, RunGroupOrder = 2 })
            .JournalToSqlTable("dbo", "SchemaVersions")
            .WithTransactionPerScript()
            .WithExecutionTimeout(TimeSpan.FromMinutes(5))
            // DbUp'ın kendi logu bağlantı bilgisini yazabilir; sonucu aşağıda kendimiz logluyoruz.
            .LogToNowhere()
            .Build();

        var result = upgrader.PerformUpgrade();
        var executed = result.Scripts.Select(s => ShortName(s.Name)).ToList();

        if (!result.Successful)
        {
            var failed = result.ErrorScript is null ? "?" : ShortName(result.ErrorScript.Name);
            logger.LogError(result.Error, "Veritabanı migration'ı başarısız. Hatalı script: {Script}", failed);
            throw new InvalidOperationException($"Veritabanı migration'ı başarısız (script: {failed}).", result.Error);
        }

        logger.LogInformation("Veritabanı şeması güncel. Çalışan script'ler: {Scriptler}", executed);
        return executed;
    }

    private static string ShortName(string resourceName) =>
        resourceName.StartsWith(MigrationsPrefix, StringComparison.Ordinal) ? "Migrations/" + resourceName[MigrationsPrefix.Length..]
        : resourceName.StartsWith(ProceduresPrefix, StringComparison.Ordinal) ? "Procedures/" + resourceName[ProceduresPrefix.Length..]
        : resourceName;
}
