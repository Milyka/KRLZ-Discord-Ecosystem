using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Dapper;

var builder = WebApplication.CreateBuilder(args);

var botDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Bot"));
var settingsPath = Path.Combine(botDir, "settings.json");
if (!File.Exists(settingsPath))
{
    var altBotDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "Bot"));
    if (File.Exists(Path.Combine(altBotDir, "settings.json")))
    {
        botDir = altBotDir;
        settingsPath = Path.Combine(altBotDir, "settings.json");
    }
    else
    {
        settingsPath = "settings.json";
    }
}

var spInstance = new SettingsProvider(settingsPath);
if (spInstance.Current.DatabaseConnectionString.Contains("Data Source=krlz.db", StringComparison.OrdinalIgnoreCase))
{
    var sharedDbPath = Path.Combine(botDir, "krlz.db");
    if (Directory.Exists(botDir))
    {
        spInstance.Current.DatabaseConnectionString = $"Data Source={sharedDbPath};Cache=Shared";
    }
}

builder.Services.AddSingleton(spInstance);
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddSingleton<DbMigrator>();
builder.Services.AddSingleton<GuildSettingsRepository>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<ModerationRepository>();
builder.Services.AddSingleton<TicketRepository>();
builder.Services.AddSingleton<SecurityRepository>();
builder.Services.AddSingleton<ShopRepository>();
builder.Services.AddSingleton<ItemRepository>();
builder.Services.AddSingleton<ClanRepository>();
builder.Services.AddSingleton<AuctionRepository>();
builder.Services.AddSingleton<AchievementRepository>();
builder.Services.AddSingleton<ProgressionRepository>();
builder.Services.AddSingleton<SocialRepository>();
builder.Services.AddSingleton<SocialExpansionRepository>();
builder.Services.AddSingleton<VerificationAndLogsRepository>();
builder.Services.AddSingleton<CommunityEventsRepository>();
builder.Services.AddSingleton<PartyGameRepository>();
builder.Services.AddHttpClient();

var app = builder.Build();
var migrator = app.Services.GetRequiredService<DbMigrator>();
await migrator.MigrateAsync();

var sessions = new ConcurrentDictionary<string, (ulong UserId, string Username, DateTimeOffset ExpiresAt)>();

app.UseDefaultFiles();
app.UseStaticFiles();

bool IsAuthorized(HttpContext ctx, SettingsProvider sp)
{
    var expectedToken = string.IsNullOrWhiteSpace(sp.Current.WebPanel.AdminApiToken)
        ? "admin"
        : sp.Current.WebPanel.AdminApiToken;

    if (ctx.Request.Headers.TryGetValue("X-Admin-Token", out var headerToken) &&
        string.Equals(headerToken.ToString(), expectedToken, StringComparison.Ordinal))
    {
        return true;
    }

    if (ctx.Request.Cookies.TryGetValue("krlz_session", out var sid) &&
        sessions.TryGetValue(sid, out var sess) &&
        sess.ExpiresAt > DateTimeOffset.UtcNow)
    {
        return true;
    }

    return false;
}

app.MapGet("/auth/discord", (SettingsProvider sp) =>
{
    var wp = sp.Current.WebPanel;
    var url = $"https://discord.com/api/oauth2/authorize?client_id={Uri.EscapeDataString(wp.DiscordClientId)}&redirect_uri={Uri.EscapeDataString(wp.RedirectUri)}&response_type=code&scope=identify%20guilds.members.read";
    return Results.Redirect(url);
});

app.MapGet("/auth/callback", async (
    string? code,
    HttpContext ctx,
    SettingsProvider sp,
    GuildSettingsRepository settingsRepo,
    IHttpClientFactory httpFactory) =>
{
    if (string.IsNullOrWhiteSpace(code))
        return Results.BadRequest("Отсутствует код авторизации Discord OAuth2.");

    var wp = sp.Current.WebPanel;
    var guildId = sp.Current.GuildId;
    var client = httpFactory.CreateClient();

    var tokenResp = await client.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["client_id"] = wp.DiscordClientId,
        ["client_secret"] = wp.DiscordClientSecret,
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["redirect_uri"] = wp.RedirectUri
    }));

    if (!tokenResp.IsSuccessStatusCode)
        return Results.Unauthorized();

    using var tokenDoc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync());
    var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString();
    if (string.IsNullOrWhiteSpace(accessToken))
        return Results.Unauthorized();

    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    var userResp = await client.GetAsync("https://discord.com/api/users/@me");
    if (!userResp.IsSuccessStatusCode)
        return Results.Unauthorized();

    using var userDoc = JsonDocument.Parse(await userResp.Content.ReadAsStringAsync());
    var userIdStr = userDoc.RootElement.GetProperty("id").GetString() ?? "0";
    var username = userDoc.RootElement.GetProperty("username").GetString() ?? "Admin";
    ulong.TryParse(userIdStr, out var userId);

    var memberResp = await client.GetAsync($"https://discord.com/api/users/@me/guilds/{guildId}/member");
    if (!memberResp.IsSuccessStatusCode)
        return Results.StatusCode(403);

    var cfg = await settingsRepo.GetOrCreateAsync(guildId);
    using var memberDoc = JsonDocument.Parse(await memberResp.Content.ReadAsStringAsync());
    var hasRole = false;

    if (memberDoc.RootElement.TryGetProperty("roles", out var rolesEl))
    {
        foreach (var r in rolesEl.EnumerateArray())
        {
            if (ulong.TryParse(r.GetString(), out var rId) &&
                (rId == cfg.AdminRoleId || rId == cfg.ModeratorRoleId))
            {
                hasRole = true;
                break;
            }
        }
    }

    if (!hasRole && cfg.AdminRoleId != 0)
        return Results.StatusCode(403);

    var sessionId = Guid.NewGuid().ToString("N");
    sessions[sessionId] = (userId, username, DateTimeOffset.UtcNow.AddHours(12));
    ctx.Response.Cookies.Append("krlz_session", sessionId, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Expires = DateTimeOffset.UtcNow.AddHours(12)
    });

    return Results.Redirect("/");
});

