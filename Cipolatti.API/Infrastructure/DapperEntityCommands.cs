using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using Dapper;

namespace Cipolatti.API.Infrastructure;

public static class DapperEntityCommands
{
    public static (string Sql, DynamicParameters Parameters) Insert<T>(T entity, bool returnRow = false)
    {
        var table = Table<T>();
        var properties = Properties<T>().Where(p => !IsGeneratedKey(p, entity)).ToArray();
        var columns = properties.Select(Column);
        var names = properties.Select(p => "@" + p.Name);
        var parameters = Parameters(entity, properties);
        return ($"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", names)})" + (returnRow ? " RETURNING *" : string.Empty), parameters);
    }

    public static (string Sql, DynamicParameters Parameters) Update<T>(T entity)
    {
        var properties = Properties<T>().ToArray();
        var key = properties.SingleOrDefault(p => p.GetCustomAttribute<KeyAttribute>() is not null)
            ?? throw new InvalidOperationException($"{typeof(T).Name} não possui chave.");
        var setters = properties.Where(p => p != key).Select(p => $"{Column(p)} = @{p.Name}");
        return ($"UPDATE {Table<T>()} SET {string.Join(", ", setters)} WHERE {Column(key)} = @{key.Name}", Parameters(entity, properties));
    }

    private static IEnumerable<PropertyInfo> Properties<T>() => typeof(T).GetProperties()
        .Where(p => p.CanRead && IsScalar(p.PropertyType) && p.GetCustomAttribute<NotMappedAttribute>() is null);
    private static bool IsGeneratedKey<T>(PropertyInfo property, T entity)
    {
        if (property.GetCustomAttribute<KeyAttribute>() is null) return false;
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type != typeof(byte) && type != typeof(short) && type != typeof(int) && type != typeof(long)) return false;
        return Convert.ToInt64(property.GetValue(entity) ?? 0) == 0;
    }
    private static string Column(PropertyInfo property) => property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name;
    private static string Table<T>()
    {
        var table = typeof(T).GetCustomAttribute<TableAttribute>() ?? throw new InvalidOperationException($"{typeof(T).Name} não possui TableAttribute.");
        return string.IsNullOrWhiteSpace(table.Schema) ? table.Name : $"{table.Schema}.{table.Name}";
    }
    private static DynamicParameters Parameters<T>(T entity, IEnumerable<PropertyInfo> properties)
    {
        var result = new DynamicParameters();
        foreach (var property in properties) result.Add(property.Name, property.GetValue(entity));
        return result;
    }
    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
               type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) ||
               type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(Guid);
    }
}
