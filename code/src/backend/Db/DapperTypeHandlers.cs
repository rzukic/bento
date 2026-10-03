using System.Data;
using System.Globalization;
using Dapper;

namespace Bento.Api.Db;

/// <summary>
/// Teaches Dapper the parameter types it does not bind by itself. Postgres
/// <c>DATE</c> columns get <see cref="DateOnly"/> values
/// (<see cref="Services.CrudService.CoerceTemporalForProvider"/>); without a
/// handler Dapper rejects them ("cannot be used as a parameter value") on
/// inserts, updates and date filters.
/// </summary>
public static class DapperTypeHandlers
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value;
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly date => date,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            string text => DateOnly.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType().Name} to DateOnly.")
        };
    }
}