app.MapGet("/api/overview", async (
    HttpContext ctx,
    SettingsProvider sp,
    UserRepository userRepo,
    GuildSettingsRepository settingsRepo,
    ItemRepository itemRepo,
    AchievementRepository achRepo,
    ProgressionRepository progRepo,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var guildId = sp.Current.GuildId;
    await itemRepo.EnsureDefaultItemsAsync(guildId);
    await achRepo.EnsureDefaultAchievementsAsync(guildId);
    await progRepo.EnsureDefaultQuestsAndSeasonAsync(guildId);

    var totals = await userRepo.GetGuildTotalsAsync(guildId);
    var cfg = await settingsRepo.GetOrCreateAsync(guildId);

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    var bankTotal = await conn.ExecuteScalarAsync<long>("SELECT COALESCE(SUM(deposit_balance), 0) FROM bank_accounts WHERE guild_id = @GuildId;", new { GuildId = guildId });
    var crystalsTotal = await conn.ExecuteScalarAsync<long>("SELECT COALESCE(SUM(crystals), 0) FROM bank_accounts WHERE guild_id = @GuildId;", new { GuildId = guildId });
    var clansCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clans WHERE guild_id = @GuildId;", new { GuildId = guildId });
    var auctionsCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auctions WHERE guild_id = @GuildId AND status = 'active';", new { GuildId = guildId });
    var openTicketsCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tickets WHERE guild_id = @GuildId AND status = 'open';", new { GuildId = guildId });
    var pendingAppsCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM verification_applications WHERE guild_id = @GuildId AND status = 'pending';", new { GuildId = guildId });
    var marriagesCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM marriages WHERE guild_id = @GuildId;", new { GuildId = guildId });
    var petsCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM user_pets WHERE guild_id = @GuildId;", new { GuildId = guildId });
    var txCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM transactions WHERE guild_id = @GuildId;", new { GuildId = guildId });

    var recentTx = await conn.QueryAsync<TransactionEntity>(@"
        SELECT * FROM transactions
        WHERE guild_id = @GuildId
        ORDER BY id DESC
        LIMIT 12;",
        new { GuildId = guildId });

    var txBreakdown = await conn.QueryAsync<(string TxType, long Count, long Volume)>(@"
        SELECT tx_type AS TxType, COUNT(*) AS Count, COALESCE(SUM(amount), 0) AS Volume
        FROM transactions
        WHERE guild_id = @GuildId
        GROUP BY tx_type
        ORDER BY Volume DESC
        LIMIT 8;",
        new { GuildId = guildId });

    return Results.Ok(new
    {
        guildId = guildId.ToString(),
        currencyName = cfg.CurrencyName,
        currencySymbol = cfg.CurrencySymbol,
        embedColorHex = cfg.EmbedColorHex,
        totalUsers = totals.TotalUsers,
        totalBalance = totals.TotalBalance,
        totalBankDeposits = bankTotal,
        totalCrystals = crystalsTotal,
        totalMessages = totals.TotalMessages,
        totalVoiceHours = Math.Round(totals.TotalVoiceSeconds / 3600.0, 1),
        clansCount,
        auctionsCount,
        openTicketsCount,
        pendingAppsCount,
        marriagesCount,
        petsCount,
        txCount,
        recentTransactions = recentTx.Select(t => new
        {
            t.Id,
            fromUserId = t.FromUserId?.ToString() ?? "Система",
            toUserId = t.ToUserId?.ToString() ?? "Система",
            t.Amount,
            t.TxType,
            t.Description,
            createdAt = t.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
        }),
        txBreakdown = txBreakdown.Select(b => new
        {
            txType = b.TxType,
            count = b.Count,
            volume = b.Volume
        })
    });
});

app.MapGet("/api/leaderboards", async (
    string? metric,
    HttpContext ctx,
    SettingsProvider sp,
    UserRepository userRepo) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var m = string.IsNullOrWhiteSpace(metric) ? "balance" : metric;
    var rows = await userRepo.GetTopAsync(sp.Current.GuildId, m, "all", 25, 0);
    return Results.Ok(rows.Select((r, i) => new
    {
        rank = i + 1,
        userId = r.UserId.ToString(),
        value = r.Value,
        level = r.Level
    }));
});

app.MapGet("/api/users", async (
    string? search,
    string? sort,
    HttpContext ctx,
    SettingsProvider sp,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var guildId = sp.Current.GuildId;
    var orderBy = sort switch
    {
        "xp" => "u.xp DESC",
        "messages" => "u.messages DESC",
        "voice" => "u.voice_seconds DESC",
        "reputation" => "COALESCE(ur.reputation, 0) DESC",
        "bank" => "COALESCE(ba.deposit_balance, 0) DESC",
        _ => "u.balance DESC"
    };

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    var users = await conn.QueryAsync<UserProfileEntity>(
        $"SELECT * FROM users u WHERE u.guild_id = @GuildId ORDER BY {orderBy} LIMIT 100;",
        new { GuildId = guildId });

    var reps = (await conn.QueryAsync<UserReputationEntity>(
        "SELECT * FROM user_reputation WHERE guild_id = @GuildId;",
        new { GuildId = guildId })).ToDictionary(x => x.UserId);

    var banks = (await conn.QueryAsync<BankAccountEntity>(
        "SELECT * FROM bank_accounts WHERE guild_id = @GuildId;",
        new { GuildId = guildId })).ToDictionary(x => x.UserId);

    var pets = (await conn.QueryAsync<UserPetEntity>(
        "SELECT * FROM user_pets WHERE guild_id = @GuildId;",
        new { GuildId = guildId })).ToDictionary(x => x.UserId);

    var clanLinks = (await conn.QueryAsync<(ulong UserId, string Tag)>(@"
        SELECT cm.user_id AS UserId, c.tag AS Tag
        FROM clan_members cm
        INNER JOIN clans c ON c.id = cm.clan_id
        WHERE cm.guild_id = @GuildId;",
        new { GuildId = guildId })).ToDictionary(x => x.UserId, x => x.Tag);

    var q = search?.Trim() ?? string.Empty;
    var list = users
        .Where(u => string.IsNullOrWhiteSpace(q) ||
                    u.UserId.ToString().Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    u.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    u.Bio.Contains(q, StringComparison.OrdinalIgnoreCase))
        .Select(u =>
        {
            reps.TryGetValue(u.UserId, out var rep);
            banks.TryGetValue(u.UserId, out var bank);
            pets.TryGetValue(u.UserId, out var pet);
            clanLinks.TryGetValue(u.UserId, out var clanTag);

            return new
            {
                userId = u.UserId.ToString(),
                u.Balance,
                bankDeposit = bank?.DepositBalance ?? 0L,
                crystals = bank?.Crystals ?? 0L,
                u.Xp,
                u.Level,
                u.Messages,
                voiceHours = Math.Round(u.VoiceSeconds / 3600.0, 1),
                reputation = rep?.Reputation ?? 0L,
                accentColorHex = rep?.AccentColorHex ?? "",
                u.Title,
                u.Bio,
                u.SelectedBackground,
                clanTag = clanTag ?? "",
                petName = pet is not null ? $"{pet.Name} (Ур.{pet.Level})" : ""
            };
        });

    return Results.Ok(list);
});

