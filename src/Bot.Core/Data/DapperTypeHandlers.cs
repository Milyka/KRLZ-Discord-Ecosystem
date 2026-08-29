using System.Data;
using System.Globalization;
using Dapper;

namespace Bot.Data;

public static class DapperConfig
{
    private static bool _configured;

    public static void Configure()
    {
        if (_configured)
            return;

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new UlongTypeHandler());
        SqlMapper.AddTypeHandler(new NullableUlongTypeHandler());
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new NullableDateTimeOffsetHandler());
        _configured = true;
    }

    private sealed class UlongTypeHandler : SqlMapper.TypeHandler<ulong>
    {
        public override void SetValue(IDbDataParameter parameter, ulong value)
        {
            parameter.DbType = DbType.Int64;
            parameter.Value = unchecked((long)value);
        }

        public override ulong Parse(object value) => value switch
        {
            long l => unchecked((ulong)l),
            ulong u => u,
            int i => (ulong)i,
            string s => ulong.Parse(s, CultureInfo.InvariantCulture),
            _ => Convert.ToUInt64(value, CultureInfo.InvariantCulture)
        };
    }

    private sealed class NullableUlongTypeHandler : SqlMapper.TypeHandler<ulong?>
    {
        public override void SetValue(IDbDataParameter parameter, ulong? value)
        {
            parameter.DbType = DbType.Int64;
            parameter.Value = value.HasValue ? unchecked((long)value.Value) : DBNull.Value;
        }

        public override ulong? Parse(object value)
        {
            if (value is null or DBNull)
                return null;

            return value switch
            {
                long l => unchecked((ulong)l),
                ulong u => u,
                int i => (ulong)i,
                string s when string.IsNullOrWhiteSpace(s) => null,
                string s => ulong.Parse(s, CultureInfo.InvariantCulture),
                _ => Convert.ToUInt64(value, CultureInfo.InvariantCulture)
            };
        }
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            _ => DateTimeOffset.UtcNow
        };
    }

    private sealed class NullableDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset?>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset? value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.HasValue
                ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value;
        }

        public override DateTimeOffset? Parse(object value)
        {
            if (value is null or DBNull)
                return null;

            return value switch
            {
                DateTimeOffset dto => dto,
                DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
                string s when string.IsNullOrWhiteSpace(s) => null,
                string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                _ => null
            };
        }
    }
}
