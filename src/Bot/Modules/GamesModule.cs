using Bot.Data;
using Bot.Infrastructure;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class GamesModule(
    UserRepository userRepo,
    GuildSettingsRepository settingsRepo,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly string[] EightBallAnswers =
    [
        "Бесспорно.",
        "Предрешено.",
        "Никаких сомнений.",
        "Определённо да.",
        "Можешь быть уверен в этом.",
        "Мне кажется — да.",
        "Вероятнее всего.",
        "Хорошие перспективы.",
        "Знаки говорят — да.",
        "Пока не ясно, попробуй снова.",
        "Спроси позже.",
        "Лучше не рассказывать сейчас.",
        "Сейчас нельзя предсказать.",
        "Даже не думай.",
        "Мой ответ — нет.",
        "По моим данным — нет.",
        "Перспективы не очень хорошие.",
        "Весьма сомнительно."
    ];

    [SlashCommand("8ball", "Задать вопрос магическому шару предсказаний")]
    public async Task EightBallCommandAsync([Summary("question", "Ваш вопрос")] string question)
    {
        var answer = EightBallAnswers[Random.Shared.Next(EightBallAnswers.Length)];
        var embed = embeds.CreateBase("Магический шар KRLZ")
            .AddField("Вопрос", question, false)
            .AddField("Ответ", $"**{answer}**", false)
            .Build();

        await RespondAsync(embed: embed);
    }

    [SlashCommand("rps", "Сыграть в Камень-Ножницы-Бумага (против бота или участника на ставку)")]
    public async Task RpsCommandAsync(
        [Summary("opponent", "Соперник (оставьте пустым для игры с ботом)")] SocketGuildUser? opponent = null,
        [Summary("bet", "Ставка в валюте сервера")] [MinValue(0)] long bet = 0)
    {
        if (opponent is not null && (opponent.IsBot || opponent.Id == Context.User.Id))
        {
            await RespondAsync(embed: embeds.Error("Выберите реального участника сервера или оставьте поле пустым для игры с ботом."), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var profile = await userRepo.GetOrCreateAsync(Context.Guild.Id, Context.User.Id);
        if (bet > 0 && profile.Balance < bet)
        {
            await RespondAsync(embed: embeds.Error("Недостаточно средств на балансе для указанной ставки."), ephemeral: true);
            return;
        }

        var targetId = opponent?.Id ?? 0;
        var desc = opponent is null
            ? $"Выберите ваш ход кнопкой ниже. Ставка: **{bet:N0} {cfg.CurrencySymbol}**."
            : $"Вызов для <@{opponent.Id}> на ставку **{bet:N0} {cfg.CurrencySymbol}**!\nИнициатор <@{Context.User.Id}>, выберите свой ход:";

        var comp = new ComponentBuilder()
            .WithButton("Камень", $"rps:init:{Context.User.Id}:{targetId}:{bet}:rock", ButtonStyle.Primary)
            .WithButton("Ножницы", $"rps:init:{Context.User.Id}:{targetId}:{bet}:scissors", ButtonStyle.Primary)
            .WithButton("Бумага", $"rps:init:{Context.User.Id}:{targetId}:{bet}:paper", ButtonStyle.Primary)
            .Build();

        await RespondAsync(embed: embeds.Info("Камень • Ножницы • Бумага", desc), components: comp);
    }

    [ComponentInteraction("rps:init:*:*:*:*", true)]
    public async Task HandleRpsInitMoveAsync(string p1Str, string p2Str, string betStr, string p1Move)
    {
        if (!ulong.TryParse(p1Str, out var p1Id) ||
            !ulong.TryParse(p2Str, out var p2Id) ||
            !long.TryParse(betStr, out var bet))
            return;

        if (Context.User.Id != p1Id)
        {
            await RespondAsync(embed: embeds.Error("Этот выбор предназначен для инициатора игры."), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

        if (p2Id == 0)
        {
            var moves = new[] { "rock", "scissors", "paper" };
            var botMove = moves[Random.Shared.Next(moves.Length)];
            var result = EvaluateWinner(p1Move, botMove);

            if (bet > 0)
            {
                if (result == 1)
                    await userRepo.TryModifyBalanceAsync(Context.Guild.Id, p1Id, bet, "rps_win", "Победа в КНБ против бота");
                else if (result == -1)
                    await userRepo.TryModifyBalanceAsync(Context.Guild.Id, p1Id, -bet, "rps_loss", "Проигрыш в КНБ против бота");
            }

            var statusText = result switch
            {
                1 => $"Победа <@{p1Id}>!" + (bet > 0 ? $" (+{bet:N0} {cfg.CurrencySymbol})" : string.Empty),
                -1 => "Победа бота KRLZ!" + (bet > 0 ? $" (-{bet:N0} {cfg.CurrencySymbol})" : string.Empty),
                _ => "Ничья! Ставка сохранена."
            };

            await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
            {
                m.Embed = embeds.Info("Результат КНБ",
                    $"**Ваш ход:** {FormatMove(p1Move)}\n**Ход бота:** {FormatMove(botMove)}\n\n**Итог:** {statusText}");
                m.Components = null;
            });
            return;
        }

        var challengeComp = new ComponentBuilder()
            .WithButton("Камень", $"rps:reply:{p1Id}:{p2Id}:{bet}:{p1Move}:rock", ButtonStyle.Success)
            .WithButton("Ножницы", $"rps:reply:{p1Id}:{p2Id}:{bet}:{p1Move}:scissors", ButtonStyle.Success)
            .WithButton("Бумага", $"rps:reply:{p1Id}:{p2Id}:{bet}:{p1Move}:paper", ButtonStyle.Success)
            .Build();

        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
        {
            m.Embed = embeds.Info("Дуэль КНБ — Ожидание соперника",
                $"<@{p1Id}> сделал свой ход втайне!\nОчередь за <@{p2Id}> (ставка: **{bet:N0} {cfg.CurrencySymbol}**).");
            m.Components = challengeComp;
        });
    }

    [ComponentInteraction("rps:reply:*:*:*:*:*", true)]
    public async Task HandleRpsReplyMoveAsync(string p1Str, string p2Str, string betStr, string p1Move, string p2Move)
    {
        if (!ulong.TryParse(p1Str, out var p1Id) ||
            !ulong.TryParse(p2Str, out var p2Id) ||
            !long.TryParse(betStr, out var bet))
            return;

        if (Context.User.Id != p2Id)
        {
            await RespondAsync(embed: embeds.Error("Сейчас ход второго участника дуэли."), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var winner = EvaluateWinner(p1Move, p2Move);

        if (bet > 0 && winner != 0)
        {
            var loserId = winner == 1 ? p2Id : p1Id;
            var winnerId = winner == 1 ? p1Id : p2Id;
            var paid = await userRepo.TryTransferAsync(Context.Guild.Id, loserId, winnerId, bet, "Дуэль КНБ");
            if (!paid)
            {
                await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
                {
                    m.Embed = embeds.Error("У одного из участников недостаточно средств для расчёта по ставке.");
                    m.Components = null;
                });
                return;
            }
        }

        var outcome = winner switch
        {
            1 => $"Победил <@{p1Id}>!",
            -1 => $"Победил <@{p2Id}>!",
            _ => "Боевая ничья!"
        };

        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
        {
            m.Embed = embeds.Success("Дуэль КНБ завершена",
                $"<@{p1Id}>: **{FormatMove(p1Move)}**\n<@{p2Id}>: **{FormatMove(p2Move)}**\n\n**Итог:** {outcome}");
            m.Components = null;
        });
    }

    private static int EvaluateWinner(string a, string b)
    {
        if (a == b) return 0;
        return (a, b) switch
        {
            ("rock", "scissors") => 1,
            ("scissors", "paper") => 1,
            ("paper", "rock") => 1,
            _ => -1
        };
    }

    private static string FormatMove(string m) => m switch
    {
        "rock" => "Камень",
        "scissors" => "Ножницы",
        _ => "Бумага"
    };
}