app.MapGet("/api/users/{userIdStr}", async (
    string userIdStr,
    HttpContext ctx,
    SettingsProvider sp,
    UserRepository userRepo,
    SocialExpansionRepository socialExpRepo,
    ModerationRepository modRepo,
    ItemRepository itemRepo,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    if (!ulong.TryParse(userIdStr, out var userId))
        return Results.BadRequest("Некорректный ID пользователя.");

    var guildId = sp.Current.GuildId;
    var profile = await userRepo.GetOrCreateAsync(guildId, userId);
    var rep = await socialExpRepo.GetOrCreateReputationAsync(guildId, userId);
    var bank = await socialExpRepo.GetOrCreateBankAccountAsync(guildId, userId);
    var pet = await socialExpRepo.GetUserPetAsync(guildId, userId);
    var inventory = await itemRepo.GetUserInventoryAsync(guildId, userId);
    var warns = await modRepo.GetActiveWarnsAsync(guildId, userId);

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    var recentTx = await conn.QueryAsync<TransactionEntity>(@"
        SELECT * FROM transactions
        WHERE guild_id = @GuildId AND (from_user_id = @UserId OR to_user_id = @UserId)
        ORDER BY id DESC LIMIT 20;",
        new { GuildId = guildId, UserId = userId });

    return Results.Ok(new
    {
        userId = profile.UserId.ToString(),
        profile.Balance,
        profile.Xp,
        profile.Level,
        profile.Messages,
        voiceHours = Math.Round(profile.VoiceSeconds / 3600.0, 1),
        profile.Title,
        profile.Bio,
        profile.Birthday,
        profile.SelectedBackground,
        reputation = rep.Reputation,
        positiveRep = rep.PositiveCount,
        negativeRep = rep.NegativeCount,
        accentColorHex = rep.AccentColorHex ?? "",
        bankDeposit = bank.DepositBalance,
        crystals = bank.Crystals,
        totalInterestEarned = bank.TotalInterestEarned,
        pet = pet is null ? null : new { pet.Name, pet.PetCode, pet.Rarity, pet.Level, pet.Xp, pet.Satiety },
        inventory = inventory.Select(i => new
        {
            code = i.Definition.Code,
            name = i.Definition.Name,
            rarity = i.Definition.Rarity,
            quantity = i.Inventory.Quantity
        }),
        warns = warns.Select(w => new
        {
            w.Id,
            moderatorId = w.ModeratorId.ToString(),
            w.Reason,
            createdAt = w.CreatedAt.ToString("yyyy-MM-dd HH:mm")
        }),
        transactions = recentTx.Select(t => new
        {
            t.Id,
            t.TxType,
            t.Amount,
            t.Description,
            createdAt = t.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
        })
    });
});

