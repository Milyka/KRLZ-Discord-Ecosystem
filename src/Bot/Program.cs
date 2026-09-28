using Bot.Data;
using Bot.Infrastructure;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine("logs", "krlz-bot-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();

    builder.Services.AddSingleton<SettingsProvider>();
    builder.Services.AddSingleton<EmbedFactory>();
    builder.Services.AddSingleton<TelemetryService>();

    builder.Services.AddSingleton(sp =>
    {
        var settings = sp.GetRequiredService<SettingsProvider>().Current;
        var intents = GatewayIntents.AllUnprivileged;
        if (settings.EnableMessageContentIntent)
            intents |= GatewayIntents.MessageContent;

        return new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = intents,
            AlwaysDownloadUsers = false,
            MessageCacheSize = 250,
            LogGatewayIntentWarnings = false,
            DefaultRetryMode = RetryMode.AlwaysRetry
        });
    });

    builder.Services.AddSingleton(sp => new InteractionService(sp.GetRequiredService<DiscordSocketClient>(), new InteractionServiceConfig
    {
        DefaultRunMode = RunMode.Async,
        UseCompiledLambda = true,
        AutoServiceScopes = true
    }));

    builder.Services.AddSingleton<DbConnectionFactory>();
    builder.Services.AddSingleton<DbMigrator>();
    builder.Services.AddSingleton<GuildSettingsRepository>();
    builder.Services.AddSingleton<UserRepository>();
    builder.Services.AddSingleton<ModerationRepository>();
    builder.Services.AddSingleton<TicketRepository>();
    builder.Services.AddSingleton<CustomRoleRepository>();
    builder.Services.AddSingleton<ShopRepository>();
    builder.Services.AddSingleton<SocialRepository>();
    builder.Services.AddSingleton<SocialExpansionRepository>();
    builder.Services.AddSingleton<SecurityRepository>();
    builder.Services.AddSingleton<ItemRepository>();
    builder.Services.AddSingleton<AchievementRepository>();
    builder.Services.AddSingleton<ClanRepository>();
    builder.Services.AddSingleton<AuctionRepository>();
    builder.Services.AddSingleton<CasinoRepository>();
    builder.Services.AddSingleton<ProgressionRepository>();
    builder.Services.AddSingleton<PrivateVoiceRepository>();
    builder.Services.AddSingleton<VerificationAndLogsRepository>();
    builder.Services.AddSingleton<CommunityEventsRepository>();
    builder.Services.AddSingleton<PartyGameRepository>();

    builder.Services.AddSingleton<InteractionErrorHandler>();
    builder.Services.AddSingleton<ModerationService>();
    builder.Services.AddSingleton<TicketService>();
    builder.Services.AddSingleton<EconomyService>();
    builder.Services.AddSingleton<AntispamService>();
    builder.Services.AddSingleton<BackupService>();
    builder.Services.AddSingleton<SecurityService>();
    builder.Services.AddSingleton<ItemService>();
    builder.Services.AddSingleton<InventoryService>();
    builder.Services.AddSingleton<AchievementService>();
    builder.Services.AddSingleton<ProgressionService>();
    builder.Services.AddSingleton<ClanService>();
    builder.Services.AddSingleton<AuctionService>();
    builder.Services.AddSingleton<CasinoService>();
    builder.Services.AddSingleton<FishingService>();
    builder.Services.AddSingleton<PrivateVoiceService>();
    builder.Services.AddSingleton<VerificationAndAutomodService>();
    builder.Services.AddSingleton<ServerLoggingService>();
    builder.Services.AddSingleton<CommunityEventsService>();
    builder.Services.AddSingleton<DatabaseBackupService>();
    builder.Services.AddSingleton<LevelingService>();
    builder.Services.AddSingleton<ProfileCardService>();
    builder.Services.AddSingleton<PartyGameService>();

    builder.Services.AddHostedService<DiscordBotHostedService>();
    builder.Services.AddHostedService<SchedulerService>();

    using var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Критический сбой при запуске бота KRLZ");
}
finally
{
    await Log.CloseAndFlushAsync();
}
