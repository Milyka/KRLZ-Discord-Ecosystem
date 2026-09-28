using System.Reflection;
using Bot.Data;
using Bot.Infrastructure;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class DiscordBotHostedService(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    SettingsProvider settingsProvider,
    DbMigrator migrator,
    InteractionErrorHandler errorHandler,
    AntispamService antispamService,
    VerificationAndAutomodService automodService,
    ServerLoggingService serverLoggingService,
    LevelingService levelingService,
    PrivateVoiceService privateVoiceService,
    CasinoService casinoService,
    PartyGameService partyGameService,
    TicketService ticketService,
    SecurityService securityService,
    GuildSettingsRepository settingsRepo,
    ModerationRepository modRepo,
    SocialExpansionRepository socialExpRepo,
    TelemetryService telemetryService,
    EmbedFactory embeds,
    ILogger<DiscordBotHostedService> logger) : IHostedService
{
    private bool _modulesRegistered;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await migrator.MigrateAsync(cancellationToken);
        await casinoService.RefundInterruptedSessionsOnStartupAsync(cancellationToken);
        await socialExpRepo.RefundInterruptedMinesSessionsOnStartupAsync(cancellationToken);

        client.Log += OnDiscordLogAsync;
        interactions.Log += OnDiscordLogAsync;
        client.Ready += OnReadyAsync;
        client.MessageReceived += OnMessageReceivedAsync;
        client.MessageUpdated += OnMessageUpdatedAsync;
        client.MessageDeleted += OnMessageDeletedAsync;
        client.UserJoined += OnUserJoinedAsync;
        client.UserLeft += OnUserLeftAsync;
        client.GuildMemberUpdated += OnGuildMemberUpdatedAsync;
        client.UserVoiceStateUpdated += OnUserVoiceStateUpdatedAsync;
        client.ChannelCreated += OnChannelCreatedAsync;
        client.ChannelDestroyed += OnChannelDestroyedAsync;
        client.AuditLogCreated += OnAuditLogCreatedAsync;
        client.RoleUpdated += OnRoleUpdatedAsync;

        errorHandler.Initialize();
        await interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), services);

        var token = settingsProvider.Current.Token;
        if (string.IsNullOrWhiteSpace(token) || token == "PUT_YOUR_DISCORD_BOT_TOKEN_HERE")
        {
            logger.LogCritical("В файле settings.json не указан действительный токен бота Discord.");
            return;
        }

        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await levelingService.FlushActiveVoiceSessionsAsync(client, cancellationToken);
        await client.StopAsync();
        await client.LogoutAsync();
    }

    private Task OnReadyAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await client.SetGameAsync("KRLZ Server | /party-help", type: ActivityType.Watching);
                await privateVoiceService.ReconcileRoomsOnStartupAsync(client);
                await partyGameService.RestoreInterruptedPartyGamesOnStartupAsync(client);

                if (_modulesRegistered)
                    return;

                _modulesRegistered = true;
                var configuredGuildId = settingsProvider.Current.GuildId;

                if (configuredGuildId != 0 && client.GetGuild(configuredGuildId) is not null)
                {
                    await interactions.RegisterCommandsToGuildAsync(configuredGuildId, true);
                    logger.LogInformation("Слэш-команды зарегистрированы для гильдии {GuildId}", configuredGuildId);
                }
                else
                {
                    await interactions.RegisterCommandsGloballyAsync(true);
                    logger.LogInformation("Слэш-команды зарегистрированы глобально");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка инициализации в OnReadyAsync");
            }
        });

        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(SocketMessage rawMessage)
    {
        if (rawMessage is not SocketUserMessage message || message.Author.IsBot)
            return Task.CompletedTask;

        telemetryService.LogMessage(message);

        var auctionChannelId = settingsProvider.Current.Auction.ChannelId != 0
            ? settingsProvider.Current.Auction.ChannelId
            : 1566933458171994242UL;

        if (message.Channel.Id == auctionChannelId)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await message.DeleteAsync();
                    logger.LogInformation("Удалено обычное сообщение {MessageId} от {UserId} в канале аукциона", message.Id, message.Author.Id);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Не удалось удалить обычное сообщение в канале аукциона");
                }
            });
            return Task.CompletedTask;
        }

        if (message.Channel.Id == TelemetryService.AdminDirectChannelId)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAdminDirectChannelAsync(message);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Ошибка обработки сообщения админ-канала");
                }
            });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (await HandleVerificationMessageAsync(message))
                    return;

                await serverLoggingService.HandleMessageReceivedAsync(message);

                if (await antispamService.InspectMessageAsync(message))
                    return;

                if (await automodService.InspectAutomodAsync(message))
                    return;

                await ticketService.TrackIncomingMessageAsync(message);
                await levelingService.HandleMessageAsync(message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при асинхронной обработке сообщения от {UserId}", message.Author.Id);
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleAdminDirectChannelAsync(SocketUserMessage message)
    {
        try
        {
            await message.AddReactionAsync(new Emoji("👀"));
        }
        catch { }

        var text = (message.Content ?? string.Empty).Trim().ToLowerInvariant();

        if (text.Contains("krlz") || text.Contains("крлз"))
        {
            await message.Channel.SendMessageAsync("🫡 **KRLZ (.NET 10) на связи!** Слушаю тебя.", messageReference: new MessageReference(message.Id));
            return;
        }

        if (text.Contains("точка") || text.Contains("точку") || text == "." || text == "ping" || text == "пинг" || text.Contains("активен"))
        {
            await message.Channel.SendMessageAsync(".", messageReference: new MessageReference(message.Id));
            return;
        }

        if (text == "!stats" || text == "стата" || text == "статистика")
        {
            var statsCandidates = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "logs", "telemetry_stats.json"),
                Path.Combine(AppContext.BaseDirectory, "logs", "telemetry_stats.json")
            };
            var statsPath = statsCandidates.FirstOrDefault(File.Exists);
            if (statsPath is not null)
            {
                var content = await File.ReadAllTextAsync(statsPath);
                await message.Channel.SendMessageAsync($"```json\n{content}\n```", messageReference: new MessageReference(message.Id));
            }
            return;
        }

        if (text == "!errors" || text == "ошибки")
        {
            var errCandidates = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "logs", "issues_and_errors.log"),
                Path.Combine(AppContext.BaseDirectory, "logs", "issues_and_errors.log")
            };
            var errPath = errCandidates.FirstOrDefault(p => File.Exists(p) && new FileInfo(p).Length > 0);
            if (errPath is not null)
            {
                var lines = await File.ReadAllLinesAsync(errPath);
                var tail = string.Join("\n", lines.TakeLast(20));
                await message.Channel.SendMessageAsync($"⚠️ **Последние ошибки:**\n```\n{tail}\n```", messageReference: new MessageReference(message.Id));
            }
            else
            {
                await message.Channel.SendMessageAsync("✅ Ошибок в работе команд не зафиксировано!", messageReference: new MessageReference(message.Id));
            }
            return;
        }
    }

    private async Task<bool> HandleVerificationMessageAsync(SocketUserMessage message)
    {
        if (message.Channel is not SocketTextChannel channel)
            return false;

        var guild = channel.Guild;
        if (message.Author is not SocketGuildUser member)
            return false;

        var cfg = await settingsRepo.GetOrCreateAsync(guild.Id);
        var isWelcomeChannel = cfg.WelcomeChannelId != 0 && channel.Id == cfg.WelcomeChannelId;
        var hasActiveCaptcha = automodService.HasActiveCaptcha(guild.Id, member.Id);

        if (!isWelcomeChannel && !hasActiveCaptcha)
            return false;

        if (!hasActiveCaptcha && (member.GuildPermissions.Administrator || member.GuildPermissions.ManageGuild))
            return false;

        try
        {
            await message.DeleteAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось удалить сообщение верификации от {UserId} в канале {ChannelId}", member.Id, channel.Id);
        }

        if (hasActiveCaptcha)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                var warnMsg = await channel.SendMessageAsync(
                    $"⚠️ {member.Mention}, бот не может прочитать текст сообщения из чата, так как в Discord Developer Portal выключен **Message Content Intent**.\n" +
                    $"👉 Пожалуйста, упомяните бота в сообщении (`@{channel.Guild.CurrentUser.Username} КОД`) или нажмите кнопку **«Ввести через окно»**!");
                _ = Task.Delay(9000).ContinueWith(async _ =>
                {
                    try { await warnMsg.DeleteAsync(); } catch { }
                });
                return true;
            }

            var (passed, resultMsg) = await automodService.VerifyCaptchaInputAsync(guild, member, message.Content);
            if (passed)
            {
                var okMsg = await channel.SendMessageAsync($"✅ {member.Mention}, код верный! Верификация пройдена, добро пожаловать на сервер!");
                _ = Task.Delay(6000).ContinueWith(async _ =>
                {
                    try { await okMsg.DeleteAsync(); } catch { }
                });
            }
            else
            {
                var failMsg = await channel.SendMessageAsync($"❌ {member.Mention}, {resultMsg}");
                _ = Task.Delay(6000).ContinueWith(async _ =>
                {
                    try { await failMsg.DeleteAsync(); } catch { }
                });
            }
            return true;
        }

        if (cfg.VerifiedRoleId != 0 && member.Roles.Any(r => r.Id == cfg.VerifiedRoleId))
            return true;

        var promptMsg = await channel.SendMessageAsync(
            $"⚠️ {member.Mention}, сначала нажмите кнопку **«Пройти картинку-капчу»** выше, чтобы получить код с картинки, а затем отправьте его сюда.");
        _ = Task.Delay(7000).ContinueWith(async _ =>
        {
            try { await promptMsg.DeleteAsync(); } catch { }
        });

        return true;
    }

    private Task OnMessageUpdatedAsync(
        Cacheable<IMessage, ulong> before,
        SocketMessage after,
        ISocketMessageChannel channel)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await serverLoggingService.HandleMessageUpdatedAsync(before, after, channel);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ошибка в OnMessageUpdatedAsync");
            }
        });
        return Task.CompletedTask;
    }

    private Task OnMessageDeletedAsync(
        Cacheable<IMessage, ulong> cachedMessage,
        Cacheable<IMessageChannel, ulong> cachedChannel)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await serverLoggingService.HandleMessageDeletedAsync(cachedMessage, cachedChannel);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ошибка в OnMessageDeletedAsync");
            }
        });
        return Task.CompletedTask;
    }

    private Task OnGuildMemberUpdatedAsync(
        Cacheable<SocketGuildUser, ulong> before,
        SocketGuildUser after)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await serverLoggingService.HandleGuildMemberUpdatedAsync(before, after);

                const ulong BanRoleId = 1553561451137073152;
                const ulong VerifiedRoleId = 1548721744871821476;

                if (after.Roles.Any(r => r.Id == BanRoleId) && after.Roles.Any(r => r.Id == VerifiedRoleId))
                {
                    var verifiedRole = after.Guild.GetRole(VerifiedRoleId);
                    if (verifiedRole is not null)
                    {
                        try
                        {
                            await after.RemoveRoleAsync(verifiedRole);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ошибка в OnGuildMemberUpdatedAsync");
            }
        });
        return Task.CompletedTask;
    }

    private Task OnChannelCreatedAsync(SocketChannel channel)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await serverLoggingService.HandleChannelCreatedOrDeletedAsync(channel, created: true);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ошибка в OnChannelCreatedAsync");
            }
        });
        return Task.CompletedTask;
    }

    private Task OnChannelDestroyedAsync(SocketChannel channel)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await serverLoggingService.HandleChannelCreatedOrDeletedAsync(channel, created: false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ошибка в OnChannelDestroyedAsync");
            }
        });
        return Task.CompletedTask;
    }

    private Task OnUserJoinedAsync(SocketGuildUser user)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await securityService.HandleMemberJoinedAsync(user);

                await Task.Delay(1500);
                var verifyChannel = user.Guild.GetTextChannel(1565721954898673704);
                if (verifyChannel is not null)
                {
                    var pingMsg = await verifyChannel.SendMessageAsync($"👋 Привет, {user.Mention}! Чтобы открыть доступ ко всем комнатам сервера, пройди быструю верификацию ниже ⬇️");
                    await Task.Delay(4000);
                    await pingMsg.DeleteAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка обработки входа нового участника {User}", user.Id);
            }
        });
        return Task.CompletedTask;
    }

    private Task OnUserLeftAsync(SocketGuild guild, SocketUser user)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var cfg = await settingsRepo.GetOrCreateAsync(guild.Id);
                if (cfg.WelcomeChannelId != 0 && guild.GetTextChannel(cfg.WelcomeChannelId) is { } welcomeChannel)
                {
                    var embed = embeds.CreateBase("Участник покинул сервер")
                        .WithDescription($"Пользователь **{user.Username}** (`{user.Id}`) покинул сообщество {guild.Name}.")
                        .AddField("Осталось участников", guild.MemberCount.ToString(), true)
                        .Build();
                    await welcomeChannel.SendMessageAsync(embed: embed);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ошибка в OnUserLeftAsync");
            }
        });
        return Task.CompletedTask;
    }

    private Task OnUserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (user is SocketGuildUser member && after.VoiceChannel is not null)
                {
                    var vban = await modRepo.GetVoiceBanAsync(member.Guild.Id, member.Id);
                    if (vban is not null && (!vban.ExpiresAt.HasValue || vban.ExpiresAt.Value > DateTimeOffset.UtcNow))
                    {
                        try
                        {
                            await member.ModifyAsync(p => p.Channel = null);
                            return;
                        }
                        catch
                        {
                        }
                    }
                }

                await serverLoggingService.HandleVoiceLogAsync(user, before, after);
                await privateVoiceService.HandleVoiceStateUpdatedAsync(user, before, after);
                await levelingService.HandleVoiceStateUpdatedAsync(user, before, after);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ошибка в OnUserVoiceStateUpdatedAsync");
            }
        });
        return Task.CompletedTask;
    }

    private Task OnAuditLogCreatedAsync(SocketAuditLogEntry entry, SocketGuild guild)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await securityService.HandleAuditLogEntryAsync(guild, entry);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ошибка в OnAuditLogCreatedAsync");
            }
        });
        return Task.CompletedTask;
    }

    private Task OnRoleUpdatedAsync(SocketRole before, SocketRole after)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await securityService.InspectRoleUpdatedAsync(before, after);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ошибка в OnRoleUpdatedAsync");
            }
        });
        return Task.CompletedTask;
    }

    private Task OnDiscordLogAsync(LogMessage msg)
    {
        if (msg.Message is not null &&
            msg.Message.Contains("Serializer Error", StringComparison.OrdinalIgnoreCase) &&
            msg.Exception is ArgumentNullException)
        {
            // Ignore Discord.Net 3.15 MessageComponentConverter warning on Discord Components V2 (type 17/12/10/14)
            return Task.CompletedTask;
        }

        var level = msg.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            _ => LogLevel.Debug
        };

        logger.Log(level, msg.Exception, "[Discord] {Source}: {Message}", msg.Source, msg.Message);
        return Task.CompletedTask;
    }
}
