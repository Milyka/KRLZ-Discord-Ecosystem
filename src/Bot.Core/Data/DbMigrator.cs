using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Bot.Data;

public sealed class DbMigrator(DbConnectionFactory dbFactory, ILogger<DbMigrator> logger)
{
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        DapperConfig.Configure();
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS schema_migrations (
                name TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL
            );");

        var applied = (await conn.QueryAsync<string>("SELECT name FROM schema_migrations;")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames()
            .Where(r => r.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var resourceName in resources)
        {
            if (applied.Contains(resourceName))
                continue;

            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Не найден встроенный SQL-ресурс {resourceName}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);

            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(sql, transaction: tx);
            await conn.ExecuteAsync(
                "INSERT INTO schema_migrations (name, applied_at) VALUES (@Name, @AppliedAt);",
                new { Name = resourceName, AppliedAt = DateTimeOffset.UtcNow },
                transaction: tx);
            await tx.CommitAsync(ct);

            logger.LogInformation("Применена миграция базы данных: {Migration}", resourceName);
        }
    }
}
