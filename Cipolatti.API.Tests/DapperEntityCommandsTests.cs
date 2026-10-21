using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Npgsql;
using NpgsqlTypes;
namespace Cipolatti.API.Tests;

public sealed class DapperEntityCommandsTests
{
    [Fact]
    public void Romaneio_uses_PostgreSql_date_and_time_types()
    {
        Assert.Equal(typeof(DateOnly), typeof(TRomaneio).GetProperty(nameof(TRomaneio.DataCarregamento))!.PropertyType);
        Assert.Equal(typeof(TimeOnly), typeof(TRomaneio).GetProperty(nameof(TRomaneio.HoraChegada))!.PropertyType);
        Assert.Equal(typeof(TimeOnly?), typeof(TRomaneio).GetProperty(nameof(TRomaneio.InicioCarregamento))!.PropertyType);
        Assert.Equal(typeof(DateOnly?), typeof(TRomaneio).GetProperty(nameof(TRomaneio.DataSaidaCaminhao))!.PropertyType);
    }

    [Fact]
    public void DateOnly_handler_configures_a_PostgreSql_date_parameter()
    {
        var parameter = new NpgsqlParameter();
        var value = new DateOnly(2026, 10, 21);

        new DapperDateOnlyTypeHandler().SetValue(parameter, value);

        Assert.Equal(NpgsqlDbType.Date, parameter.NpgsqlDbType);
        Assert.Equal(value, parameter.Value);
    }

    [Fact]
    public void TimeOnly_handler_configures_a_PostgreSql_time_parameter()
    {
        var parameter = new NpgsqlParameter();
        var value = new TimeOnly(12, 44, 56);

        new DapperTimeOnlyTypeHandler().SetValue(parameter, value);

        Assert.Equal(NpgsqlDbType.Time, parameter.NpgsqlDbType);
        Assert.Equal(value, parameter.Value);
    }

    [Fact]
    public void Insert_keeps_string_key_and_parameterizes_values()
    {
        var item = new TConfCargaGeral { Barcode = "ABC123", DocaOrigem = "D1" };
        var (sql, parameters) = DapperEntityCommands.Insert(item);
        Assert.Contains("expedicao.t_conf_carga_geral", sql);
        Assert.Contains("barcode", sql);
        Assert.DoesNotContain("ABC123", sql);
        Assert.Equal("ABC123", parameters.Get<string>("Barcode"));
    }

    [Fact]
    public void Insert_omits_default_numeric_generated_key()
    {
        var item = new TRomaneio { CodRomaneiro = 0 };
        var (sql, _) = DapperEntityCommands.Insert(item);
        Assert.DoesNotContain("cod_romaneiro", sql);
    }

    [Fact]
    public void Update_uses_key_and_all_scalar_columns()
    {
        var item = new TRomaneio { CodRomaneiro = 42, NomeMotorista = "Teste" };
        var (sql, parameters) = DapperEntityCommands.Update(item);
        Assert.Contains("WHERE cod_romaneiro = @CodRomaneiro", sql);
        Assert.Contains("nome_motorista = @NomeMotorista", sql);
        Assert.Equal(42, parameters.Get<int>("CodRomaneiro"));
    }
}