app.MapPost("/api/users/{userIdStr}/modify", async (
    string userIdStr,
    HttpContext ctx,
    SettingsProvider sp,
    UserRepository userRepo,
    SocialExpansionRepository socialExpRepo,
    ItemRepository itemRepo,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    if (!ulong.TryParse(userIdStr, out var userId))
        return Results.BadRequest("Некорректный ID пользователя.");

    var guildId = sp.Current.GuildId;
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    var root = doc.RootElement;

    var profile = await userRepo.GetOrCreateAsync(guildId, userId);
    await socialExpRepo.GetOrCreateReputationAsync(guildId, userId);
    await socialExpRepo.GetOrCreateBankAccountAsync(guildId, userId);

    if (root.TryGetProperty("balanceDelta", out var bd) && bd.ValueKind == JsonValueKind.Number)
    {
        var delta = bd.GetInt64();
        if (delta != 0)
            await userRepo.TryModifyBalanceAsync(guildId, userId, delta, "web_admin", "Корректировка баланса через Bot.Web");
    }

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    if (root.TryGetProperty("newXp", out var xpEl) && xpEl.ValueKind == JsonValueKind.Number)
    {
        var newXp = Math.Max(0, xpEl.GetInt64());
        var newLvl = UserRepository.CalculateLevel(newXp);
        await conn.ExecuteAsync(
            "UPDATE users SET xp = @Xp, level = @Lvl WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { Xp = newXp, Lvl = newLvl, GuildId = guildId, UserId = userId });
    }

    if (root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
    {
        await userRepo.UpdateTitleAsync(guildId, userId, titleEl.GetString() ?? profile.Title);
    }

    if (root.TryGetProperty("bio", out var bioEl) && bioEl.ValueKind == JsonValueKind.String)
    {
        await userRepo.UpdateBioAsync(guildId, userId, bioEl.GetString() ?? profile.Bio);
    }

    if (root.TryGetProperty("reputation", out var repEl) && repEl.ValueKind == JsonValueKind.Number)
    {
        await conn.ExecuteAsync(
            "UPDATE user_reputation SET reputation = @Rep, updated_at = @Now WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { Rep = repEl.GetInt64(), Now = DateTimeOffset.UtcNow, GuildId = guildId, UserId = userId });
    }

    if (root.TryGetProperty("crystals", out var cryEl) && cryEl.ValueKind == JsonValueKind.Number)
    {
        await conn.ExecuteAsync(
            "UPDATE bank_accounts SET crystals = @Crystals, updated_at = @Now WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { Crystals = Math.Max(0, cryEl.GetInt64()), Now = DateTimeOffset.UtcNow, GuildId = guildId, UserId = userId });
    }

    if (root.TryGetProperty("bankDeposit", out var depEl) && depEl.ValueKind == JsonValueKind.Number)
    {
        await conn.ExecuteAsync(
            "UPDATE bank_accounts SET deposit_balance = @Dep, updated_at = @Now WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { Dep = Math.Max(0, depEl.GetInt64()), Now = DateTimeOffset.UtcNow, GuildId = guildId, UserId = userId });
    }

    if (root.TryGetProperty("grantItemCode", out var icEl) && icEl.ValueKind == JsonValueKind.String)
    {
        var code = icEl.GetString()?.Trim();
        var qty = root.TryGetProperty("grantItemQty", out var qEl) && qEl.ValueKind == JsonValueKind.Number ? qEl.GetInt32() : 1;
        if (!string.IsNullOrWhiteSpace(code) && qty > 0)
        {
            await itemRepo.AddItemToInventoryAsync(guildId, userId, code, qty);
        }
    }

    return Results.Ok(new { status = "updated" });
});

app.MapGet("/api/shop", async (
    HttpContext ctx,
    SettingsProvider sp,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    var items = await conn.QueryAsync<ShopItemEntity>(
        "SELECT * FROM shop_items WHERE guild_id = @GuildId ORDER BY id ASC;",
        new { GuildId = sp.Current.GuildId });

    return Results.Ok(items.Select(i => new
    {
        i.Id,
        i.Name,
        i.Description,
        i.ItemType,
        i.Price,
        roleId = i.RoleId?.ToString() ?? "",
        payload = i.Payload ?? "",
        i.Enabled,
        createdAt = i.CreatedAt.ToString("yyyy-MM-dd")
    }));
});

app.MapPost("/api/shop", async (
    HttpContext ctx,
    SettingsProvider sp,
    ShopRepository shopRepo) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    var r = doc.RootElement;
    var name = r.GetProperty("name").GetString()?.Trim() ?? "Товар";
    var desc = r.TryGetProperty("description", out var d) ? d.GetString()?.Trim() ?? "" : "";
    var itemType = r.TryGetProperty("itemType", out var it) ? it.GetString() ?? "role" : "role";
    var price = r.TryGetProperty("price", out var pr) ? Math.Max(0, pr.GetInt64()) : 1000;
    var roleIdStr = r.TryGetProperty("roleId", out var rl) ? rl.GetString() : null;
    var payload = r.TryGetProperty("payload", out var pl) ? pl.GetString() : null;

    ulong? roleId = ulong.TryParse(roleIdStr, out var parsedRole) && parsedRole != 0 ? parsedRole : null;
    var id = await shopRepo.AddShopItemAsync(sp.Current.GuildId, name, desc, itemType, price, roleId, payload);
    return Results.Ok(new { id });
});

app.MapDelete("/api/shop/{id:long}", async (
    long id,
    HttpContext ctx,
    SettingsProvider sp,
    ShopRepository shopRepo) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    await shopRepo.RemoveShopItemAsync(sp.Current.GuildId, id);
    return Results.Ok(new { deleted = id });
});

app.MapGet("/api/items", async (
    HttpContext ctx,
    SettingsProvider sp,
    ItemRepository itemRepo) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var guildId = sp.Current.GuildId;
    await itemRepo.EnsureDefaultItemsAsync(guildId);
    var defs = await itemRepo.GetDefinitionsAsync(guildId);

    return Results.Ok(defs.Select(d => new
    {
        d.Id,
        d.Code,
        d.Name,
        d.Description,
        d.Category,
        d.Rarity,
        d.MaxStack,
        d.BasePrice,
        d.SellPrice,
        d.Tradeable,
        d.Usable,
        effectJson = d.EffectJson ?? "",
        recipeJson = d.RecipeJson ?? ""
    }));
});

app.MapPost("/api/items", async (
    HttpContext ctx,
    SettingsProvider sp,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    var r = doc.RootElement;
    var guildId = sp.Current.GuildId;

    var code = (r.GetProperty("code").GetString() ?? "custom_item").Trim().ToLowerInvariant();
    var name = (r.GetProperty("name").GetString() ?? "Предмет").Trim();
    var desc = (r.TryGetProperty("description", out var d) ? d.GetString() : "") ?? "";
    var cat = (r.TryGetProperty("category", out var c) ? c.GetString() : "resource") ?? "resource";
    var rarity = (r.TryGetProperty("rarity", out var rar) ? rar.GetString() : "common") ?? "common";
    var basePrice = r.TryGetProperty("basePrice", out var bp) ? Math.Max(0, bp.GetInt64()) : 100;
    var sellPrice = r.TryGetProperty("sellPrice", out var spEl) ? Math.Max(0, spEl.GetInt64()) : 50;
    var usable = r.TryGetProperty("usable", out var uEl) && uEl.GetBoolean();
    var tradeable = !r.TryGetProperty("tradeable", out var tEl) || tEl.GetBoolean();

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    await conn.ExecuteAsync(@"
        INSERT INTO item_definitions (
            guild_id, code, name, description, category, rarity, max_stack,
            base_price, sell_price, tradeable, usable, effect_json, recipe_json, created_at
        ) VALUES (
            @GuildId, @Code, @Name, @Description, @Category, @Rarity, 99,
            @BasePrice, @SellPrice, @Tradeable, @Usable, NULL, NULL, @Now
        )
        ON CONFLICT(guild_id, code) DO UPDATE SET
            name = excluded.name,
            description = excluded.description,
            category = excluded.category,
            rarity = excluded.rarity,
            base_price = excluded.base_price,
            sell_price = excluded.sell_price,
            tradeable = excluded.tradeable,
            usable = excluded.usable;",
        new
        {
            GuildId = guildId,
            Code = code,
            Name = name,
            Description = desc,
            Category = cat,
            Rarity = rarity,
            BasePrice = basePrice,
            SellPrice = sellPrice,
            Tradeable = tradeable ? 1 : 0,
            Usable = usable ? 1 : 0,
            Now = DateTimeOffset.UtcNow
        });

    return Results.Ok(new { status = "saved", code });
});

app.MapDelete("/api/items/{id:long}", async (
    long id,
    HttpContext ctx,
    SettingsProvider sp,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    await conn.ExecuteAsync("DELETE FROM item_definitions WHERE id = @Id AND guild_id = @GuildId;", new { Id = id, GuildId = sp.Current.GuildId });
    return Results.Ok(new { deleted = id });
});

app.MapGet("/api/clans", async (
    HttpContext ctx,
    SettingsProvider sp,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var guildId = sp.Current.GuildId;
    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    var clans = await conn.QueryAsync<ClanEntity>(
        "SELECT * FROM clans WHERE guild_id = @GuildId ORDER BY points DESC;",
        new { GuildId = guildId });

    var counts = (await conn.QueryAsync<(long ClanId, int Count)>(
        "SELECT clan_id AS ClanId, COUNT(*) AS Count FROM clan_members WHERE guild_id = @GuildId GROUP BY clan_id;",
        new { GuildId = guildId })).ToDictionary(x => x.ClanId, x => x.Count);

    var wars = await conn.QueryAsync<ClanWarEntity>(
        "SELECT * FROM clan_wars WHERE guild_id = @GuildId ORDER BY id DESC LIMIT 15;",
        new { GuildId = guildId });

    return Results.Ok(new
    {
        clans = clans.Select(c => new
        {
            c.Id,
            c.Name,
            c.Tag,
            c.Description,
            leaderId = c.LeaderId.ToString(),
            c.Treasury,
            c.Points,
            memberCount = counts.GetValueOrDefault(c.Id, 1),
            c.MemberLimit,
            c.EarnBonusPercent,
            c.ColorHex,
            createdAt = c.CreatedAt.ToString("yyyy-MM-dd")
        }),
        wars = wars.Select(w => new
        {
            w.Id,
            w.ChallengerClanId,
            w.DefenderClanId,
            w.ChallengerScore,
            w.DefenderScore,
            w.WagerPercent,
            w.Status,
            endsAt = w.EndsAt?.ToString("yyyy-MM-dd HH:mm") ?? "-"
        })
    });
});

app.MapPost("/api/clans/{id:long}/modify", async (
    long id,
    HttpContext ctx,
    SettingsProvider sp,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    var r = doc.RootElement;
    var treasury = r.TryGetProperty("treasury", out var tEl) ? Math.Max(0, tEl.GetInt64()) : 0L;
    var points = r.TryGetProperty("points", out var pEl) ? Math.Max(0, pEl.GetInt64()) : 0L;

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    await conn.ExecuteAsync(
        "UPDATE clans SET treasury = @Treasury, points = @Points WHERE id = @Id AND guild_id = @GuildId;",
        new { Treasury = treasury, Points = points, Id = id, GuildId = sp.Current.GuildId });

    return Results.Ok(new { status = "updated" });
});

app.MapGet("/api/auctions", async (
    HttpContext ctx,
    SettingsProvider sp,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    var lots = await conn.QueryAsync<AuctionEntity>(@"
        SELECT * FROM auctions
        WHERE guild_id = @GuildId
        ORDER BY id DESC
        LIMIT 40;",
        new { GuildId = sp.Current.GuildId });

    return Results.Ok(lots.Select(a => new
    {
        a.Id,
        sellerId = a.SellerId.ToString(),
        a.LotType,
        a.LotTitle,
        a.ItemQuantity,
        a.StartPrice,
        a.CurrentBid,
        buyoutPrice = a.BuyoutPrice,
        highestBidderId = a.HighestBidderId?.ToString() ?? "-",
        a.Status,
        endsAt = a.EndsAt.ToString("yyyy-MM-dd HH:mm")
    }));
});

app.MapGet("/api/families", async (
    HttpContext ctx,
    SettingsProvider sp,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var guildId = sp.Current.GuildId;
    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    var marriages = (await conn.QueryAsync<MarriageEntity>(
        "SELECT * FROM marriages WHERE guild_id = @GuildId ORDER BY id DESC;",
        new { GuildId = guildId })).ToList();

    var profiles = (await conn.QueryAsync<FamilyProfileEntity>(
        "SELECT * FROM family_profiles WHERE guild_id = @GuildId;",
        new { GuildId = guildId })).ToDictionary(x => x.MarriageId);

    var kidsCount = (await conn.QueryAsync<(long MarriageId, int Count)>(
        "SELECT marriage_id AS MarriageId, COUNT(*) AS Count FROM family_members WHERE guild_id = @GuildId GROUP BY marriage_id;",
        new { GuildId = guildId })).ToDictionary(x => x.MarriageId, x => x.Count);

    return Results.Ok(marriages.Select(m =>
    {
        profiles.TryGetValue(m.Id, out var fp);
        return new
        {
            m.Id,
            user1Id = m.User1Id.ToString(),
            user2Id = m.User2Id.ToString(),
            familyName = fp?.FamilyName ?? "Семья KRLZ",
            familyBalance = fp?.FamilyBalance ?? 0L,
            loveLevel = fp?.LoveLevel ?? 1,
            loveXp = fp?.LoveXp ?? 0L,
            childrenCount = kidsCount.GetValueOrDefault(m.Id, 0),
            marriedAt = m.MarriedAt.ToString("yyyy-MM-dd")
        };
    }));
});

app.MapGet("/api/progression", async (
    HttpContext ctx,
    SettingsProvider sp,
    ProgressionRepository progRepo,
    AchievementRepository achRepo,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var guildId = sp.Current.GuildId;
    await progRepo.EnsureDefaultQuestsAndSeasonAsync(guildId);
    await achRepo.EnsureDefaultAchievementsAsync(guildId);

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    var quests = await conn.QueryAsync<QuestEntity>("SELECT * FROM quests WHERE guild_id = @GuildId ORDER BY id ASC;", new { GuildId = guildId });
    var achievements = await conn.QueryAsync<AchievementEntity>("SELECT * FROM achievements WHERE guild_id = @GuildId ORDER BY id ASC;", new { GuildId = guildId });
    var season = await progRepo.GetActiveSeasonAsync(guildId);

    return Results.Ok(new
    {
        season = season is null ? null : new
        {
            season.Id,
            season.Name,
            season.MaxLevel,
            season.XpPerLevel,
            season.PremiumPrice,
            startsAt = season.StartsAt.ToString("yyyy-MM-dd"),
            endsAt = season.EndsAt.ToString("yyyy-MM-dd")
        },
        quests = quests.Select(q => new
        {
            q.Id,
            q.Code,
            q.Name,
            q.Description,
            q.QuestPeriod,
            q.ObjectiveType,
            q.TargetValue,
            q.RewardCoins,
            q.RewardXp,
            q.RewardBpXp,
            q.Enabled
        }),
        achievements = achievements.Select(a => new
        {
            a.Id,
            a.Code,
            a.Name,
            a.Description,
            a.EventType,
            a.TargetValue,
            a.Rarity,
            a.RewardCoins,
            a.RewardXp
        })
    });
});

app.MapPost("/api/quests", async (
    HttpContext ctx,
    SettingsProvider sp,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    var r = doc.RootElement;
    var code = (r.GetProperty("code").GetString() ?? "custom_quest").Trim().ToLowerInvariant();
    var name = (r.GetProperty("name").GetString() ?? "Квест").Trim();
    var desc = (r.TryGetProperty("description", out var d) ? d.GetString() : "") ?? "";
    var period = (r.TryGetProperty("questPeriod", out var p) ? p.GetString() : "daily") ?? "daily";
    var objType = (r.TryGetProperty("objectiveType", out var o) ? o.GetString() : "messages") ?? "messages";
    var targetVal = r.TryGetProperty("targetValue", out var tv) ? Math.Max(1, tv.GetInt64()) : 20;
    var coins = r.TryGetProperty("rewardCoins", out var rc) ? Math.Max(0, rc.GetInt64()) : 200;
    var xp = r.TryGetProperty("rewardXp", out var rx) ? Math.Max(0, rx.GetInt64()) : 120;

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    await conn.ExecuteAsync(@"
        INSERT INTO quests (
            guild_id, code, name, description, quest_period, objective_type,
            target_value, reward_coins, reward_xp, reward_bp_xp, reward_item_def_id, enabled, created_at
        ) VALUES (
            @GuildId, @Code, @Name, @Description, @Period, @ObjType,
            @TargetValue, @Coins, @Xp, 120, NULL, 1, @Now
        )
        ON CONFLICT(guild_id, code) DO UPDATE SET
            name = excluded.name,
            description = excluded.description,
            quest_period = excluded.quest_period,
            objective_type = excluded.objective_type,
            target_value = excluded.target_value,
            reward_coins = excluded.reward_coins,
            reward_xp = excluded.reward_xp;",
        new
        {
            GuildId = sp.Current.GuildId,
            Code = code,
            Name = name,
            Description = desc,
            Period = period,
            ObjType = objType,
            TargetValue = targetVal,
            Coins = coins,
            Xp = xp,
            Now = DateTimeOffset.UtcNow
        });

    return Results.Ok(new { status = "saved", code });
});

app.MapGet("/api/punishments", async (
    HttpContext ctx,
    SettingsProvider sp,
    ModerationRepository modRepo) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var list = await modRepo.GetRecentModlogAsync(sp.Current.GuildId, 40);
    return Results.Ok(list.Select(p => new
    {
        p.Id,
        userId = p.UserId.ToString(),
        moderatorId = p.ModeratorId.ToString(),
        p.Type,
        p.Reason,
        p.Active,
        createdAt = p.CreatedAt.ToString("yyyy-MM-dd HH:mm")
    }));
});

app.MapGet("/api/logs", async (
    string? type,
    HttpContext ctx,
    SettingsProvider sp,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var guildId = sp.Current.GuildId;
    await using var conn = await dbFactory.CreateOpenConnectionAsync();

    if (type == "nicknames")
    {
        var nicks = await conn.QueryAsync<NicknameHistoryEntity>(
            "SELECT * FROM nickname_history WHERE guild_id = @GuildId ORDER BY id DESC LIMIT 40;",
            new { GuildId = guildId });
        return Results.Ok(nicks.Select(n => new
        {
            n.Id,
            userId = n.UserId.ToString(),
            oldNickname = n.OldNickname ?? "-",
            newNickname = n.NewNickname ?? "-",
            changedAt = n.ChangedAt.ToString("yyyy-MM-dd HH:mm")
        }));
    }

    if (type == "verifications")
    {
        var apps = await conn.QueryAsync<VerificationApplicationEntity>(
            "SELECT * FROM verification_applications WHERE guild_id = @GuildId ORDER BY id DESC LIMIT 30;",
            new { GuildId = guildId });
        return Results.Ok(apps.Select(a => new
        {
            a.Id,
            userId = a.UserId.ToString(),
            a.AnswersJson,
            a.Status,
            reviewerId = a.ReviewerId?.ToString() ?? "-",
            reviewReason = a.ReviewReason ?? "",
            createdAt = a.CreatedAt.ToString("yyyy-MM-dd HH:mm")
        }));
    }

    var msgs = await conn.QueryAsync<MessageLogEntity>(
        "SELECT * FROM message_logs WHERE guild_id = @GuildId ORDER BY created_at DESC LIMIT 40;",
        new { GuildId = guildId });

    return Results.Ok(msgs.Select(m => new
    {
        messageId = m.MessageId.ToString(),
        channelId = m.ChannelId.ToString(),
        authorId = m.AuthorId.ToString(),
        m.AuthorName,
        m.Content,
        m.IsDeleted,
        editedContent = m.EditedContent ?? "",
        createdAt = m.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
    }));
});

app.MapGet("/api/tickets", async (
    HttpContext ctx,
    SettingsProvider sp,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    var tickets = await conn.QueryAsync<TicketEntity>(@"
        SELECT * FROM tickets
        WHERE guild_id = @GuildId
        ORDER BY id DESC
        LIMIT 30;",
        new { GuildId = sp.Current.GuildId });

    return Results.Ok(tickets.Select(t => new
    {
        t.Id,
        t.TicketNumber,
        ownerId = t.OwnerId.ToString(),
        claimedById = t.ClaimedById?.ToString() ?? "-",
        t.Category,
        t.Subject,
        t.Status,
        t.CloseReason,
        t.Rating,
        createdAt = t.CreatedAt.ToString("yyyy-MM-dd HH:mm")
    }));
});

app.MapGet("/api/tickets/{id:long}/messages", async (
    long id,
    HttpContext ctx,
    SettingsProvider sp,
    TicketRepository ticketRepo) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var messages = await ticketRepo.GetMessagesAsync(id);
    return Results.Ok(messages.Select(m => new
    {
        m.Id,
        authorId = m.AuthorId.ToString(),
        m.AuthorName,
        m.Content,
        m.Attachments,
        createdAt = m.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
    }));
});

