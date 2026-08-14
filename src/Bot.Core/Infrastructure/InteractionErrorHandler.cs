using System.Collections.Concurrent;
using System.Diagnostics;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Infrastructure;

public sealed class InteractionErrorHandler(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    EmbedFactory embeds,
    TelemetryService telemetryService,
    ILogger<InteractionErrorHandler> logger)
{
    private readonly ConcurrentDictionary<ulong, Stopwatch> _watchMap = new();

    public void Initialize()
    {
        client.InteractionCreated += OnInteractionCreatedAsync;
        interactions.InteractionExecuted += OnInteractionExecutedAsync;
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        _watchMap[interaction.Id] = Stopwatch.StartNew();

        if (interaction is not SocketSlashCommand)
        {
            telemetryService.LogInteraction(interaction);
        }

        try
        {
            const ulong auctionChannelId = 1566933458171994242UL;
            if (interaction.ChannelId == auctionChannelId)
            {
                if (interaction is SocketSlashCommand slashCmd &&
                    !slashCmd.Data.Name.Equals("auction", StringComparison.OrdinalIgnoreCase))
                {
                    await interaction.RespondAsync(
                        embed: embeds.Error("В канале <#1566933458171994242> разрешены исключительно команды `/auction`. Для других команд используйте канал <#1565456386970886154>."),
                        ephemeral: true);
                    return;
                }
            }

            var ctx = new SocketInteractionContext(client, interaction);
            await interactions.ExecuteCommandAsync(ctx, services);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка выполнения интерактивного вызова {Type} от {UserId}", interaction.Type, interaction.User.Id);
            telemetryService.RecordNote("INTERACTION_EXCEPTION", $"Вызов {interaction.Type} от {interaction.User.Username}: {ex.Message}");
            await SendErrorAsync(interaction, "Произошла внутренняя ошибка при обработке вашего запроса.");
        }
    }

    private async Task OnInteractionExecutedAsync(ICommandInfo? commandInfo, IInteractionContext context, IResult result)
    {
        var duration = _watchMap.TryRemove(context.Interaction.Id, out var sw) ? sw.Elapsed : TimeSpan.Zero;

        string? argsSummary = null;
        if (context.Interaction is SocketSlashCommand slashCmd)
        {
            argsSummary = FormatOptions(slashCmd.Data.Options);
        }

        var exception = result is ExecuteResult er ? er.Exception : null;
        telemetryService.LogCommandExecuted(commandInfo, context, result, duration, argsSummary, exception);

        if (result.IsSuccess)
            return;

        var message = result.Error switch
        {
            InteractionCommandError.UnmetPrecondition => result.ErrorReason,
            InteractionCommandError.BadArgs => "Переданы некорректные параметры команды.",
            InteractionCommandError.ConvertFailed => "Не удалось распознать один из аргументов команды.",
            InteractionCommandError.UnknownCommand => "Команда не найдена или устарела.",
            InteractionCommandError.Exception => "Команда завершилась с ошибкой. Попробуйте позже.",
            _ => string.IsNullOrWhiteSpace(result.ErrorReason) ? "Не удалось выполнить команду." : result.ErrorReason
        };

        if (result is ExecuteResult execResult && execResult.Exception is not null)
        {
            logger.LogError(execResult.Exception, "Исключение в команде {Command} от {User}", commandInfo?.Name ?? "unknown", context.User.Id);
        }

        await SendErrorAsync(context.Interaction, message);
    }

    private static string FormatOptions(IReadOnlyCollection<SocketSlashCommandDataOption>? options)
    {
        if (options is null || options.Count == 0)
            return string.Empty;

        return string.Join(", ", options.Select(o =>
        {
            if (o.Options?.Count > 0)
                return $"{o.Name} [{FormatOptions(o.Options)}]";
            return $"{o.Name}: {o.Value}";
        }));
    }

    private async Task SendErrorAsync(IDiscordInteraction interaction, string message)
    {
        var embed = embeds.Error(message);
        try
        {
            if (interaction.HasResponded)
                await interaction.FollowupAsync(embed: embed, ephemeral: true);
            else
                await interaction.RespondAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось отправить сообщение об ошибке пользователю {UserId}", interaction.User.Id);
        }
    }
}
