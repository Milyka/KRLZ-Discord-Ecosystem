using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Bot.Data;
using Bot.Infrastructure;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Bot.Services;

public sealed class VerificationAndAutomodService
{
    private static readonly SKTypeface FontBold = LoadTypeface("Inter-Bold.ttf", SKFontStyle.Bold);

    private static readonly IReadOnlyDictionary<char, char> HomoglyphMap = new Dictionary<char, char>
    {
        ['a'] = 'а', ['e'] = 'е', ['o'] = 'о', ['p'] = 'р', ['c'] = 'с',
        ['y'] = 'у', ['x'] = 'х', ['k'] = 'к', ['m'] = 'м', ['t'] = 'т',
        ['b'] = 'в', ['0'] = 'о', ['3'] = 'з', ['4'] = 'ч', ['6'] = 'б',
        ['@'] = 'а', ['$'] = 's', ['1'] = 'i'
    };

    private const int TargetPoolSize = 3;
    private readonly ConcurrentQueue<(string Code, byte[] PngBytes)> _preGeneratedPool = new();
    private readonly SemaphoreSlim _replenishLock = new(1, 1);
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _captchaCooldowns = new();

    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), (string Code, int Attempts)> _captchas = new();
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), int> _automodStrikes = new();

    private readonly GuildSettingsRepository settingsRepo;
    private readonly ModerationService moderationService;
    private readonly SettingsProvider settingsProvider;
    private readonly EmbedFactory embeds;
    private readonly ILogger<VerificationAndAutomodService> logger;

    public VerificationAndAutomodService(
        GuildSettingsRepository settingsRepo,
        ModerationService moderationService,
        SettingsProvider settingsProvider,
        EmbedFactory embeds,
        ILogger<VerificationAndAutomodService> logger)
    {
        this.settingsRepo = settingsRepo;
        this.moderationService = moderationService;
        this.settingsProvider = settingsProvider;
        this.embeds = embeds;
        this.logger = logger;

        _ = Task.Run(WarmupPoolAsync);
    }

    private async Task WarmupPoolAsync()
    {
        try
        {
            await ReplenishPoolAsync();
            logger.LogInformation("Пул готовых капч успешно инициализирован ({Count} шт.)", _preGeneratedPool.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при первоначальной генерации пула капч");
        }
    }

    private async Task ReplenishPoolAsync()
    {
        if (!await _replenishLock.WaitAsync(0))
            return;

        try
        {
            while (_preGeneratedPool.Count < TargetPoolSize)
            {
                var item = await Task.Run(RenderSingleCaptcha);
                _preGeneratedPool.Enqueue(item);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при пополнении пула капч");
        }
        finally
        {
            _replenishLock.Release();
        }
    }

    public bool CheckAndApplyCaptchaCooldown(ulong userId, out int remainingSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        if (_captchaCooldowns.TryGetValue(userId, out var lastClick))
        {
            var diff = now - lastClick;
            if (diff < TimeSpan.FromSeconds(5))
            {
                remainingSeconds = Math.Max(1, (int)Math.Ceiling((TimeSpan.FromSeconds(5) - diff).TotalSeconds));
                return false;
            }
        }

        _captchaCooldowns[userId] = now;
        remainingSeconds = 0;
        return true;
    }

    public bool HasActiveCaptcha(ulong guildId, ulong userId) => _captchas.ContainsKey((guildId, userId));

    public bool TryGetCaptcha(ulong guildId, ulong userId, out (string Code, int Attempts) state) => _captchas.TryGetValue((guildId, userId), out state);

    private static SKTypeface LoadTypeface(string fileName, SKFontStyle style)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", fileName);
        return File.Exists(path) ? SKTypeface.FromFile(path) : SKTypeface.FromFamilyName(null, style);
    }

    private static (string Code, byte[] PngBytes) RenderSingleCaptcha()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[6];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[CasinoService.NextCryptoInt(0, alphabet.Length)];

        var code = new string(chars);

        const int width = 360;
        const int height = 120;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(18, 21, 34));

        using var noiseLinePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true
        };

        for (var i = 0; i < 14; i++)
        {
            noiseLinePaint.Color = new SKColor(
                (byte)CasinoService.NextCryptoInt(70, 220),
                (byte)CasinoService.NextCryptoInt(70, 220),
                (byte)CasinoService.NextCryptoInt(120, 255),
                90);

            canvas.DrawLine(
                CasinoService.NextCryptoInt(0, width),
                CasinoService.NextCryptoInt(0, height),
                CasinoService.NextCryptoInt(0, width),
                CasinoService.NextCryptoInt(0, height),
                noiseLinePaint);
        }

        using var dotPaint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 120; i++)
        {
            dotPaint.Color = new SKColor(255, 255, 255, (byte)CasinoService.NextCryptoInt(25, 95));
            canvas.DrawCircle(
                CasinoService.NextCryptoInt(0, width),
                CasinoService.NextCryptoInt(0, height),
                CasinoService.NextCryptoInt(1, 3),
                dotPaint);
        }

        using var charPaint = new SKPaint
        {
            Typeface = FontBold,
            TextSize = 44f,
            IsAntialias = true
        };

        for (var i = 0; i < code.Length; i++)
        {
            var cx = 42f + i * 50f;
            var cy = 76f + CasinoService.NextCryptoInt(-10, 11);
            var angle = CasinoService.NextCryptoInt(-18, 19);

            charPaint.Color = new SKColor(
                (byte)CasinoService.NextCryptoInt(150, 255),
                (byte)CasinoService.NextCryptoInt(160, 255),
                (byte)CasinoService.NextCryptoInt(210, 255));

            canvas.Save();
            canvas.RotateDegrees(angle, cx, cy);
            canvas.DrawText(code[i].ToString(), cx, cy, charPaint);
            canvas.Restore();
        }

        using var img = surface.Snapshot();
        using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
        return (code, encoded.ToArray());
    }

    public async Task<(string Code, byte[] PngBytes)> GenerateVisualCaptchaAsync(
        ulong guildId,
        ulong userId,
        CancellationToken ct = default)
    {
        (string Code, byte[] PngBytes) result;

        if (_preGeneratedPool.TryDequeue(out var cached))
        {
            result = cached;
            _ = Task.Run(ReplenishPoolAsync, CancellationToken.None);
        }
        else
        {
            result = await Task.Run(RenderSingleCaptcha, ct);
            _ = Task.Run(ReplenishPoolAsync, CancellationToken.None);
        }

        var prevAttempts = _captchas.TryGetValue((guildId, userId), out var existing) ? existing.Attempts : 0;
        _captchas[(guildId, userId)] = (result.Code, prevAttempts);

        return result;
    }

    public static string NormalizeCaptchaCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var cleaned = Regex.Replace(input, @"<@!?[0-9]+>|<#[0-9]+>|<@&[0-9]+>", string.Empty);
        if (string.IsNullOrWhiteSpace(cleaned))
            return string.Empty;

        var sb = new StringBuilder(cleaned.Length);
        foreach (var ch in cleaned)
        {
            if (char.IsWhiteSpace(ch) || ch == '*' || ch == '`' || ch == '_' || ch == '~' ||
                ch == '.' || ch == ',' || ch == '!' || ch == '?' || ch == ':' || ch == ';' ||
                ch == '-' || ch == '/' || ch == '\\' || ch == '|' || ch == '(' || ch == ')' ||
                ch == '[' || ch == ']' || ch == '{' || ch == '}' || ch == '"' || ch == '\'' || ch == '@')
                continue;

            // Map Cyrillic homoglyphs to Latin uppercase
            var mapped = ch switch
            {
                'а' or 'А' => 'A',
                'в' or 'В' => 'B',
                'с' or 'С' => 'C',
                'е' or 'Е' or 'ё' or 'Ё' => 'E',
                'н' or 'Н' => 'H',
                'к' or 'К' => 'K',
                'м' or 'М' => 'M',
                'о' or 'О' => 'O',
                'р' or 'Р' => 'P',
                'т' or 'Т' => 'T',
                'х' or 'Х' => 'X',
                'у' or 'У' => 'Y',
                'з' or 'З' => '3',
                _ => char.ToUpperInvariant(ch)
            };

            sb.Append(mapped);
        }

        return sb.ToString();
    }

    public async Task<(bool Passed, string Message)> VerifyCaptchaInputAsync(
        SocketGuild guild,
        SocketGuildUser member,
        string userInput,
        CancellationToken ct = default)
    {
        var key = (guild.Id, member.Id);
        if (!_captchas.TryGetValue(key, out var state))
            return (false, "Сессия капчи не найдена. Нажмите кнопку верификации заново.");

        var normExpected = NormalizeCaptchaCode(state.Code);
        var normUser = NormalizeCaptchaCode(userInput);

        logger.LogInformation(
            "Проверка капчи для {UserId} ({UserName}): ожидалось '{Expected}' (норм: '{NormExpected}'), получено '{User}' (норм: '{NormUser}')",
            member.Id, member.Username, state.Code, normExpected, userInput, normUser);

        if (string.IsNullOrWhiteSpace(normUser))
        {
            return (false, "Не удалось распознать защитный код. Пожалуйста, напишите символы с картинки в чат или нажмите кнопку «Ввести через окно».");
        }

        var isMatch = string.Equals(normExpected, normUser, StringComparison.OrdinalIgnoreCase) ||
                      normUser.Contains(normExpected, StringComparison.OrdinalIgnoreCase);

        if (isMatch)
        {
            _captchas.TryRemove(key, out _);
            _captchaCooldowns.TryRemove(member.Id, out _);
            var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);

            if (cfg.QuarantineRoleId != 0 && guild.GetRole(cfg.QuarantineRoleId) is { } qRole && member.Roles.Any(r => r.Id == qRole.Id))
                await member.RemoveRoleAsync(qRole);

            if (cfg.VerifiedRoleId != 0 && guild.GetRole(cfg.VerifiedRoleId) is { } vRole && guild.CurrentUser.Hierarchy > vRole.Position)
                await member.AddRoleAsync(vRole);

            return (true, "Капча успешно пройдена! Доступ к каналам сервера открыт.");
        }

        var verCfg = settingsProvider.Current.Verification;
        var nextAttempts = state.Attempts + 1;
        _captchas[key] = (state.Code, nextAttempts);

        if (nextAttempts >= Math.Max(1, verCfg.MaxAttempts))
        {
            _captchas.TryRemove(key, out _);
            _captchaCooldowns.TryRemove(member.Id, out _);
            if (verCfg.KickOnRepeatedFail && HierarchyGuard.CanModerate(guild, guild.CurrentUser, member, out _))
            {
                await member.KickAsync("Превышен лимит попыток прохождения графической капчи");
                return (false, "Превышен лимит попыток. Вы были исключены с сервера.");
            }

            if (HierarchyGuard.CanModerate(guild, guild.CurrentUser, member, out _))
            {
                var timeoutSpan = TimeSpan.FromMinutes(Math.Max(1, verCfg.TimeoutMinutesOnFail));
                await member.SetTimeOutAsync(timeoutSpan);
                return (false, $"Превышен лимит попыток ({verCfg.MaxAttempts}). Выдан таймаут на {verCfg.TimeoutMinutesOnFail} мин.");
            }
        }

        var left = Math.Max(0, verCfg.MaxAttempts - nextAttempts);
        return (false, $"Неверный код с картинки. Осталось попыток: **{left}**.");
    }

    public async Task<bool> InspectAutomodAsync(SocketUserMessage message, CancellationToken ct = default)
    {
        if (message.Author.IsBot || message.Author is not SocketGuildUser member || message.Channel is not SocketTextChannel channel)
            return false;

        var amCfg = settingsProvider.Current.Automod;
        if (!amCfg.Enabled)
            return false;

        var guild = member.Guild;
        if (member.Id == guild.OwnerId || member.GuildPermissions.Administrator || member.GuildPermissions.ManageMessages)
            return false;

        var dbCfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
        if ((dbCfg.AdminRoleId != 0 && member.Roles.Any(r => r.Id == dbCfg.AdminRoleId)) ||
            (dbCfg.ModeratorRoleId != 0 && member.Roles.Any(r => r.Id == dbCfg.ModeratorRoleId)))
            return false;

        var raw = message.Content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var normalized = NormalizeObfuscatedText(raw);
        string? matchedRule = null;
        var toxicityScore = 0;

        foreach (var word in amCfg.BlockedWords)
        {
            if (string.IsNullOrWhiteSpace(word))
                continue;

            var normWord = NormalizeObfuscatedText(word);
            if (normalized.Contains(normWord, StringComparison.OrdinalIgnoreCase))
            {
                matchedRule = $"Запрещённое слово: `{word}`";
                toxicityScore = Math.Max(toxicityScore, amCfg.ToxicityMuteThreshold);
                break;
            }
        }

        if (matchedRule is null)
        {
            foreach (var pattern in amCfg.BlockedRegexPatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;

                try
                {
                    if (Regex.IsMatch(raw, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)) ||
                        Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                    {
                        matchedRule = $"Совпадение с фильтром Regex";
                        toxicityScore = Math.Max(toxicityScore, amCfg.ToxicityWarnThreshold);
                        break;
                    }
                }
                catch
                {
                }
            }
        }

        foreach (var kvp in amCfg.ToxicityLexicon)
        {
            var normLex = NormalizeObfuscatedText(kvp.Key);
            if (!string.IsNullOrWhiteSpace(normLex) && normalized.Contains(normLex, StringComparison.OrdinalIgnoreCase))
            {
                toxicityScore += kvp.Value;
                matchedRule ??= $"Превышен порог токсичности ({toxicityScore} ед.)";
            }
        }

        if (matchedRule is null && toxicityScore < amCfg.ToxicityWarnThreshold)
            return false;

        try
        {
            await message.DeleteAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось удалить сообщение автомодерацией в канале {ChannelId}", channel.Id);
        }

        var strike = _automodStrikes.AddOrUpdate((guild.Id, member.Id), 1, (_, old) => old + 1);

        try
        {
            var dmWarn = embeds.Warning("Автомодерация KRLZ",
                $"Ваше сообщение в канале <#{channel.Id}> было удалено.\n**Причина:** {matchedRule}\n**Индекс токсичности:** {toxicityScore}");
            await member.SendMessageAsync(embed: dmWarn);
        }
        catch
        {
        }

        if (toxicityScore >= amCfg.ToxicityMuteThreshold || strike >= 3)
        {
            if (HierarchyGuard.CanModerate(guild, guild.CurrentUser, member, out _))
            {
                var muteSpan = TimeSpan.FromMinutes(Math.Max(5, amCfg.MuteDurationMinutes));
                await moderationService.MuteAsync(guild, guild.CurrentUser, member, muteSpan, $"[Автомод] {matchedRule}", ct);
            }
        }
        else if (toxicityScore >= amCfg.ToxicityWarnThreshold || strike == 2)
        {
            if (HierarchyGuard.CanModerate(guild, guild.CurrentUser, member, out _))
            {
                await moderationService.WarnAsync(guild, guild.CurrentUser, member, $"[Автомод] {matchedRule}", ct);
            }
        }

        return true;
    }

    public static string NormalizeObfuscatedText(string input)
    {
        var sb = new StringBuilder(input.Length);
        char? prev = null;

        foreach (var rawCh in input.ToLowerInvariant())
        {
            if (!char.IsLetterOrDigit(rawCh) && rawCh is not ('@' or '$'))
                continue;

            var mapped = HomoglyphMap.TryGetValue(rawCh, out var m) ? m : rawCh;
            if (prev == mapped)
                continue;

            sb.Append(mapped);
            prev = mapped;
        }

        return sb.ToString();
    }
}
