using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Infrastructure;

public sealed class TelemetryService : IDisposable
{
    public const ulong AdminDirectChannelId = 1565459290150535271;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly ILogger<TelemetryService> _logger;
    private readonly string _logsDir;
    private readonly string _liveLogPath;
    private readonly string _jsonlLogPath;
    private readonly string _adminDispatchLogPath;
    private readonly string _issuesLogPath;
    private readonly string _statsJsonPath;

    private readonly Channel<string> _logQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;

    private long _totalMessages;
    private long _totalCommands;
    private long _totalErrors;
    private long _totalInteractions;
    private readonly ConcurrentDictionary<string, long> _commandCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ulong, long> _userActivity = new();

    public TelemetryService(ILogger<TelemetryService> logger)
    {
        _logger = logger;

        var baseLogs = ResolveProjectLogsDirectory();
        if (!Directory.Exists(baseLogs))
            Directory.CreateDirectory(baseLogs);

        _logsDir = baseLogs;
        _liveLogPath = Path.Combine(_logsDir, "telemetry_live.log");
        _jsonlLogPath = Path.Combine(_logsDir, "telemetry_events.jsonl");
        _adminDispatchLogPath = Path.Combine(_logsDir, "admin_dispatch.log");
        _issuesLogPath = Path.Combine(_logsDir, "issues_and_errors.log");
        _statsJsonPath = Path.Combine(_logsDir, "telemetry_stats.json");

        _writerTask = Task.Run(ProcessLogQueueAsync);

        UpdateStatsJson();
        QueueLog($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [SYSTEM] TelemetryService инициализирован (.NET 10). Целевой канал админа: {AdminDirectChannelId}");

        _logger.LogInformation("🚀 TelemetryService запущен. Логирование ведётся в {LogsDir}", _logsDir);
    }

    private static string ResolveProjectLogsDirectory()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "logs")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "logs")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "logs"))
        };

        foreach (var candidate in candidates)
        {
            var parent = Directory.GetParent(candidate)?.FullName;
            if (parent is not null && File.Exists(Path.Combine(parent, "KRLZ.sln")))
                return candidate;
        }

        return candidates[0];
    }

    public void LogMessage(SocketUserMessage message)
    {
        if (message.Author.IsBot)
            return;

        Interlocked.Increment(ref _totalMessages);
        _userActivity.AddOrUpdate(message.Author.Id, 1, (_, v) => v + 1);

        var now = DateTimeOffset.UtcNow;
        var channel = message.Channel;
        var channelName = channel is SocketGuildChannel gc ? $"#{gc.Name}" : channel.Name;
        var author = message.Author;
        var displayName = author is SocketGuildUser gu ? gu.DisplayName : author.Username;
        var content = message.Content ?? string.Empty;
        var isTargetAdminChannel = channel.Id == AdminDirectChannelId;

        var attachStr = message.Attachments.Count > 0
            ? $" [Вложения: {string.Join(", ", message.Attachments.Select(a => a.Url))}]"
            : string.Empty;

        var prefix = isTargetAdminChannel ? "🚨 [ADMIN_DISPATCH]" : "[MSG]";
        var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] {prefix} [{channelName} ({channel.Id})] {displayName} (@{author.Username} | {author.Id}): \"{content}\"{attachStr}";

        QueueLog(line);

        var jsonRecord = new
        {
            timestamp = now.ToString("O"),
            type = isTargetAdminChannel ? "admin_dispatch" : "message",
            channelId = channel.Id,
            channelName,
            userId = author.Id,
            username = author.Username,
            displayName,
            content,
            attachments = message.Attachments.Select(a => a.Url).ToArray()
        };
        QueueJson(jsonRecord);

        if (isTargetAdminChannel)
        {
            AppendDedicatedFile(_adminDispatchLogPath, line);
            _logger.LogWarning("📨 Сообщение в целевом канале {ChannelId} от {User}: {Content}", AdminDirectChannelId, displayName, content);
        }

        UpdateStatsJson();
    }

    public void LogCommandExecuted(
        ICommandInfo? commandInfo,
        IInteractionContext context,
        IResult result,
        TimeSpan duration,
        string? argsSummary,
        Exception? exception = null)
    {
        Interlocked.Increment(ref _totalCommands);
        var now = DateTimeOffset.UtcNow;
        var cmdName = commandInfo?.Name ?? "unknown";
        _commandCounts.AddOrUpdate(cmdName, 1, (_, c) => c + 1);

        var user = context.User;
        var displayName = user is SocketGuildUser gu ? gu.DisplayName : user.Username;
        var channelName = context.Channel is SocketGuildChannel gc ? $"#{gc.Name}" : context.Channel.Name;

        var argsPart = string.IsNullOrWhiteSpace(argsSummary) ? string.Empty : $" | Args: {argsSummary}";
        var durMs = (long)duration.TotalMilliseconds;

        if (result.IsSuccess)
        {
            var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [CMD_OK] [{channelName}] {displayName} (@{user.Username}): /{cmdName}{argsPart} ({durMs}ms)";
            QueueLog(line);

            QueueJson(new
            {
                timestamp = now.ToString("O"),
                type = "command_ok",
                command = cmdName,
                channelId = context.Channel.Id,
                channelName,
                userId = user.Id,
                username = user.Username,
                displayName,
                args = argsSummary,
                durationMs = durMs,
                status = "success"
            });
        }
        else
        {
            Interlocked.Increment(ref _totalErrors);
            var errReason = result.ErrorReason ?? result.Error.ToString() ?? "Unknown error";
            var exMsg = exception?.Message ?? (result is ExecuteResult er ? er.Exception?.Message : null);
            var exStr = string.IsNullOrWhiteSpace(exMsg) ? string.Empty : $" | Ex: {exMsg}";

            var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] ❌ [CMD_ERR] [{channelName}] {displayName} (@{user.Username}): /{cmdName}{argsPart} -> Ошибка: {errReason}{exStr} ({durMs}ms)";
            QueueLog(line);

            var issueDetails = new StringBuilder();
            issueDetails.AppendLine($"[{now:yyyy-MM-dd HH:mm:ss.fff}] ❌ ОШИБКА В КОМАНДЕ /{cmdName}");
            issueDetails.AppendLine($"Пользователь: {displayName} (@{user.Username} | {user.Id})");
            issueDetails.AppendLine($"Канал: {channelName} ({context.Channel.Id})");
            issueDetails.AppendLine($"Аргументы: {argsSummary}");
            issueDetails.AppendLine($"Причина: {errReason}");
            if (exception is not null)
            {
                issueDetails.AppendLine($"Исключение: {exception.GetType().FullName}: {exception.Message}");
                issueDetails.AppendLine($"Стек:\n{exception.StackTrace}");
            }
            else if (result is ExecuteResult erEx && erEx.Exception is not null)
            {
                issueDetails.AppendLine($"Исключение: {erEx.Exception.GetType().FullName}: {erEx.Exception.Message}");
                issueDetails.AppendLine($"Стек:\n{erEx.Exception.StackTrace}");
            }
            issueDetails.AppendLine(new string('-', 60));

            AppendDedicatedFile(_issuesLogPath, issueDetails.ToString());

            QueueJson(new
            {
                timestamp = now.ToString("O"),
                type = "command_err",
                command = cmdName,
                channelId = context.Channel.Id,
                channelName,
                userId = user.Id,
                username = user.Username,
                displayName,
                args = argsSummary,
                durationMs = durMs,
                status = "error",
                errorReason = errReason,
                exception = exMsg
            });
        }

        UpdateStatsJson();
    }

    public void LogInteraction(SocketInteraction interaction)
    {
        var now = DateTimeOffset.UtcNow;
        var user = interaction.User;
        var displayName = user is SocketGuildUser gu ? gu.DisplayName : user.Username;
        var channelName = interaction.Channel is SocketGuildChannel gc ? $"#{gc.Name}" : interaction.Channel?.Name ?? "unknown";

        string typeStr;
        string detail;

        if (interaction is SocketMessageComponent comp)
        {
            typeStr = comp.Data.Type.ToString();
            detail = comp.Data.CustomId;
        }
        else if (interaction is SocketModal modal)
        {
            typeStr = "Modal";
            detail = $"{modal.Data.CustomId} ({string.Join(", ", modal.Data.Components.Select(c => $"{c.CustomId}={c.Value}"))})";
        }
        else
        {
            typeStr = interaction.Type.ToString();
            detail = string.Empty;
        }

        Interlocked.Increment(ref _totalInteractions);
        var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [INTERACTION:{typeStr}] [{channelName}] {displayName} (@{user.Username}): {detail}";
        QueueLog(line);

        QueueJson(new
        {
            timestamp = now.ToString("O"),
            type = "interaction",
            interactionType = typeStr,
            channelId = interaction.Channel?.Id ?? 0,
            channelName,
            userId = user.Id,
            username = user.Username,
            displayName,
            detail
        });
    }

    public void RecordNote(string tag, string note)
    {
        var now = DateTimeOffset.UtcNow;
        var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] 📌 [{tag}] {note}";
        QueueLog(line);
        AppendDedicatedFile(_issuesLogPath, line);
    }

    private void QueueLog(string line)
    {
        _logQueue.Writer.TryWrite(line);
    }

    private void QueueJson(object record)
    {
        try
        {
            var json = JsonSerializer.Serialize(record);
            AppendDedicatedFile(_jsonlLogPath, json);
        }
        catch
        {
        }
    }

    private static void AppendDedicatedFile(string filePath, string text)
    {
        try
        {
            File.AppendAllText(filePath, text + Environment.NewLine, Utf8NoBom);
        }
        catch
        {
        }
    }

    private async Task ProcessLogQueueAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                if (await _logQueue.Reader.WaitToReadAsync(_cts.Token))
                {
                    while (_logQueue.Reader.TryRead(out var line))
                    {
                        await File.AppendAllTextAsync(_liveLogPath, line + Environment.NewLine, Utf8NoBom, _cts.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ошибка записи live_telemetry.log");
                await Task.Delay(500, _cts.Token);
            }
        }
    }

    private void UpdateStatsJson()
    {
        try
        {
            var topCommands = _commandCounts
                .OrderByDescending(kv => kv.Value)
                .Take(15)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var stats = new
            {
                updatedAt = DateTimeOffset.UtcNow.ToString("O"),
                totalMessages = _totalMessages,
                totalCommands = _totalCommands,
                totalErrors = _totalErrors,
                totalInteractions = _totalInteractions,
                uniqueActiveUsers = _userActivity.Count,
                topCommands
            };

            var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_statsJsonPath, json, Utf8NoBom);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
