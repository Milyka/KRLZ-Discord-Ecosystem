using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class CustomRoleRepository(DbConnectionFactory dbFactory)
{
    public async Task<CustomRoleEntity?> CreateWithPaymentAsync(
        ulong guildId,
        ulong ownerId,
        ulong roleId,
        string name,
        string colorHex,
        long price,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(@"
            INSERT INTO users (guild_id, user_id, created_at)
            VALUES (@GuildId, @UserId, @CreatedAt)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, UserId = ownerId, CreatedAt = DateTimeOffset.UtcNow },
            transaction: tx);

        var updated = await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance - @Price
            WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Price;",
            new { GuildId = guildId, UserId = ownerId, Price = price },
            transaction: tx);

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var id = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO custom_roles (guild_id, owner_id, role_id, name, color_hex, created_at)
            VALUES (@GuildId, @OwnerId, @RoleId, @Name, @ColorHex, @CreatedAt);
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                OwnerId = ownerId,
                RoleId = roleId,
                Name = name,
                ColorHex = colorHex,
                CreatedAt = now
            },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @UserId, NULL, @Price, 'custom_role', @Description, @CreatedAt);",
            new
            {
                GuildId = guildId,
                UserId = (ulong?)ownerId,
                Price = price,
                Description = $"Создание кастомной роли {name}",
                CreatedAt = now
            },
            transaction: tx);

        await tx.CommitAsync(ct);
        return new CustomRoleEntity(id, guildId, ownerId, roleId, name, colorHex, now);
    }

    public async Task<IReadOnlyList<CustomRoleEntity>> GetByOwnerAsync(ulong guildId, ulong ownerId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CustomRoleEntity>(@"
            SELECT * FROM custom_roles
            WHERE guild_id = @GuildId AND owner_id = @OwnerId
            ORDER BY id DESC;",
            new { GuildId = guildId, OwnerId = ownerId });
        return rows.ToList();
    }

    public async Task<CustomRoleEntity?> GetByRoleIdAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<CustomRoleEntity>(@"
            SELECT * FROM custom_roles
            WHERE guild_id = @GuildId AND role_id = @RoleId;",
            new { GuildId = guildId, RoleId = roleId });
    }

    public async Task UpdateMetadataAsync(ulong guildId, ulong roleId, string name, string colorHex, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE custom_roles
            SET name = @Name, color_hex = @ColorHex
            WHERE guild_id = @GuildId AND role_id = @RoleId;",
            new { GuildId = guildId, RoleId = roleId, Name = name, ColorHex = colorHex });
    }

    public async Task DeleteByRoleIdAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM custom_roles WHERE guild_id = @GuildId AND role_id = @RoleId;",
            new { GuildId = guildId, RoleId = roleId });
    }
}