app.MapGet("/api/security-events", async (
    HttpContext ctx,
    SettingsProvider sp,
    SecurityRepository secRepo) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var events = await secRepo.GetRecentEventsAsync(sp.Current.GuildId, 35);
    return Results.Ok(events.Select(e => new
    {
        e.Id,
        offenderId = e.OffenderId.ToString(),
        e.EventType,
        e.Details,
        e.ActionTaken,
        createdAt = e.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
    }));
});

app.MapGet("/api/transactions", async (
    string? txType,
    HttpContext ctx,
    SettingsProvider sp,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    var sql = string.IsNullOrWhiteSpace(txType)
        ? "SELECT * FROM transactions WHERE guild_id = @GuildId ORDER BY id DESC LIMIT 60;"
        : "SELECT * FROM transactions WHERE guild_id = @GuildId AND tx_type = @TxType ORDER BY id DESC LIMIT 60;";

    var rows = await conn.QueryAsync<TransactionEntity>(sql, new { GuildId = sp.Current.GuildId, TxType = txType });
    return Results.Ok(rows.Select(t => new
    {
        t.Id,
        fromUserId = t.FromUserId?.ToString() ?? "Система",
        toUserId = t.ToUserId?.ToString() ?? "Система",
        t.Amount,
        t.TxType,
        t.Description,
        createdAt = t.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
    }));
});

