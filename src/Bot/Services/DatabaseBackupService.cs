using Bot.Data;
using Bot.Infrastructure;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class DatabaseBackupService(
    DbConnectionFactory dbFactory,
    CommunityEventsRepository eventsRepo,
    SettingsProvider settingsProvider,
    EmbedFactory embeds,
    ILogger<DatabaseBackupService> logger)
{
    private DateTimeOffset _lastDbBackup = DateTimeOffset.UtcNow;

    public async Task<(bool Success, string Message, string FilePath, long SizeBytes)> CreateDatabaseBackupNowAsync(
        DiscordSocketClient client,
        ulong guildId,
        ulong triggeredById,
        CancellationToken ct = default)
    {
        var cfg = settingsProvider.Current.DatabaseBackup;
        var backupDir = Path.GetFullPath(cfg.BackupDirectory);
        Directory.CreateDirectory(backupDir);

        var fileName = $"krlz-db-{DateTime.UtcNow:yyyyMMdd-HHmmss}.sqlite";
        var fullPath = Path.Combine(backupDir, fileName);

        await using (var conn = await dbFactory.CreateOpenConnectionAsync(ct))
        {
            await using var cmd = conn.CreateCommand();
            var escapedPath = fullPath.Replace("'", "''");
            cmd.CommandText = $"VACUUM INTO '{escapedPath}';";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var fileInfo = new FileInfo(fullPath);
        var sizeBytes = fileInfo.Exists ? fileInfo.Length : 0L;
        var sentToChannel = false;

        if (cfg.BackupChannelId != 0 && client.GetGuild(guildId)?.GetTextChannel(cfg.BackupChannelId) is { } channel)
        {
            try
            {
                var embed = embeds.Info("Резервная копия базы данных SQLite",
                    $"**Файл:** `{fileName}`\n**Размер:** `{sizeBytes / 1024.0:F1} КБ`\n**Инициатор:** <@{triggeredById}>");
                await channel.SendFileAsync(fullPath, embed: embed);
                sentToChannel = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось отправить файл бэкапа БД в канал {ChannelId}", cfg.BackupChannelId);
            }
        }

        await eventsRepo.LogDatabaseBackupAsync(guildId, fileName, fullPath, sizeBytes, triggeredById, sentToChannel, ct);
        RotateLocalCopies(backupDir, Math.Max(1, cfg.KeepLocalCopies));

        return (true, $"Создана копия `{fileName}` ({sizeBytes / 1024.0:F1} КБ).", fullPath, sizeBytes);
    }

    public async Task ProcessScheduledDatabaseBackupAsync(DiscordSocketClient client, CancellationToken ct = default)
    {
        var cfg = settingsProvider.Current.DatabaseBackup;
        if (!cfg.Enabled)
            return;

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastDbBackup).TotalHours < Math.Max(1, cfg.IntervalHours))
            return;

        _lastDbBackup = now;
        var guild = client.Guilds.FirstOrDefault();
        if (guild is null)
            return;

        try
        {
            await CreateDatabaseBackupNowAsync(client, guild.Id, guild.CurrentUser.Id, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка планового создания копии базы данных SQLite");
        }
    }

    private static void RotateLocalCopies(string backupDir, int keepCount)
    {
        var files = new DirectoryInfo(backupDir)
            .GetFiles("krlz-db-*.sqlite")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Skip(keepCount)
            .ToList();

        foreach (var oldFile in files)
        {
            try { oldFile.Delete(); } catch { }
        }
    }
}
