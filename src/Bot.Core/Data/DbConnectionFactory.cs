using Bot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Bot.Data;

public sealed class DbConnectionFactory
{
    private readonly SettingsProvider _settingsProvider;
    private readonly string _resolvedConnectionString;

    public DbConnectionFactory(SettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
        _resolvedConnectionString = ResolveConnectionString(settingsProvider.Current.DatabaseConnectionString);
        settingsProvider.Current.DatabaseConnectionString = _resolvedConnectionString;
    }

    private static string ResolveConnectionString(string rawConnStr)
    {
        if (!rawConnStr.Contains("Data Source=krlz.db", StringComparison.OrdinalIgnoreCase))
            return rawConnStr;

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "Bot", "krlz.db")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "krlz.db")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Bot", "krlz.db")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "krlz.db")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "krlz.db"))
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return $"Data Source={path};Cache=Shared";
            }
        }

        var botDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "Bot"));
        if (Directory.Exists(botDir))
            return $"Data Source={Path.Combine(botDir, "krlz.db")};Cache=Shared";

        return rawConnStr;
    }

    public async Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_resolvedConnectionString);
        await conn.OpenAsync(ct);

        await using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 8000;";
        await pragmaCmd.ExecuteNonQueryAsync(ct);

        return conn;
    }
}