app.MapGet("/api/settings", async (
    HttpContext ctx,
    SettingsProvider sp,
    GuildSettingsRepository settingsRepo) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var cfg = await settingsRepo.GetOrCreateAsync(sp.Current.GuildId);
    var cur = sp.Current;

    return Results.Ok(new
    {
        cfg.EmbedColorHex,
        cfg.CurrencyName,
        cfg.CurrencySymbol,
        cfg.CustomRolePrice,
        cfg.WarnThresholdMute,
        cfg.WarnThresholdBan,
        cfg.AntispamEnabled,
        cfg.AntinukeEnabled,
        cfg.AntiraidEnabled,
        cfg.CaptchaEnabled,
        dailyAmount = cur.Economy.DailyAmount,
        workMinAmount = cur.Economy.WorkMinAmount,
        workMaxAmount = cur.Economy.WorkMaxAmount,
        messageRewardAmount = cur.Economy.MessageRewardAmount,
        voiceRewardPerMinute = cur.Economy.VoiceRewardPerMinute,
        xpPerMessage = cur.Economy.XpPerMessage,
        xpPerVoiceMinute = cur.Economy.XpPerVoiceMinute,
        clanPrice = cur.Clans.CreationPrice,
        clanBaseMemberLimit = cur.Clans.BaseMemberLimit,
        auctionFee = cur.Auction.CommissionPercent,
        casinoMinBet = cur.Casino.MinBet,
        casinoMaxBet = cur.Casino.MaxBet,
        casinoDailyLossLimit = cur.Casino.DailyLossLimit,
        bankDailyInterestPercent = cur.Bank.DailyInterestPercent,
        bankMaxInterestCap = cur.Bank.MaxInterestCapPerDay,
        crystalBuyRate = cur.Bank.CrystalBuyRate,
        crystalSellRate = cur.Bank.CrystalSellRate,
        repCooldownHours = cur.Social.ReputationCooldownHours,
        adoptionFee = cur.Social.AdoptionFee,
        petAdoptPrice = cur.Pets.AdoptPrice,
        petFeedCost = cur.Pets.FeedCost
    });
});

