using System.Net;
using System.Text;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class TicketService(
    TicketRepository ticketRepo,
    GuildSettingsRepository settingsRepo,
    EmbedFactory embeds,
    ILogger<TicketService> logger)
{
    public async Task<(bool Success, string Message, RestTextChannel? Channel)> CreateTicketAsync(
        SocketGuild guild,
        SocketGuildUser user,
        string categoryKey,
        string subject,
        CancellationToken ct = default)
    {
        var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
        var openCount = await ticketRepo.GetOpenCountForUserAsync(guild.Id, user.Id, ct);

        if (openCount >= cfg.MaxOpenTickets)
            return (false, $"У вас уже открыто максимальное число тикетов ({cfg.MaxOpenTickets}).", null);

        var number = await ticketRepo.GetNextTicketNumberAsync(guild.Id, ct);
        var channelName = $"ticket-{number:D4}";

        var overwrites = new List<Overwrite>
        {
            new(guild.EveryoneRole.Id, PermissionTarget.Role, new OverwritePermissions(viewChannel: PermValue.Deny)),
            new(user.Id, PermissionTarget.User, new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Allow,
                readMessageHistory: PermValue.Allow,
                attachFiles: PermValue.Allow,
                embedLinks: PermValue.Allow)),
            new(guild.CurrentUser.Id, PermissionTarget.User, new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Allow,
                manageChannel: PermValue.Allow,
                readMessageHistory: PermValue.Allow))
        };

        if (cfg.ModeratorRoleId != 0 && guild.GetRole(cfg.ModeratorRoleId) is not null)
        {
            overwrites.Add(new Overwrite(cfg.ModeratorRoleId, PermissionTarget.Role, new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Allow,
                readMessageHistory: PermValue.Allow,
                attachFiles: PermValue.Allow)));
        }

        if (cfg.AdminRoleId != 0 && guild.GetRole(cfg.AdminRoleId) is not null)
        {
            overwrites.Add(new Overwrite(cfg.AdminRoleId, PermissionTarget.Role, new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Allow,
                readMessageHistory: PermValue.Allow,
                manageMessages: PermValue.Allow)));
        }

        var channel = await guild.CreateTextChannelAsync(channelName, props =>
        {
            props.Topic = $"Тикет #{number:D4} | Автор: {user.Username} ({user.Id}) | Категория: {categoryKey}";
            props.PermissionOverwrites = overwrites;
            if (cfg.TicketCategoryId != 0 && guild.GetCategoryChannel(cfg.TicketCategoryId) is not null)
                props.CategoryId = cfg.TicketCategoryId;
        });

        var entity = await ticketRepo.CreateTicketAsync(guild.Id, number, channel.Id, user.Id, categoryKey, subject, ct);

        var categoryTitle = categoryKey switch
        {
            "report" => "Жалоба на участника",
            "application" => "Заявка в команду / на роль",
            _ => "Общий вопрос"
        };

        var embed = embeds.CreateBase($"Тикет #{entity.TicketNumber:D4} — {categoryTitle}")
            .WithDescription("Опишите вашу ситуацию максимально подробно. Сотрудники поддержки ответят вам в ближайшее время.")
            .AddField("Автор обращения", $"<@{user.Id}> (`{user.Id}`)", true)
            .AddField("Категория", categoryTitle, true)
            .AddField("Тема", subject, false)
            .Build();

        var components = new ComponentBuilder()
            .WithButton("Взять в работу", "ticket:claim", ButtonStyle.Success)
            .WithButton("Добавить участника", "ticket:adduser", ButtonStyle.Secondary)
            .WithButton("Вызвать старшего", "ticket:escalate", ButtonStyle.Primary)
            .WithButton("Закрыть тикет", "ticket:close", ButtonStyle.Danger)
            .Build();

        var mentionText = cfg.ModeratorRoleId != 0 ? $"<@{user.Id}> <@&{cfg.ModeratorRoleId}>" : $"<@{user.Id}>";
        await channel.SendMessageAsync(text: mentionText, embed: embed, components: components);

        return (true, $"Тикет создан: <#{channel.Id}>", channel);
    }

    public async Task TrackIncomingMessageAsync(SocketUserMessage message, CancellationToken ct = default)
    {
        if (message.Channel is not SocketTextChannel textChannel)
            return;

        var ticket = await ticketRepo.GetByChannelAsync(textChannel.Id, ct);
        if (ticket is null || ticket.Status != "open")
            return;

        var attachments = message.Attachments.Count > 0
            ? string.Join(", ", message.Attachments.Select(a => a.Url))
            : null;

        await ticketRepo.AddMessageAsync(
            ticket.Id,
            message.Author.Id,
            $"{message.Author.Username}",
            message.Content ?? string.Empty,
            attachments,
            ct);
    }

    public async Task<bool> CloseTicketAsync(
        SocketGuild guild,
        SocketTextChannel channel,
        SocketGuildUser closedBy,
        string reason,
        CancellationToken ct = default)
    {
        var ticket = await ticketRepo.GetByChannelAsync(channel.Id, ct);
        if (ticket is null || ticket.Status != "open")
            return false;

        await ticketRepo.CloseTicketAsync(ticket.Id, reason, ct);
        var messages = await ticketRepo.GetMessagesAsync(ticket.Id, ct);
        var htmlBytes = GenerateHtmlTranscript(guild, ticket, closedBy, reason, messages);
        var fileName = $"ticket-{ticket.TicketNumber:D4}-transcript.html";

        var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
        var summaryEmbed = embeds.CreateBase($"Тикет #{ticket.TicketNumber:D4} закрыт")
            .AddField("Автор", $"<@{ticket.OwnerId}> (`{ticket.OwnerId}`)", true)
            .AddField("Закрыл", $"<@{closedBy.Id}> (`{closedBy.Id}`)", true)
            .AddField("Ответственный", ticket.ClaimedById.HasValue ? $"<@{ticket.ClaimedById.Value}>" : "Не назначен", true)
            .AddField("Категория", ticket.Category, true)
            .AddField("Сообщений", messages.Count.ToString(), true)
            .AddField("Причина закрытия", reason, false)
            .Build();

        if (cfg.TicketLogChannelId != 0 && guild.GetTextChannel(cfg.TicketLogChannelId) is { } logChannel)
        {
            await using var logStream = new MemoryStream(htmlBytes);
            await logChannel.SendFileAsync(logStream, fileName, embed: summaryEmbed);
        }

        var ownerUser = await guild.GetGuildUserSafeAsync(ticket.OwnerId);
        if (ownerUser is not null)
        {
            try
            {
                var ratingButtons = new ComponentBuilder()
                    .WithButton("1 ★", $"ticket:rate:{ticket.Id}:1", ButtonStyle.Secondary)
                    .WithButton("2 ★", $"ticket:rate:{ticket.Id}:2", ButtonStyle.Secondary)
                    .WithButton("3 ★", $"ticket:rate:{ticket.Id}:3", ButtonStyle.Secondary)
                    .WithButton("4 ★", $"ticket:rate:{ticket.Id}:4", ButtonStyle.Primary)
                    .WithButton("5 ★", $"ticket:rate:{ticket.Id}:5", ButtonStyle.Success)
                    .Build();

                await using var dmStream = new MemoryStream(htmlBytes);
                await ownerUser.SendFileAsync(dmStream, fileName, text: "Ваш тикет закрыт. Оцените качество работы поддержки:", embed: summaryEmbed, components: ratingButtons);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Не удалось отправить транскрипт тикета пользователю {OwnerId}", ticket.OwnerId);
            }
        }

        await channel.DeleteAsync();
        return true;
    }

    private static byte[] GenerateHtmlTranscript(
        SocketGuild guild,
        TicketEntity ticket,
        SocketGuildUser closedBy,
        string closeReason,
        IReadOnlyList<TicketMessageEntity> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"UTF-8\">");
        sb.AppendLine($"<title>Транскрипт тикета #{ticket.TicketNumber:D4} — {WebUtility.HtmlEncode(guild.Name)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { background:#11131a; color:#e5e7eb; font-family:'Segoe UI',sans-serif; margin:0; padding:24px; }");
        sb.AppendLine(".header { background:#1b1f2e; padding:20px; border-radius:12px; margin-bottom:20px; border-left:4px solid #5865F2; }");
        sb.AppendLine(".msg { background:#171a26; padding:12px 16px; border-radius:8px; margin-bottom:10px; }");
        sb.AppendLine(".meta { color:#9ca3af; font-size:12px; margin-bottom:6px; }");
        sb.AppendLine(".author { color:#818cf8; font-weight:600; }");
        sb.AppendLine(".content { white-space:pre-wrap; word-break:break-word; font-size:14px; }");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<div class=\"header\">");
        sb.AppendLine($"<h2>Тикет #{ticket.TicketNumber:D4} ({WebUtility.HtmlEncode(ticket.Category)})</h2>");
        sb.AppendLine($"<p><b>Сервер:</b> {WebUtility.HtmlEncode(guild.Name)} | <b>Автор ID:</b> {ticket.OwnerId}</p>");
        sb.AppendLine($"<p><b>Тема:</b> {WebUtility.HtmlEncode(ticket.Subject)}</p>");
        sb.AppendLine($"<p><b>Закрыл:</b> {WebUtility.HtmlEncode(closedBy.Username)} ({closedBy.Id}) | <b>Причина:</b> {WebUtility.HtmlEncode(closeReason)}</p>");
        sb.AppendLine("</div>");

        foreach (var msg in messages)
        {
            sb.AppendLine("<div class=\"msg\">");
            sb.AppendLine($"<div class=\"meta\"><span class=\"author\">{WebUtility.HtmlEncode(msg.AuthorName)} ({msg.AuthorId})</span> — {msg.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC</div>");
            sb.AppendLine($"<div class=\"content\">{WebUtility.HtmlEncode(msg.Content)}</div>");
            if (!string.IsNullOrWhiteSpace(msg.Attachments))
                sb.AppendLine($"<div class=\"meta\">Вложения: {WebUtility.HtmlEncode(msg.Attachments)}</div>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
