using System.Text;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class SocialExpansionModule(
    SocialExpansionRepository socialExpRepo,
    SocialRepository socialRepo,
    UserRepository userRepo,
    GuildSettingsRepository settingsRepo,
    ProfileCardService profileCardService,
    SettingsProvider settingsProvider,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    [Group("rep", "Система репутации и отзывов участников сервера")]
    public sealed class ReputationSubgroup(
        SocialExpansionRepository socialExpRepo,
        UserRepository userRepo,
        SettingsProvider settingsProvider,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("give", "Повысить (+rep) или понизить (-rep) репутацию участнику сервера")]
        public async Task GiveRepCommandAsync(
            [Summary("user", "Участник сервера")] SocketGuildUser target,
            [Summary("type", "Тип оценки"),
             Choice("Положительная (+1)", 1),
             Choice("Отрицательная (-1)", -1)] int delta = 1,
            [Summary("comment", "Короткий комментарий (за что выдан отзыв)")] string comment = "Спасибо за помощь и общение!")
        {
            if (target.IsBot || target.Id == Context.User.Id)
            {
                await RespondAsync(embed: embeds.Error("Выберите другого реального участника сервера."), ephemeral: true);
                return;
            }

            await DeferAsync();
            var giverProfile = await userRepo.GetOrCreateAsync(Context.Guild.Id, Context.User.Id);
            var minLvl = settingsProvider.Current.Social.MinLevelForReputation;
            if (giverProfile.Level < minLvl)
            {
                await FollowupAsync(embed: embeds.Error($"Выдавать репутацию можно начиная с **{minLvl} уровня** (ваш уровень: {giverProfile.Level})."), ephemeral: true);
                return;
            }

            var cleanComment = comment.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cleanComment.Length > 100)
                cleanComment = cleanComment[..100];

            var (ok, msg, newScore) = await socialExpRepo.TryGiveReputationAsync(
                Context.Guild.Id,
                Context.User.Id,
                target.Id,
                delta,
                cleanComment,
                settingsProvider.Current.Social.ReputationCooldownHours);

            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
                return;
            }

            var sign = delta > 0 ? "+1" : "-1";
            var scoreText = newScore > 0 ? $"+{newScore}" : newScore.ToString();
            var embed = embeds.Success("Репутация обновлена",
                $"<@{Context.User.Id}> изменил репутацию <@{target.Id}> на **{sign}**!\n" +
                $"**Текущая репутация:** `{scoreText}`\n" +
                $"**Комментарий:** `{cleanComment}`");

            await FollowupAsync(embed: embed);
        }

        [SlashCommand("info", "Посмотреть репутацию и историю отзывов об участнике")]
        public async Task RepInfoCommandAsync([Summary("user", "Участник сервера")] SocketGuildUser? target = null)
        {
            await DeferAsync();
            var member = target ?? (SocketGuildUser)Context.User;
            var rep = await socialExpRepo.GetOrCreateReputationAsync(Context.Guild.Id, member.Id);
            var rank = await userRepo.GetUserRankByReputationAsync(Context.Guild.Id, member.Id);
            var logs = await socialExpRepo.GetReputationLogsAsync(Context.Guild.Id, member.Id, 10);

            var sb = new StringBuilder();
            var scoreText = rep.Reputation > 0 ? $"+{rep.Reputation}" : rep.Reputation.ToString();
            sb.AppendLine($"**Рейтинг репутации:** `{scoreText}` (Место в топе: **#{rank}**)");
            sb.AppendLine($"**Отзывы:** 👍 `{rep.PositiveCount}`  |  👎 `{rep.NegativeCount}`");
            sb.AppendLine();
            sb.AppendLine("**Последние отзывы:**");

            if (logs.Count == 0)
            {
                sb.AppendLine("*Отзывов пока нет.*");
            }
            else
            {
                foreach (var l in logs)
                {
                    var badge = l.Delta > 0 ? "🟢 `+1`" : "🔴 `-1`";
                    sb.AppendLine($"{badge} от <@{l.FromUserId}> ({TimestampTag.FromDateTimeOffset(l.CreatedAt, TimestampTagStyles.ShortDate)}): `{l.Comment}`");
                }
            }

            await FollowupAsync(embed: embeds.Info($"Репутация — {member.DisplayName}", sb.ToString()));
        }
    }

    [Group("family", "Семейный банк, уровень отношений и усыновление участников")]
    public sealed class FamilySubgroup(
        SocialRepository socialRepo,
        SocialExpansionRepository socialExpRepo,
        UserRepository userRepo,
        GuildSettingsRepository settingsRepo,
        ProfileCardService profileCardService,
        SettingsProvider settingsProvider,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("info", "Открыть графическую карточку семьи, семейного банка и детей")]
        public async Task FamilyInfoCommandAsync([Summary("user", "Участник сервера")] SocketGuildUser? target = null)
        {
            await DeferAsync();
            var member = target ?? (SocketGuildUser)Context.User;
            var marriage = await socialRepo.GetMarriageAsync(Context.Guild.Id, member.Id);

            if (marriage is null)
            {
                var adopted = await socialExpRepo.GetAdoptedRecordAsync(Context.Guild.Id, member.Id);
                if (adopted is not null)
                {
                    marriage = await socialRepo.GetMarriageByIdAsync(adopted.MarriageId);
                }
            }

            if (marriage is null)
            {
                await FollowupAsync(embed: embeds.Error($"Участник <@{member.Id}> пока не состоит в браке или семье. Предложить союз можно командой `/marry`."), ephemeral: true);
                return;
            }

            var family = await socialExpRepo.GetOrCreateFamilyProfileAsync(marriage.Id, Context.Guild.Id);
            var kids = await socialExpRepo.GetFamilyMembersAsync(marriage.Id);
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

            var u1 = await Context.Guild.GetGuildUserSafeAsync(marriage.User1Id);
            var u2 = await Context.Guild.GetGuildUserSafeAsync(marriage.User2Id);

            var p1Name = u1?.DisplayName ?? $"ID {marriage.User1Id}";
            var p1Av = u1?.GetAvatarUrl(ImageFormat.Png, 128) ?? u1?.GetDefaultAvatarUrl();
            var p2Name = u2?.DisplayName ?? $"ID {marriage.User2Id}";
            var p2Av = u2?.GetAvatarUrl(ImageFormat.Png, 128) ?? u2?.GetDefaultAvatarUrl();

            var kidNames = new List<string>();
            foreach (var k in kids)
            {
                var ku = await Context.Guild.GetGuildUserSafeAsync(k.ChildUserId);
                kidNames.Add(ku?.DisplayName ?? $"ID {k.ChildUserId}");
            }

            var png = await profileCardService.RenderFamilyCardAsync(
                marriage,
                family,
                p1Name,
                p1Av,
                p2Name,
                p2Av,
                kidNames,
                cfg.CurrencySymbol);

            await using var stream = new MemoryStream(png);
            await FollowupWithFileAsync(stream, $"family-{marriage.Id}.png");
        }

        [SlashCommand("rename", "Изменить название вашей семьи на карточке /family")]
        public async Task FamilyRenameCommandAsync([Summary("name", "Новое название семьи (до 36 символов)")] string name)
        {
            await DeferAsync();
            var marriage = await socialRepo.GetMarriageAsync(Context.Guild.Id, Context.User.Id);
            if (marriage is null)
            {
                await FollowupAsync(embed: embeds.Error("Вы не состоите в браке."), ephemeral: true);
                return;
            }

            var clean = name.Replace("\r", " ").Replace("\n", " ").Trim();
            if (clean.Length is < 2 or > 36)
            {
                await FollowupAsync(embed: embeds.Error("Название семьи должно быть от 2 до 36 символов."), ephemeral: true);
                return;
            }

            await socialExpRepo.GetOrCreateFamilyProfileAsync(marriage.Id, Context.Guild.Id);
            await socialExpRepo.UpdateFamilyNameAsync(marriage.Id, clean);
            await FollowupAsync(embed: embeds.Success("Название семьи изменено", $"Ваша семья теперь называется **{clean}**!"));
        }

        [SlashCommand("deposit", "Пополнить общий семейный банк")]
        public async Task FamilyDepositCommandAsync([Summary("amount", "Сумма пополнения")] [MinValue(1)] long amount)
        {
            await DeferAsync();
            var marriage = await socialRepo.GetMarriageAsync(Context.Guild.Id, Context.User.Id);
            if (marriage is null)
            {
                await FollowupAsync(embed: embeds.Error("Вы не состоите в браке."), ephemeral: true);
                return;
            }

            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var ok = await socialExpRepo.TryDepositFamilyAsync(marriage.Id, Context.Guild.Id, Context.User.Id, amount);
            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error("Недостаточно средств на личном балансе."), ephemeral: true);
                return;
            }

            var family = await socialExpRepo.GetOrCreateFamilyProfileAsync(marriage.Id, Context.Guild.Id);
            await FollowupAsync(embed: embeds.Success("Семейный банк пополнен",
                $"Вы внесли **{amount:N0} {cfg.CurrencySymbol}** в семейный бюджет.\n**Баланс семьи:** `{family.FamilyBalance:N0} {cfg.CurrencySymbol}`."));
        }

        [SlashCommand("withdraw", "Снять средства из общего семейного банка")]
        public async Task FamilyWithdrawCommandAsync([Summary("amount", "Сумма снятия")] [MinValue(1)] long amount)
        {
            await DeferAsync();
            var marriage = await socialRepo.GetMarriageAsync(Context.Guild.Id, Context.User.Id);
            if (marriage is null)
            {
                await FollowupAsync(embed: embeds.Error("Вы не состоите в браке."), ephemeral: true);
                return;
            }

            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var ok = await socialExpRepo.TryWithdrawFamilyAsync(marriage.Id, Context.Guild.Id, Context.User.Id, amount);
            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error("В семейном банке недостаточно средств."), ephemeral: true);
                return;
            }

            var family = await socialExpRepo.GetOrCreateFamilyProfileAsync(marriage.Id, Context.Guild.Id);
            await FollowupAsync(embed: embeds.Success("Средства сняты",
                $"Вы сняли **{amount:N0} {cfg.CurrencySymbol}** из семейного банка.\n**Остаток в банке семьи:** `{family.FamilyBalance:N0} {cfg.CurrencySymbol}`."));
        }

        [SlashCommand("adopt", "Предложить участнику сервера стать ребёнком в вашей семье")]
        public async Task FamilyAdoptCommandAsync([Summary("child", "Участник для усыновления")] SocketGuildUser child)
        {
            if (child.IsBot || child.Id == Context.User.Id)
            {
                await RespondAsync(embed: embeds.Error("Укажите другого реального участника сервера."), ephemeral: true);
                return;
            }

            await DeferAsync();
            var marriage = await socialRepo.GetMarriageAsync(Context.Guild.Id, Context.User.Id);
            if (marriage is null)
            {
                await FollowupAsync(embed: embeds.Error("Усыновлять участников могут только семейные пары (/marry)."), ephemeral: true);
                return;
            }

            if (child.Id == marriage.User1Id || child.Id == marriage.User2Id)
            {
                await FollowupAsync(embed: embeds.Error("Нельзя усыновить собственного супруга."), ephemeral: true);
                return;
            }

            var fee = settingsProvider.Current.Social.AdoptionFee;
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

            var comp = new ComponentBuilder()
                .WithButton("Согласиться", $"family:adopt_ok:{marriage.Id}:{Context.User.Id}:{child.Id}", ButtonStyle.Success)
                .WithButton("Отказаться", $"family:adopt_no:{marriage.Id}:{Context.User.Id}:{child.Id}", ButtonStyle.Danger)
                .Build();

            await FollowupAsync(
                text: $"<@{child.Id}>",
                embed: embeds.Info("Предложение усыновления",
                    $"Семейная пара <@{marriage.User1Id}> и <@{marriage.User2Id}> приглашает <@{child.Id}> стать частью их семьи!\n" +
                    $"Пошлина оформления: **{fee:N0} {cfg.CurrencySymbol}**."),
                components: comp);
        }

        [SlashCommand("leave", "Покинуть приёмную семью или исключить ребёнка из семьи")]
        public async Task FamilyLeaveCommandAsync([Summary("child", "Ребёнок (если исключает родитель)")] SocketGuildUser? child = null)
        {
            await DeferAsync();
            var targetId = child?.Id ?? Context.User.Id;
            if (child is not null && child.Id != Context.User.Id)
            {
                var marriage = await socialRepo.GetMarriageAsync(Context.Guild.Id, Context.User.Id);
                if (marriage is null)
                {
                    await FollowupAsync(embed: embeds.Error("Вы не состоите в браке."), ephemeral: true);
                    return;
                }
            }

            var removed = await socialExpRepo.RemoveFamilyMemberAsync(Context.Guild.Id, targetId);
            if (!removed)
            {
                await FollowupAsync(embed: embeds.Error("Указанный участник не числится приёмным ребёнком в семье."), ephemeral: true);
                return;
            }

            await FollowupAsync(embed: embeds.Warning("Состав семьи изменён", $"Участник <@{targetId}> больше не числится в составе приёмной семьи."));
        }
    }

    [ComponentInteraction("family:adopt_*:*:*:*", true)]
    public async Task HandleFamilyAdoptDecisionAsync(string decision, string marriageIdStr, string parentIdStr, string childIdStr)
    {
        if (!long.TryParse(marriageIdStr, out var marriageId) ||
            !ulong.TryParse(parentIdStr, out var parentId) ||
            !ulong.TryParse(childIdStr, out var childId))
            return;

        if (Context.User.Id != childId)
        {
            await RespondAsync(embed: embeds.Error("Ответить на это предложение может только приглашённый участник."), ephemeral: true);
            return;
        }

        await DeferAsync();
        if (decision == "no")
        {
            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embeds.Warning("Отказ от усыновления", $"<@{childId}> отклонил(а) приглашение в семью.");
                m.Components = null;
            });
            return;
        }

        var fee = settingsProvider.Current.Social.AdoptionFee;
        if (fee > 0)
        {
            var paid = await userRepo.TryModifyBalanceAsync(Context.Guild.Id, parentId, -fee, "family_adopt", $"Пошлина усыновления {childId}");
            if (!paid)
            {
                await ModifyOriginalResponseAsync(m =>
                {
                    m.Embed = embeds.Error("У родителя недостаточно средств для оплаты пошлины усыновления.");
                    m.Components = null;
                });
                return;
            }
        }

        var (ok, msg) = await socialExpRepo.TryAdoptChildAsync(
            marriageId,
            Context.Guild.Id,
            childId,
            settingsProvider.Current.Social.MaxAdoptedChildren);

        if (!ok)
        {
            if (fee > 0)
                await userRepo.TryModifyBalanceAsync(Context.Guild.Id, parentId, fee, "family_adopt_refund", "Возврат пошлины усыновления");

            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embeds.Error(msg);
                m.Components = null;
            });
            return;
        }

        await socialExpRepo.AddFamilyLoveXpAsync(marriageId, Context.Guild.Id, 100);
        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embeds.Success("Пополнение в семье!", $"🎉 <@{childId}> официально принят(а) в семью! (+100 очков семейной гармонии)");
            m.Components = null;
        });
    }

    [Group("bank", "Банк KRLZ: защищённые вклады под процент и обмен кристаллов")]
    public sealed class BankSubgroup(
        SocialExpansionRepository socialExpRepo,
        UserRepository userRepo,
        GuildSettingsRepository settingsRepo,
        SettingsProvider settingsProvider,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("info", "Открыть состояние вашего банковского счёта, вклада и кристаллов")]
        public async Task BankInfoCommandAsync()
        {
            await DeferAsync();
            var profile = await userRepo.GetOrCreateAsync(Context.Guild.Id, Context.User.Id);
            var bank = await socialExpRepo.GetOrCreateBankAccountAsync(Context.Guild.Id, Context.User.Id);
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var bCfg = settingsProvider.Current.Bank;

            var estDaily = Math.Min(bCfg.MaxInterestCapPerDay, (long)Math.Floor(bank.DepositBalance * (bCfg.DailyInterestPercent / 100.0)));
            var nextInterestAt = bank.LastInterestAt.AddHours(24);

            var embed = embeds.CreateBase($"Банк KRLZ — Счёт {Context.User.Username}")
                .AddField("На руках", $"**{profile.Balance:N0} {cfg.CurrencySymbol}**", true)
                .AddField("Депозитный вклад", $"**{bank.DepositBalance:N0} {cfg.CurrencySymbol}**", true)
                .AddField("Кристаллы", $"**{bank.Crystals:N0} 💎**", true)
                .AddField("Ставка по вкладу", $"`{bCfg.DailyInterestPercent:F1}%` в сутки (до `{bCfg.MaxInterestCapPerDay:N0} {cfg.CurrencySymbol}`)", true)
                .AddField("Ожидаемый доход", $"`+{estDaily:N0} {cfg.CurrencySymbol}` ({TimestampTag.FromDateTimeOffset(nextInterestAt, TimestampTagStyles.Relative)})", true)
                .AddField("Всего начислено %", $"`{bank.TotalInterestEarned:N0} {cfg.CurrencySymbol}`", true)
                .AddField("Курс биржи кристаллов 💎",
                    $"Покупка: **1 💎 = {bCfg.CrystalBuyRate:N0} {cfg.CurrencySymbol}** | Продажа: **1 💎 = {bCfg.CrystalSellRate:N0} {cfg.CurrencySymbol}**",
                    false)
                .Build();

            await FollowupAsync(embed: embed);
        }

        [SlashCommand("deposit", "Внести средства на защищённый депозит под ежедневный процент")]
        public async Task BankDepositCommandAsync([Summary("amount", "Сумма вклада")] [MinValue(1)] long amount)
        {
            await DeferAsync();
            var bCfg = settingsProvider.Current.Bank;
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            if (amount < bCfg.MinDepositAmount)
            {
                await FollowupAsync(embed: embeds.Error($"Минимальная сумма банковского вклада составляет **{bCfg.MinDepositAmount:N0} {cfg.CurrencySymbol}**."), ephemeral: true);
                return;
            }

            var ok = await socialExpRepo.TryDepositBankAsync(Context.Guild.Id, Context.User.Id, amount);
            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error("Недостаточно средств на руках для пополнения вклада."), ephemeral: true);
                return;
            }

            var bank = await socialExpRepo.GetOrCreateBankAccountAsync(Context.Guild.Id, Context.User.Id);
            await FollowupAsync(embed: embeds.Success("Депозит пополнен",
                $"Вы разместили **{amount:N0} {cfg.CurrencySymbol}** на вкладе под `{bCfg.DailyInterestPercent:F1}%` годовых/суточных.\n" +
                $"**Сумма на депозите:** `{bank.DepositBalance:N0} {cfg.CurrencySymbol}`."));
        }

        [SlashCommand("withdraw", "Снять средства с депозитного счёта Банка KRLZ")]
        public async Task BankWithdrawCommandAsync([Summary("amount", "Сумма снятия")] [MinValue(1)] long amount)
        {
            await DeferAsync();
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var ok = await socialExpRepo.TryWithdrawBankAsync(Context.Guild.Id, Context.User.Id, amount);
            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error("На вашем депозитном счёте недостаточно средств."), ephemeral: true);
                return;
            }

            var bank = await socialExpRepo.GetOrCreateBankAccountAsync(Context.Guild.Id, Context.User.Id);
            await FollowupAsync(embed: embeds.Success("Средства выведены",
                $"Вы сняли **{amount:N0} {cfg.CurrencySymbol}** с банковского счёта.\n" +
                $"**Остаток на депозите:** `{bank.DepositBalance:N0} {cfg.CurrencySymbol}`."));
        }

        [SlashCommand("exchange", "Обменять валюту на кристаллы 💎 или продать кристаллы")]
        public async Task BankExchangeCommandAsync(
            [Summary("action", "Операция обмена"),
             Choice("Купить кристаллы 💎 за монеты", "buy"),
             Choice("Продать кристаллы 💎 за монеты", "sell")] string action,
            [Summary("crystals", "Количество кристаллов 💎")] [MinValue(1)] long crystals)
        {
            await DeferAsync();
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var bCfg = settingsProvider.Current.Bank;
            var buy = action == "buy";
            var rate = buy ? bCfg.CrystalBuyRate : bCfg.CrystalSellRate;

            var (ok, msg) = await socialExpRepo.TryExchangeCrystalsAsync(
                Context.Guild.Id,
                Context.User.Id,
                buy,
                crystals,
                rate,
                cfg.CurrencySymbol);

            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
                return;
            }

            await FollowupAsync(embed: embeds.Success("Валютная операция выполнена", msg));
        }
    }

    [Group("pet", "Питомцы, уход и экспедиции за сокровищами")]
    public sealed class PetSubgroup(
        SocialExpansionRepository socialExpRepo,
        GuildSettingsRepository settingsRepo,
        SettingsProvider settingsProvider,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("info", "Посмотреть карточку вашего питомца и статус экспедиции")]
        public async Task PetInfoCommandAsync([Summary("user", "Участник сервера")] SocketGuildUser? target = null)
        {
            await DeferAsync();
            var member = target ?? (SocketGuildUser)Context.User;
            var pet = await socialExpRepo.GetUserPetAsync(Context.Guild.Id, member.Id);
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var pCfg = settingsProvider.Current.Pets;

            if (pet is null)
            {
                await FollowupAsync(embed: embeds.Info("Питомец отсутствует",
                    $"У <@{member.Id}> ещё нет питомца. Приручить спутника можно командой `/pet adopt` (цена: **{pCfg.AdoptPrice:N0} {cfg.CurrencySymbol}**)."), ephemeral: true);
                return;
            }

            var expStatus = "Готов к походу";
            if (pet.ExpeditionEndsAt.HasValue)
            {
                expStatus = pet.ExpeditionEndsAt.Value > DateTimeOffset.UtcNow
                    ? $"В походе (вернётся {TimestampTag.FromDateTimeOffset(pet.ExpeditionEndsAt.Value, TimestampTagStyles.Relative)})"
                    : "Поход завершён! Нажмите «Забрать награду» или `/pet claim`";
            }

            var baseBonus = pet.Rarity switch
            {
                "legendary" => 15,
                "epic" => 10,
                "rare" => 6,
                _ => 3
            } + Math.Min(15, pet.Level - 1);

            var embed = embeds.CreateBase($"Питомец — {pet.Name}")
                .AddField("Владелец", $"<@{member.Id}>", true)
                .AddField("Вид / Редкость", $"`{pet.PetCode}` • **{pet.Rarity.ToUpperInvariant()}**", true)
                .AddField("Уровень и опыт", $"**Ур. {pet.Level}** (`{pet.Xp:N0} XP`)", true)
                .AddField("Сытость", $"`{pet.Satiety}%`", true)
                .AddField("Пассивный бонус", $"`+{baseBonus}%` к монетам и XP", true)
                .AddField("Экспедиция", expStatus, false)
                .Build();

            ComponentBuilder? comp = null;
            if (member.Id == Context.User.Id)
            {
                comp = new ComponentBuilder()
                    .WithButton("Покормить", $"pet:feed:{member.Id}", ButtonStyle.Primary, new Emoji("🍖"))
                    .WithButton("В экспедицию", $"pet:exp:{member.Id}", ButtonStyle.Secondary, new Emoji("🧭"))
                    .WithButton("Забрать награду", $"pet:claim:{member.Id}", ButtonStyle.Success, new Emoji("🎁"));
            }

            await FollowupAsync(embed: embed, components: comp?.Build());
        }

        [SlashCommand("adopt", "Приручить собственного питомца-спутника")]
        public async Task PetAdoptCommandAsync(
            [Summary("species", "Вид питомца"),
             Choice("Кибер-Лис (Cyber Fox)", "cyber_fox"),
             Choice("Неоновый Дракон (Neon Dragon)", "neon_dragon"),
             Choice("Кот Бездны (Void Cat)", "void_cat"),
             Choice("Морозный Волк (Frost Wolf)", "frost_wolf"),
             Choice("Золотой Феникс (Gold Phoenix)", "gold_phoenix")] string species,
            [Summary("name", "Кличка питомца")] string name)
        {
            var cleanName = name.Trim();
            if (cleanName.Length is < 2 or > 28)
            {
                await RespondAsync(embed: embeds.Error("Кличка питомца должна содержать от 2 до 28 символов."), ephemeral: true);
                return;
            }

            await DeferAsync();
            var roll = CasinoService.NextCryptoInt(1, 101);
            var rarity = roll switch
            {
                <= 8 => "legendary",
                <= 28 => "epic",
                <= 65 => "rare",
                _ => "common"
            };

            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var price = settingsProvider.Current.Pets.AdoptPrice;
            var (ok, msg, _) = await socialExpRepo.TryAdoptPetAsync(
                Context.Guild.Id,
                Context.User.Id,
                species,
                cleanName,
                rarity,
                price,
                cfg.CurrencySymbol);

            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
                return;
            }

            await FollowupAsync(embed: embeds.Success("Новый питомец приручен!", msg));
        }

        [SlashCommand("feed", "Покормить вашего питомца и повысить его опыт")]
        public async Task PetFeedCommandAsync()
        {
            await DeferAsync();
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var cost = settingsProvider.Current.Pets.FeedCost;
            var (ok, msg) = await socialExpRepo.TryFeedPetAsync(Context.Guild.Id, Context.User.Id, cost, cfg.CurrencySymbol);
            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
                return;
            }

            await FollowupAsync(embed: embeds.Success("Кормление питомца", msg));
        }

        [SlashCommand("rename", "Дать новую кличку вашему питомцу")]
        public async Task PetRenameCommandAsync([Summary("name", "Новая кличка питомца")] string name)
        {
            await DeferAsync(ephemeral: true);
            var pet = await socialExpRepo.GetUserPetAsync(Context.Guild.Id, Context.User.Id);
            if (pet is null)
            {
                await FollowupAsync(embed: embeds.Error("У вас нет питомца."), ephemeral: true);
                return;
            }

            var clean = name.Trim();
            if (clean.Length is < 2 or > 28)
            {
                await FollowupAsync(embed: embeds.Error("Кличка должна быть от 2 до 28 символов."), ephemeral: true);
                return;
            }

            await socialExpRepo.RenamePetAsync(Context.Guild.Id, Context.User.Id, clean);
            await FollowupAsync(embed: embeds.Success("Кличка изменена", $"Вашего питомца теперь зовут **{clean}**!"), ephemeral: true);
        }

        [SlashCommand("expedition", "Отправить питомца в экспедицию за монетами и опытом")]
        public async Task PetExpeditionCommandAsync()
        {
            await DeferAsync();
            var hours = settingsProvider.Current.Pets.ExpeditionHours;
            var (ok, msg) = await socialExpRepo.StartPetExpeditionAsync(Context.Guild.Id, Context.User.Id, hours);
            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
                return;
            }

            await FollowupAsync(embed: embeds.Success("Экспедиция началась", msg));
        }

        [SlashCommand("claim", "Забрать добычу питомца после возвращения из экспедиции")]
        public async Task PetClaimCommandAsync()
        {
            await DeferAsync();
            var pCfg = settingsProvider.Current.Pets;
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var (ok, msg, coins, xp) = await socialExpRepo.ClaimPetExpeditionAsync(
                Context.Guild.Id,
                Context.User.Id,
                pCfg.ExpeditionBaseCoins,
                pCfg.ExpeditionBaseXp);

            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
                return;
            }

            await FollowupAsync(embed: embeds.Success("Добыча экспедиции получена!",
                $"{msg}\n**Награда:** `+{coins:N0} {cfg.CurrencySymbol}` и `+{xp:N0} XP`!"));
        }
    }

    [ComponentInteraction("pet:*:*", true)]
    public async Task HandlePetActionButtonsAsync(string action, string ownerIdStr)
    {
        await DeferAsync(ephemeral: true);
        if (!ulong.TryParse(ownerIdStr, out var ownerId) || Context.User.Id != ownerId)
        {
            await FollowupAsync(embed: embeds.Error("Управлять питомцем может только его хозяин."), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var pCfg = settingsProvider.Current.Pets;

        switch (action)
        {
            case "feed":
            {
                var (ok, msg) = await socialExpRepo.TryFeedPetAsync(Context.Guild.Id, ownerId, pCfg.FeedCost, cfg.CurrencySymbol);
                await FollowupAsync(embed: ok ? embeds.Success("Кормление", msg) : embeds.Error(msg), ephemeral: true);
                break;
            }
            case "exp":
            {
                var (ok, msg) = await socialExpRepo.StartPetExpeditionAsync(Context.Guild.Id, ownerId, pCfg.ExpeditionHours);
                await FollowupAsync(embed: ok ? embeds.Success("Экспедиция", msg) : embeds.Error(msg), ephemeral: true);
                break;
            }
            case "claim":
            {
                var (ok, msg, coins, xp) = await socialExpRepo.ClaimPetExpeditionAsync(
                    Context.Guild.Id,
                    ownerId,
                    pCfg.ExpeditionBaseCoins,
                    pCfg.ExpeditionBaseXp);
                await FollowupAsync(embed: ok
                    ? embeds.Success("Награда получена", $"{msg}\nВы получили **{coins:N0} {cfg.CurrencySymbol}** и **{xp:N0} XP**!")
                    : embeds.Error(msg), ephemeral: true);
                break;
            }
        }
    }
}