app.MapPost("/api/settings", async (
    HttpContext ctx,
    SettingsProvider sp,
    GuildSettingsRepository settingsRepo) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    var root = doc.RootElement;
    var cfg = await settingsRepo.GetOrCreateAsync(sp.Current.GuildId);

    var embedColor = root.TryGetProperty("embedColorHex", out var ec) ? ec.GetString() ?? cfg.EmbedColorHex : cfg.EmbedColorHex;
    var currencyName = root.TryGetProperty("currencyName", out var cn) ? cn.GetString() ?? cfg.CurrencyName : cfg.CurrencyName;
    var currencySymbol = root.TryGetProperty("currencySymbol", out var cs) ? cs.GetString() ?? cfg.CurrencySymbol : cfg.CurrencySymbol;
    var rolePrice = root.TryGetProperty("customRolePrice", out var rp) ? rp.GetInt64() : cfg.CustomRolePrice;
    var warnMute = root.TryGetProperty("warnThresholdMute", out var wm) ? wm.GetInt32() : cfg.WarnThresholdMute;
    var warnBan = root.TryGetProperty("warnThresholdBan", out var wb) ? wb.GetInt32() : cfg.WarnThresholdBan;
    var antispam = root.TryGetProperty("antispamEnabled", out var ase) ? ase.GetBoolean() : cfg.AntispamEnabled;
    var antinuke = root.TryGetProperty("antinukeEnabled", out var ane) ? ane.GetBoolean() : cfg.AntinukeEnabled;
    var antiraid = root.TryGetProperty("antiraidEnabled", out var are) ? are.GetBoolean() : cfg.AntiraidEnabled;
    var captcha = root.TryGetProperty("captchaEnabled", out var ce) ? ce.GetBoolean() : cfg.CaptchaEnabled;

    var updated = cfg with
    {
        EmbedColorHex = embedColor,
        CurrencyName = currencyName,
        CurrencySymbol = currencySymbol,
        CustomRolePrice = rolePrice,
        WarnThresholdMute = warnMute,
        WarnThresholdBan = warnBan,
        AntispamEnabled = antispam,
        AntinukeEnabled = antinuke,
        AntiraidEnabled = antiraid,
        CaptchaEnabled = captcha,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    await settingsRepo.UpsertAsync(updated);
    await sp.UpdateAsync(s =>
    {
        s.EmbedColorHex = embedColor;
        s.CurrencyName = currencyName;
        s.CurrencySymbol = currencySymbol;
        s.CustomRolePrice = rolePrice;
        s.Moderation.WarnThresholdMute = warnMute;
        s.Moderation.WarnThresholdBan = warnBan;
        s.Antispam.Enabled = antispam;
        s.Security.AntiNukeEnabled = antinuke;
        s.Security.AntiRaidEnabled = antiraid;
        s.Security.CaptchaEnabled = captcha;

        if (root.TryGetProperty("dailyAmount", out var da)) s.Economy.DailyAmount = da.GetInt64();
        if (root.TryGetProperty("workMinAmount", out var wmin)) s.Economy.WorkMinAmount = wmin.GetInt64();
        if (root.TryGetProperty("workMaxAmount", out var wmax)) s.Economy.WorkMaxAmount = wmax.GetInt64();
        if (root.TryGetProperty("messageRewardAmount", out var mra)) s.Economy.MessageRewardAmount = mra.GetInt64();
        if (root.TryGetProperty("voiceRewardPerMinute", out var vrpm)) s.Economy.VoiceRewardPerMinute = vrpm.GetInt64();
        if (root.TryGetProperty("xpPerMessage", out var xpm)) s.Economy.XpPerMessage = xpm.GetInt64();
        if (root.TryGetProperty("xpPerVoiceMinute", out var xpvm)) s.Economy.XpPerVoiceMinute = xpvm.GetInt64();
        if (root.TryGetProperty("clanPrice", out var cp)) s.Clans.CreationPrice = cp.GetInt64();
        if (root.TryGetProperty("auctionFee", out var af)) s.Auction.CommissionPercent = af.GetInt32();
        if (root.TryGetProperty("casinoMinBet", out var cmin)) s.Casino.MinBet = cmin.GetInt64();
        if (root.TryGetProperty("casinoMaxBet", out var cmax)) s.Casino.MaxBet = cmax.GetInt64();
        if (root.TryGetProperty("casinoDailyLossLimit", out var cdll)) s.Casino.DailyLossLimit = cdll.GetInt64();
        if (root.TryGetProperty("bankDailyInterestPercent", out var bdip)) s.Bank.DailyInterestPercent = bdip.GetDouble();
        if (root.TryGetProperty("bankMaxInterestCap", out var bmic)) s.Bank.MaxInterestCapPerDay = bmic.GetInt64();
        if (root.TryGetProperty("crystalBuyRate", out var cbr)) s.Bank.CrystalBuyRate = cbr.GetInt64();
        if (root.TryGetProperty("crystalSellRate", out var csr)) s.Bank.CrystalSellRate = csr.GetInt64();
        if (root.TryGetProperty("repCooldownHours", out var rch)) s.Social.ReputationCooldownHours = rch.GetInt32();
        if (root.TryGetProperty("adoptionFee", out var adf)) s.Social.AdoptionFee = adf.GetInt64();
        if (root.TryGetProperty("petAdoptPrice", out var pap)) s.Pets.AdoptPrice = pap.GetInt64();
        if (root.TryGetProperty("petFeedCost", out var pfc)) s.Pets.FeedCost = pfc.GetInt64();
    });

    return Results.Ok(new { status = "saved" });
});


