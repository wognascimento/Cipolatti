using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Cipolatti.API.Infrastructure;

public sealed class DapperDateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        if (parameter is NpgsqlParameter npgsqlParameter)
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Date;
        else
            parameter.DbType = DbType.Date;
        parameter.Value = value;
    }

    public override DateOnly Parse(object value) => value switch
    {
        DateOnly date => date,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        string text when DateOnly.TryParse(text, out var date) => date,
        _ => throw new DataException($"Não foi possível converter {value.GetType().Name} para DateOnly.")
    };
}

public sealed class DapperTimeOnlyTypeHandler : SqlMapper.TypeHandler<TimeOnly>
{
    public override void SetValue(IDbDataParameter parameter, TimeOnly value)
    {
        if (parameter is NpgsqlParameter npgsqlParameter)
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Time;
        else
            parameter.DbType = DbType.Time;
        parameter.Value = value;
    }

    public override TimeOnly Parse(object value) => value switch
    {
        TimeOnly time => time,
        TimeSpan timeSpan => TimeOnly.FromTimeSpan(timeSpan),
        DateTime dateTime => TimeOnly.FromDateTime(dateTime),
        string text when TimeOnly.TryParse(text, out var time) => time,
        _ => throw new DataException($"Não foi possível converter {value.GetType().Name} para TimeOnly.")
    };
}