app.MapGet("/api/party-games", async (
    HttpContext ctx,
    SettingsProvider sp,
    PartyGameRepository partyRepo,
    DbConnectionFactory dbFactory) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    var guildId = sp.Current.GuildId;
    var sessions = await partyRepo.GetRecentSessionsAsync(guildId, 30);
    var customContent = await partyRepo.GetCustomContentAsync(guildId);
    var activeBoss = await partyRepo.GetActiveRaidBossAsync(guildId);

    await using var conn = await dbFactory.CreateOpenConnectionAsync();
    var raidBosses = await conn.QueryAsync<RaidBossSessionEntity>(
        "SELECT * FROM raid_boss_sessions WHERE guild_id = @GuildId ORDER BY id DESC LIMIT 15;",
        new { GuildId = guildId });

    return Results.Ok(new
    {
        sessions = sessions.Select(s => new
        {
            s.Id,
            s.GameType,
            hostId = s.HostId.ToString(),
            s.Bet,
            s.PrizePool,
            s.Status,
            s.Phase,
            s.RoundNum,
            s.WinnerSummary,
            createdAt = s.CreatedAt.ToString("yyyy-MM-dd HH:mm")
        }),
        raidBosses = raidBosses.Select(b => new
        {
            b.Id,
            b.BossName,
            b.BossLevel,
            b.CurrentHp,
            b.MaxHp,
            b.RewardPoolCoins,
            b.RewardCrystals,
            b.Status,
            b.LastLog,
            createdAt = b.CreatedAt.ToString("yyyy-MM-dd HH:mm")
        }),
        customContent = customContent.Select(c => new
        {
            c.Id,
            c.GameType,
            c.Title,
            c.Subtitle,
            c.PayloadJson,
            createdAt = c.CreatedAt.ToString("yyyy-MM-dd")
        }),
        hasActiveBoss = activeBoss is not null
    });
});

app.MapPost("/api/party-games/content", async (
    HttpContext ctx,
    SettingsProvider sp,
    PartyGameRepository partyRepo) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    var r = doc.RootElement;
    var gameType = r.TryGetProperty("gameType", out var gt) ? gt.GetString() ?? "bunker_disaster" : "bunker_disaster";
    var title = r.TryGetProperty("title", out var t) ? t.GetString() ?? "Карточка" : "Карточка";
    var subtitle = r.TryGetProperty("subtitle", out var st) ? st.GetString() ?? "" : "";
    var payload = r.TryGetProperty("payloadJson", out var pj) ? pj.GetString() ?? "{}" : "{}";

    var id = await partyRepo.AddCustomContentAsync(sp.Current.GuildId, gameType, title, subtitle, payload);
    return Results.Ok(new { id, status = "saved" });
});

app.MapDelete("/api/party-games/content/{id:long}", async (
    long id,
    HttpContext ctx,
    SettingsProvider sp,
    PartyGameRepository partyRepo) =>
{
    if (!IsAuthorized(ctx, sp))
        return Results.Unauthorized();

    await partyRepo.DeleteCustomContentAsync(sp.Current.GuildId, id);
    return Results.Ok(new { deleted = id });
});

app.Run(app.Services.GetRequiredService<SettingsProvider>().Current.WebPanel.ListenUrl);
